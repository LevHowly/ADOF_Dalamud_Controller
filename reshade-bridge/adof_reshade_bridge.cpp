#include <Windows.h>
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>

#include <reshade.hpp>

namespace
{
    constexpr wchar_t PipeName[] = L"\\\\.\\pipe\\adof-reshade";
    constexpr char EffectName[] = "GPose_ADOF.fx";
    constexpr DWORD MaxRequestLength = 4096;

    struct BridgeState
    {
        bool deactivateAdof;
        bool ping;
        bool captureFocus;
        char captureId[64];
        bool activateAdof;
        bool autofocusEnabled;
        double manualFocusDepth;
        double cocBias;
        double cocScale;
        double lensCoefficient;
        double inverseDepthA;
        double inverseDepthB;
        double inverseDepthDenominator;
        double focalLengthMeters;
        double maxBlurRadiusPixels;
        double blurStrength;
        double focusDistanceMeters;
        double apertureFNumber;
        bool blurVisualizerEnabled;
    };

    SRWLOCK StateLock = SRWLOCK_INIT;
    CONDITION_VARIABLE StateChanged = CONDITION_VARIABLE_INIT;
    BridgeState PendingState = {};
    LONG PendingSequence = 0;
    LONG CompletedSequence = 0;
    bool LastOk = false;
    const char *LastError = "No render frame was processed.";
    double LastFocusDistanceMeters = 0;
    volatile LONG StopServer = 0;
    HANDLE ServerThread = nullptr;
    bool HoldDeactivation = false;
    reshade::api::effect_runtime *CaptureRuntime = nullptr;
    LONG CaptureSequence = 0;
    bool CaptureRendered = false;
    char CachedCaptureId[64] = {};
    double CachedFocusDistanceMeters = 0;
    reshade::api::effect_runtime *CachedCaptureRuntime = nullptr;

    void SetFloat(reshade::api::effect_runtime *runtime,
                  reshade::api::effect_uniform_variable uniform, float value)
    {
        runtime->set_uniform_value_float(uniform, &value, 1);
    }

    void SetInt(reshade::api::effect_runtime *runtime,
                reshade::api::effect_uniform_variable uniform, int32_t value)
    {
        runtime->set_uniform_value_int(uniform, &value, 1);
    }

    const char *SkipWhitespace(const char *text)
    {
        while (*text == ' ' || *text == '\t' || *text == '\r' || *text == '\n')
            ++text;
        return text;
    }

    bool ReadJsonString(const char **cursor, char *output, size_t outputSize)
    {
        const char *text = *cursor;
        if (*text++ != '"')
            return false;

        size_t length = 0;
        while (*text != '\0' && *text != '"')
        {
            if (*text == '\\' || static_cast<unsigned char>(*text) < 0x20)
                return false;
            if (length + 1 >= outputSize)
                return false;
            output[length++] = *text++;
        }

        if (*text++ != '"')
            return false;
        output[length] = '\0';
        *cursor = text;
        return true;
    }

    bool GetJsonScalar(const char *json, const char *key, char *output, size_t outputSize)
    {
        const char *cursor = SkipWhitespace(json);
        if (*cursor++ != '{')
            return false;

        for (;;)
        {
            cursor = SkipWhitespace(cursor);
            if (*cursor == '}')
                return false;

            char currentKey[64] = {};
            if (!ReadJsonString(&cursor, currentKey, sizeof(currentKey)))
                return false;

            cursor = SkipWhitespace(cursor);
            if (*cursor++ != ':')
                return false;
            cursor = SkipWhitespace(cursor);

            char value[128] = {};
            if (*cursor == '"')
            {
                if (!ReadJsonString(&cursor, value, sizeof(value)))
                    return false;
            }
            else
            {
                size_t length = 0;
                while (*cursor != '\0' && *cursor != ',' && *cursor != '}' &&
                       *cursor != ' ' && *cursor != '\t' && *cursor != '\r' && *cursor != '\n')
                {
                    if (length + 1 >= sizeof(value))
                        return false;
                    value[length++] = *cursor++;
                }
                if (length == 0)
                    return false;
            }

            if (std::strcmp(currentKey, key) == 0)
            {
                const size_t length = std::strlen(value);
                if (length + 1 > outputSize)
                    return false;
                std::memcpy(output, value, length + 1);
                return true;
            }

            cursor = SkipWhitespace(cursor);
            if (*cursor == '}')
                return false;
            if (*cursor++ != ',')
                return false;
        }
    }

    bool ParseBool(const char *json, const char *key, bool *result)
    {
        char value[16] = {};
        if (!GetJsonScalar(json, key, value, sizeof(value)))
            return false;
        if (std::strcmp(value, "true") == 0)
        {
            *result = true;
            return true;
        }
        if (std::strcmp(value, "false") == 0)
        {
            *result = false;
            return true;
        }
        return false;
    }

    bool ParseNumber(const char *json, const char *key, double *result)
    {
        char value[64] = {};
        if (!GetJsonScalar(json, key, value, sizeof(value)))
            return false;

        char *end = nullptr;
        const double parsed = std::strtod(value, &end);
        if (end == value || *end != '\0' || !std::isfinite(parsed) ||
            !std::isfinite(static_cast<float>(parsed)))
            return false;
        *result = parsed;
        return true;
    }

    bool ParseRequest(const char *json, BridgeState *state)
    {
        char value[32] = {};
        double protocolVersion = 0;
        if (!ParseNumber(json, "protocolVersion", &protocolVersion) ||
            protocolVersion != 4 ||
            !GetJsonScalar(json, "type", value, sizeof(value)))
            return false;

        if (std::strcmp(value, "deactivate_adof") == 0)
        {
            state->deactivateAdof = true;
            return true;
        }
        if (std::strcmp(value, "ping") == 0)
        {
            state->ping = true;
            return true;
        }

        state->captureFocus = std::strcmp(value, "capture_focus") == 0;
        if (state->captureFocus &&
            (!GetJsonScalar(json, "captureId", state->captureId, sizeof(state->captureId)) ||
             std::strlen(state->captureId) != 36))
            return false;
        if ((!state->captureFocus && std::strcmp(value, "set_state") != 0) ||
            !ParseBool(json, "activateAdof", &state->activateAdof) ||
            !ParseBool(json, "autofocusEnabled", &state->autofocusEnabled) ||
            !ParseNumber(json, "manualFocusDepth", &state->manualFocusDepth) ||
            !ParseNumber(json, "cocBias", &state->cocBias) ||
            !ParseNumber(json, "cocScale", &state->cocScale) ||
            !ParseNumber(json, "lensCoefficient", &state->lensCoefficient) ||
            !ParseNumber(json, "inverseDepthA", &state->inverseDepthA) ||
            !ParseNumber(json, "inverseDepthB", &state->inverseDepthB) ||
            !ParseNumber(json, "inverseDepthDenominator", &state->inverseDepthDenominator) ||
            !ParseNumber(json, "focalLengthMeters", &state->focalLengthMeters) ||
            !ParseNumber(json, "maxBlurRadiusPixels", &state->maxBlurRadiusPixels) ||
            !ParseNumber(json, "blurStrength", &state->blurStrength) ||
            !ParseNumber(json, "focusDistanceMeters", &state->focusDistanceMeters) ||
            !ParseNumber(json, "apertureFNumber", &state->apertureFNumber) ||
            !ParseBool(json, "blurVisualizerEnabled", &state->blurVisualizerEnabled))
            return false;

        return (!state->captureFocus || (state->activateAdof && !state->autofocusEnabled)) &&
               state->manualFocusDepth >= 0 && state->manualFocusDepth <= 1 &&
               state->cocBias > 0 && state->cocScale < 0 && state->lensCoefficient > 0 &&
               state->inverseDepthA > 0 &&
               state->inverseDepthDenominator >= 0 &&
               state->inverseDepthA + state->inverseDepthB > 0 &&
               state->focalLengthMeters > 0 && state->focalLengthMeters < 0.1 &&
               state->maxBlurRadiusPixels >= 1 && state->maxBlurRadiusPixels <= 100 &&
               state->blurStrength >= 1 && state->blurStrength <= 32 &&
               state->focusDistanceMeters >= 0.1 && state->focusDistanceMeters <= 1000 &&
               state->apertureFNumber > 0;
    }

    bool ReadRequest(HANDLE pipe, char *buffer, DWORD bufferSize)
    {
        DWORD length = 0;
        const ULONGLONG deadline = GetTickCount64() + 2000;
        while (length + 1 < bufferSize)
        {
            if (GetTickCount64() >= deadline ||
                InterlockedCompareExchange(&StopServer, 0, 0) != 0)
                return false;
            char character = '\0';
            DWORD bytesRead = 0;
            if (!ReadFile(pipe, &character, 1, &bytesRead, nullptr))
            {
                if (GetLastError() == ERROR_NO_DATA)
                {
                    if (InterlockedCompareExchange(&StopServer, 0, 0) != 0)
                        return false;
                    Sleep(10);
                    continue;
                }
                return false;
            }
            if (bytesRead != 1)
                return false;
            if (character == '\n')
                break;
            if (character != '\r')
                buffer[length++] = character;
        }

        if (length + 1 >= bufferSize)
            return false;
        buffer[length] = '\0';
        return length > 0;
    }

    void WriteResponse(HANDLE pipe, bool ok, const char *error, double focusDistanceMeters = 0)
    {
        char response[256] = {};
        if (ok && focusDistanceMeters > 0)
            std::snprintf(response, sizeof(response),
                          "{\"ok\":true,\"protocolVersion\":4,\"focusDistanceMeters\":%.9g}\n", focusDistanceMeters);
        else if (ok)
            std::snprintf(response, sizeof(response), "{\"ok\":true,\"protocolVersion\":4}\n");
        else
            std::snprintf(response, sizeof(response),
                          "{\"ok\":false,\"protocolVersion\":4,\"error\":\"%s\"}\n",
                          error != nullptr ? error : "Bridge could not apply the request.");

        DWORD bytesWritten = 0;
        const DWORD length = static_cast<DWORD>(std::strlen(response));
        if (!WriteFile(pipe, response, length, &bytesWritten, nullptr) || bytesWritten != length)
            reshade::log::message(reshade::log::level::warning, "GPose ADOF: IPC response was not delivered.");
    }

    bool DisableAdof(reshade::api::effect_runtime *runtime)
    {
        bool found = false;
        runtime->enumerate_techniques(nullptr, [&found](reshade::api::effect_runtime *effectRuntime,
                                                       reshade::api::effect_technique technique)
        {
            char name[128] = {};
            effectRuntime->get_technique_name(technique, name);
            if (std::strcmp(name, "GPoseADOF") == 0 ||
                std::strcmp(name, "ADOF") == 0 ||
                std::strcmp(name, "ADOF_DepthOfFieldModified") == 0)
            {
                if (effectRuntime->get_technique_state(technique))
                    effectRuntime->set_technique_state(technique, false);
                found = true;
            }
        });
        return found;
    }

    bool ApplyState(reshade::api::effect_runtime *runtime, const BridgeState &state,
                    const char **error)
    {
        using namespace reshade::api;
        bool ok = true;
        *error = nullptr;

        if (state.ping)
        {
            if (runtime->find_technique(EffectName, "GPoseADOF") == 0 ||
                runtime->find_uniform_variable(EffectName, "bGPose_CaptureFocus") == 0 ||
                runtime->find_uniform_variable(EffectName, "iGPose_DebugView") == 0)
            {
                *error = "GPose ADOF is not loaded or its uniforms are unavailable. Reload effects, disable Performance Mode and check ReShade.log for shader errors.";
                return false;
            }
            return true;
        }
        if (state.deactivateAdof)
        {
            HoldDeactivation = true;
            const bool found = DisableAdof(runtime);
            const auto debugView = runtime->find_uniform_variable(EffectName, "iGPose_DebugView");
            if (debugView != 0)
                SetInt(runtime, debugView, 0);
            const auto captureFocus = runtime->find_uniform_variable(EffectName, "bGPose_CaptureFocus");
            const bool captureDisabled = false;
            if (captureFocus != 0)
                runtime->set_uniform_value_bool(captureFocus, &captureDisabled, 1);
            if (!found)
            {
                *error = "Waiting for GPose ADOF or a legacy ADOF technique to load before confirming deactivation.";
                return false;
            }

            *error = nullptr;
            return true;
        }

        if (!state.activateAdof)
        {
            HoldDeactivation = true;
            DisableAdof(runtime);
        }
        const effect_technique adofTechnique = runtime->find_technique(EffectName, "GPoseADOF");
        const effect_uniform_variable autofocusEnable =
            runtime->find_uniform_variable(EffectName, "bADOF_AutofocusEnable");
        const effect_uniform_variable autofocusSpeed =
            runtime->find_uniform_variable(EffectName, "fADOF_AutofocusSpeed");
        const effect_uniform_variable debugView =
            runtime->find_uniform_variable(EffectName, "iGPose_DebugView");
        const effect_uniform_variable captureFocus =
            runtime->find_uniform_variable(EffectName, "bGPose_CaptureFocus");
        struct UniformValue
        {
            const char *name;
            double value;
            effect_uniform_variable handle = {};
        };
        UniformValue uniforms[] = {
            {"fADOF_ManualfocusDepth", state.manualFocusDepth},
            {"fGPose_CocBias", state.cocBias},
            {"fGPose_CocScale", state.cocScale},
            {"fGPose_LensCoefficient", state.lensCoefficient},
            {"fGPose_InverseDepthA", state.inverseDepthA},
            {"fGPose_InverseDepthB", state.inverseDepthB},
            {"fGPose_InverseDepthDenominator", state.inverseDepthDenominator},
            {"fGPose_FocalLengthMeters", state.focalLengthMeters},
            {"fADOF_ShapeRadius", state.maxBlurRadiusPixels},
            {"fGPose_BlurStrength", state.blurStrength},
            {"fGPose_TestFocusMeters", state.focusDistanceMeters},
            {"fGPose_TestAperture", state.apertureFNumber},
        };
        bool uniformsAvailable = true;
        for (auto &uniform : uniforms)
        {
            uniform.handle = runtime->find_uniform_variable(EffectName, uniform.name);
            uniformsAvailable &= uniform.handle != 0;
        }
        if (adofTechnique == 0 || autofocusEnable == 0 || autofocusSpeed == 0 ||
            debugView == 0 || captureFocus == 0 || !uniformsAvailable)
        {
            *error = "GPose_ADOF.fx and its uniforms must be loaded; disable ReShade Performance Mode.";
            ok = false;
        }
        else
        {
            reshade::log::message(reshade::log::level::info, "GPose ADOF: applying scalar focus uniforms.");
            runtime->set_uniform_value_bool(autofocusEnable, &state.autofocusEnabled, 1);
            runtime->set_uniform_value_bool(captureFocus, &state.captureFocus, 1);
            SetFloat(runtime, autofocusSpeed, 1.0f);
            for (const auto &uniform : uniforms)
                SetFloat(runtime, uniform.handle, static_cast<float>(uniform.value));
            SetInt(runtime, debugView, state.blurVisualizerEnabled && state.activateAdof ? 2 : 0);
            if (state.activateAdof)
            {
                reshade::log::message(reshade::log::level::info, "GPose ADOF: disabling legacy techniques before activation.");
                DisableAdof(runtime);
            }
            HoldDeactivation = !state.activateAdof;
            reshade::log::message(reshade::log::level::info,
                state.activateAdof ? "GPose ADOF: enabling technique." : "GPose ADOF: disabling technique.");
            runtime->set_technique_state(adofTechnique, state.activateAdof);
            if (runtime->get_technique_state(adofTechnique) != state.activateAdof)
            {
                *error = "ReShade did not accept the requested GPose ADOF technique state.";
                ok = false;
            }
        }

        if (!ok && *error == nullptr)
            *error = "The bridge could not apply the requested state.";
        return ok;
    }

    float DecodeHalfFloat(uint16_t bits)
    {
        const int exponent = (bits >> 10) & 31;
        const int fraction = bits & 1023;
        float value = exponent == 0 ? std::ldexp(static_cast<float>(fraction), -24) :
            exponent == 31 ? (fraction == 0 ? INFINITY : NAN) :
            std::ldexp(static_cast<float>(1024 + fraction), exponent - 25);
        return (bits & 0x8000) != 0 ? -value : value;
    }

    bool ReadCapturedDepth(reshade::api::effect_runtime *runtime, float *depth, const char **error)
    {
        using namespace reshade::api;
        auto *device = runtime->get_device();
        if (device->get_api() != device_api::d3d11)
        {
            *error = "Autofocus readback requires Direct3D 11.";
            return false;
        }
        const auto texture = runtime->find_texture_variable(EffectName, "ADOF_FocusTex");
        resource_view view = {};
        if (texture != 0)
            runtime->get_texture_binding(texture, &view, nullptr);
        const resource source = view != 0 ? device->get_resource_from_view(view) : resource{};
        if (source == 0)
        {
            *error = "The shader autofocus texture is unavailable.";
            return false;
        }
        const auto desc = device->get_resource_desc(source);
        const auto viewDesc = device->get_resource_view_desc(view);
        const bool halfFloat = viewDesc.format == format::r16_float &&
            (desc.texture.format == format::r16_float || desc.texture.format == format::r16_typeless);
        const bool fullFloat = viewDesc.format == format::r32_float &&
            (desc.texture.format == format::r32_float || desc.texture.format == format::r32_typeless);
        if (desc.type != resource_type::texture_2d || desc.texture.width != 1 ||
            desc.texture.height != 1 || (!halfFloat && !fullFloat) ||
            desc.texture.samples != 1)
        {
            char diagnostic[256] = {};
            std::snprintf(diagnostic, sizeof(diagnostic),
                "GPose ADOF: unsupported focus texture: type=%u size=%ux%u samples=%u resource_format=%u view_format=%u.",
                static_cast<unsigned>(desc.type), desc.texture.width, desc.texture.height,
                desc.texture.samples, static_cast<unsigned>(desc.texture.format),
                static_cast<unsigned>(viewDesc.format));
            reshade::log::message(reshade::log::level::warning, diagnostic);
            *error = "The shader autofocus texture must be a 1x1 R16F or R32F texture.";
            return false;
        }
        resource staging = {};
        const resource_desc stagingDesc(1, 1, 1, 1, viewDesc.format, 1,
                                        memory_heap::readback, resource_usage::copy_dest);
        if (!device->create_resource(stagingDesc, nullptr, resource_usage::copy_dest, &staging))
        {
            *error = "Could not allocate the autofocus readback texture.";
            return false;
        }
        auto *queue = runtime->get_command_queue();
        auto *commands = queue->get_immediate_command_list();
        commands->barrier(source, resource_usage::shader_resource, resource_usage::copy_source);
        commands->copy_texture_region(source, 0, nullptr, staging, 0, nullptr);
        commands->barrier(source, resource_usage::copy_source, resource_usage::shader_resource);
        // A one-shot 4-byte readback needs GPU completion before mapping or freeing staging.
        queue->flush_immediate_command_list();
        queue->wait_idle();
        subresource_data data = {};
        const bool mapped = device->map_texture_region(staging, 0, nullptr, map_access::read_only, &data);
        const bool valid = mapped && data.data != nullptr;
        if (valid)
        {
            if (halfFloat)
            {
                uint16_t bits = 0;
                std::memcpy(&bits, data.data, sizeof(bits));
                *depth = DecodeHalfFloat(bits);
            }
            else
                std::memcpy(depth, data.data, sizeof(*depth));
        }
        if (mapped)
            device->unmap_texture_region(staging, 0);
        device->destroy_resource(staging);
        if (!valid)
            *error = "Could not map the autofocus readback texture.";
        return valid;
    }

    void CompleteRequest(LONG sequence, bool ok, const char *error, double focusDistanceMeters = 0)
    {
        reshade::log::message(ok ? reshade::log::level::info : reshade::log::level::warning,
            ok ? "GPose ADOF: request completed." : error);
        AcquireSRWLockExclusive(&StateLock);
        LastOk = ok;
        LastError = error;
        LastFocusDistanceMeters = focusDistanceMeters;
        CompletedSequence = sequence;
        WakeAllConditionVariable(&StateChanged);
        ReleaseSRWLockExclusive(&StateLock);
    }

    void CancelCapture()
    {
        if (CaptureRuntime != nullptr)
        {
            const auto uniform = CaptureRuntime->find_uniform_variable(EffectName, "bGPose_CaptureFocus");
            const bool disabled = false;
            if (uniform != 0)
                CaptureRuntime->set_uniform_value_bool(uniform, &disabled, 1);
        }
        CaptureRuntime = nullptr;
        CaptureSequence = 0;
        CaptureRendered = false;
    }

    void OnReshadeReloadedEffects(reshade::api::effect_runtime *runtime)
    {
        if (CaptureRuntime == runtime)
        {
            const LONG sequence = CaptureSequence;
            CancelCapture();
            CompleteRequest(sequence, false, "Effects reloaded during autofocus capture; retrying.");
        }
        if (CachedCaptureRuntime == runtime)
            CachedCaptureRuntime = nullptr;
        if (HoldDeactivation)
            DisableAdof(runtime);
    }

    void OnDestroyEffectRuntime(reshade::api::effect_runtime *runtime)
    {
        OnReshadeReloadedEffects(runtime);
    }

    void OnRenderTechnique(reshade::api::effect_runtime *runtime, reshade::api::effect_technique technique,
                           reshade::api::command_list *, reshade::api::resource_view,
                           reshade::api::resource_view)
    {
        if (CaptureRuntime == runtime && technique == runtime->find_technique(EffectName, "GPoseADOF"))
            CaptureRendered = true;
    }

    void OnReshadePresent(reshade::api::effect_runtime *runtime)
    {
        BridgeState state = {};
        LONG sequence = 0;

        AcquireSRWLockExclusive(&StateLock);
        if (PendingSequence > CompletedSequence)
        {
            state = PendingState;
            sequence = PendingSequence;
        }
        ReleaseSRWLockExclusive(&StateLock);

        if (sequence == 0)
            return;

        if (CaptureSequence != 0 && CaptureSequence != sequence)
            CancelCapture();

        const char *error = nullptr;
        if (state.captureFocus && CaptureSequence == sequence)
        {
            if (CaptureRuntime != runtime || !CaptureRendered)
                return;
            float depth = 0;
            bool ok = ReadCapturedDepth(runtime, &depth, &error);
            double focusMeters = 0;
            if (ok)
            {
                const double inverseDepth = (state.inverseDepthA + state.inverseDepthB * depth) /
                                            (1 + state.inverseDepthDenominator * depth);
                focusMeters = 1 / inverseDepth;
                ok = std::isfinite(depth) && depth >= 0 && depth <= 1 &&
                     std::isfinite(focusMeters) && focusMeters >= 0.0999 && focusMeters <= 1000.1;
                if (!ok)
                    error = "The shader returned an invalid autofocus depth.";
                else
                {
                    focusMeters = std::clamp(focusMeters, 0.1, 1000.0);
                    state.captureFocus = false;
                    state.manualFocusDepth = std::sqrt(depth);
                    state.focusDistanceMeters = focusMeters;
                    state.cocBias = state.lensCoefficient / (focusMeters - state.focalLengthMeters);
                    state.cocScale = -state.cocBias * focusMeters;
                    ok = ApplyState(runtime, state, &error);
                    if (ok)
                    {
                        std::memcpy(CachedCaptureId, state.captureId, sizeof(CachedCaptureId));
                        CachedCaptureRuntime = runtime;
                        CachedFocusDistanceMeters = focusMeters;
                    }
                }
            }
            CancelCapture();
            CompleteRequest(sequence, ok, error, ok ? focusMeters : 0);
            return;
        }
        if (state.captureFocus)
        {
            if (CachedCaptureRuntime == runtime && std::strcmp(CachedCaptureId, state.captureId) == 0)
            {
                CompleteRequest(sequence, true, nullptr, CachedFocusDistanceMeters);
                return;
            }
            const auto overrideUniform = runtime->find_uniform_variable(EffectName, "bGPose_ReShadeOverride");
            bool overrideEnabled = false;
            if (overrideUniform != 0)
                runtime->get_uniform_value_bool(overrideUniform, &overrideEnabled, 1);
            if (overrideUniform == 0 || overrideEnabled)
            {
                CompleteRequest(sequence, false, "Disable Use ReShade focus controls before autofocus capture.");
                return;
            }
        }
        const bool ok = ApplyState(runtime, state, &error);
        if (ok && state.captureFocus)
        {
            CaptureRuntime = runtime;
            CaptureSequence = sequence;
            CaptureRendered = false;
            return;
        }
        if (!ok && state.captureFocus)
        {
            const auto uniform = runtime->find_uniform_variable(EffectName, "bGPose_CaptureFocus");
            const bool disabled = false;
            if (uniform != 0)
                runtime->set_uniform_value_bool(uniform, &disabled, 1);
        }
        CompleteRequest(sequence, ok, error);
    }

    DWORD WINAPI PipeServer(void *)
    {
        while (InterlockedCompareExchange(&StopServer, 0, 0) == 0)
        {
            HANDLE pipe = CreateNamedPipeW(
                PipeName,
                PIPE_ACCESS_DUPLEX,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_NOWAIT,
                1,
                512,
                512,
                0,
                nullptr);
            if (pipe == INVALID_HANDLE_VALUE)
            {
                Sleep(250);
                continue;
            }

            BOOL connected = ConnectNamedPipe(pipe, nullptr);
            if (!connected && GetLastError() == ERROR_PIPE_CONNECTED)
                connected = TRUE;
            while (!connected && InterlockedCompareExchange(&StopServer, 0, 0) == 0)
            {
                if (GetLastError() != ERROR_PIPE_LISTENING)
                    break;
                Sleep(50);
                connected = ConnectNamedPipe(pipe, nullptr);
                if (!connected && GetLastError() == ERROR_PIPE_CONNECTED)
                    connected = TRUE;
            }

            if (connected)
            {
                DWORD mode = PIPE_READMODE_BYTE | PIPE_NOWAIT;
                if (!SetNamedPipeHandleState(pipe, &mode, nullptr, nullptr))
                {
                    WriteResponse(pipe, false, "Could not configure the IPC pipe.");
                    DisconnectNamedPipe(pipe);
                    CloseHandle(pipe);
                    continue;
                }

                char request[MaxRequestLength] = {};
                BridgeState state = {};
                bool ok = ReadRequest(pipe, request, sizeof(request)) &&
                          ParseRequest(request, &state);
                const char *error = "Invalid protocol version, request type, or state values.";
                double focusDistanceMeters = 0;

                if (ok)
                {
                    AcquireSRWLockExclusive(&StateLock);
                    const LONG sequence = InterlockedIncrement(&PendingSequence);
                    PendingState = state;
                    WakeAllConditionVariable(&StateChanged);

                    while (CompletedSequence < sequence &&
                           InterlockedCompareExchange(&StopServer, 0, 0) == 0)
                    {
                        if (!SleepConditionVariableSRW(&StateChanged, &StateLock, 1000, 0))
                            break;
                    }

                    if (CompletedSequence >= sequence)
                    {
                        ok = LastOk;
                        error = LastError;
                        focusDistanceMeters = LastFocusDistanceMeters;
                    }
                    else
                    {
                        ok = false;
                        error = "No ReShade render frame processed the request before timeout.";
                    }
                    ReleaseSRWLockExclusive(&StateLock);
                }

                WriteResponse(pipe, ok, error, focusDistanceMeters);
                DisconnectNamedPipe(pipe);
            }

            CloseHandle(pipe);
        }
        return 0;
    }
}

extern "C" __declspec(dllexport) bool AddonInit(HMODULE addonModule, HMODULE reshadeModule)
{
    if (!reshade::register_addon(addonModule, reshadeModule))
        return false;

    reshade::log::message(reshade::log::level::info, "ADOF ReShade Bridge: registering IPC and effect callbacks.");
    reshade::register_event<reshade::addon_event::reshade_present>(&OnReshadePresent);
    reshade::register_event<reshade::addon_event::reshade_reloaded_effects>(&OnReshadeReloadedEffects);
    reshade::register_event<reshade::addon_event::reshade_render_technique>(&OnRenderTechnique);
    reshade::register_event<reshade::addon_event::destroy_effect_runtime>(&OnDestroyEffectRuntime);
    InterlockedExchange(&StopServer, 0);
    ServerThread = CreateThread(nullptr, 0, &PipeServer, nullptr, 0, nullptr);
    if (ServerThread == nullptr)
    {
        reshade::log::message(reshade::log::level::error, "ADOF ReShade Bridge: could not start the named-pipe server.");
        reshade::unregister_event<reshade::addon_event::reshade_present>(&OnReshadePresent);
        reshade::unregister_event<reshade::addon_event::reshade_reloaded_effects>(&OnReshadeReloadedEffects);
        reshade::unregister_event<reshade::addon_event::reshade_render_technique>(&OnRenderTechnique);
        reshade::unregister_event<reshade::addon_event::destroy_effect_runtime>(&OnDestroyEffectRuntime);
        reshade::unregister_addon(addonModule, reshadeModule);
        return false;
    }

    return true;
}

extern "C" __declspec(dllexport) void AddonUninit(HMODULE addonModule, HMODULE reshadeModule)
{
    InterlockedExchange(&StopServer, 1);
    if (ServerThread != nullptr)
    {
        WakeAllConditionVariable(&StateChanged);
        if (WaitForSingleObject(ServerThread, INFINITE) != WAIT_OBJECT_0)
            reshade::log::message(reshade::log::level::error, "GPose ADOF: could not join the IPC server thread.");
        CloseHandle(ServerThread);
        ServerThread = nullptr;
    }

    reshade::unregister_event<reshade::addon_event::reshade_present>(&OnReshadePresent);
    reshade::unregister_event<reshade::addon_event::reshade_reloaded_effects>(&OnReshadeReloadedEffects);
    reshade::unregister_event<reshade::addon_event::reshade_render_technique>(&OnRenderTechnique);
    reshade::unregister_event<reshade::addon_event::destroy_effect_runtime>(&OnDestroyEffectRuntime);
    reshade::unregister_addon(addonModule, reshadeModule);
}

extern "C" __declspec(dllexport) const char *NAME = "ADOF ReShade Bridge";
extern "C" __declspec(dllexport) const char *DESCRIPTION =
    "Receives ADOF controller state over a local named pipe and applies it to ReShade effects.";
