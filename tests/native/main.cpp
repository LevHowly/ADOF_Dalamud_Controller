#include "../../reshade-bridge/adof_reshade_bridge.cpp"
#include <cassert>

int main()
{
    assert(DecodeHalfFloat(0) == 0);
    assert(DecodeHalfFloat(0x3C00) == 1);
    assert(DecodeHalfFloat(0x3800) == 0.5f);
    assert(DecodeHalfFloat(1) == std::ldexp(1.0f, -24));
    assert(DecodeHalfFloat(0xBC00) == -1);
    assert(std::isinf(DecodeHalfFloat(0x7C00)));
    assert(std::isnan(DecodeHalfFloat(0x7E00)));
    BridgeState request = {};
    assert(ParseRequest("{\"protocolVersion\":4,\"type\":\"deactivate_adof\"}", &request));
    assert(request.deactivateAdof);
    assert(!ParseRequest("{\"protocolVersion\":3,\"type\":\"deactivate_adof\"}", &request));
    assert(!ParseRequest("{\"protocolVersion\":4,\"type\":\"set_state\"}", &request));
    assert(!ParseRequest("{\"protocolVersion\":4,\"type\":\"capture_focus\",\"captureId\":\"missing\"}", &request));
    const char *captureJson =
        "{\"protocolVersion\":4,\"type\":\"capture_focus\","
        "\"captureId\":\"12345678-1234-1234-1234-123456789012\","
        "\"activateAdof\":true,\"autofocusEnabled\":false,"
        "\"manualFocusDepth\":0.1,\"cocBias\":3,\"cocScale\":-15,"
        "\"lensCoefficient\":14.88,\"inverseDepthA\":10,\"inverseDepthB\":-9,"
        "\"inverseDepthDenominator\":999,\"focalLengthMeters\":0.05,"
        "\"maxBlurRadiusPixels\":24,\"blurStrength\":8,\"focusDistanceMeters\":5,"
        "\"apertureFNumber\":2.8,\"blurVisualizerEnabled\":true}";
    request = {};
    assert(ParseRequest(captureJson, &request));
    assert(request.captureFocus && !request.autofocusEnabled && request.blurVisualizerEnabled);
    request = {};
    assert(ParseRequest("{\"protocolVersion\":4,\"type\":\"ping\"}", &request) && request.ping);

    reshade::api::effect_runtime runtime;
    runtime.techniques = {
        {"GPose_ADOF.fx", "GPoseADOF", true},
        {"renamed-stock.fx", "ADOF", true},
        {"ADOF-depth-of-field-modified.fx", "ADOF_DepthOfFieldModified", true},
        {"other.fx", "OtherEffect", true},
    };
    const char *error = nullptr;
    assert(!ApplyState(&runtime, request, &error));
    BridgeState disable = {};
    disable.deactivateAdof = true;
    assert(ApplyState(&runtime, disable, &error));
    assert(HoldDeactivation);
    for (size_t i = 0; i < 3; ++i)
        assert(!runtime.techniques[i].enabled);
    assert(runtime.techniques[3].enabled);

    runtime.techniques[0].enabled = true;
    runtime.techniques[1].enabled = true;
    OnReshadePresent(&runtime);
    assert(runtime.techniques[0].enabled && runtime.techniques[1].enabled);
    OnReshadeReloadedEffects(&runtime);
    assert(!runtime.techniques[0].enabled && !runtime.techniques[1].enabled);

    reshade::api::effect_runtime empty;
    assert(!ApplyState(&empty, disable, &error));
    assert(error != nullptr && HoldDeactivation);
    empty.techniques.push_back({"GPose_ADOF.fx", "GPoseADOF", true});
    OnReshadeReloadedEffects(&empty);
    assert(!empty.techniques[0].enabled);

    const char *names[] = {
        "bADOF_AutofocusEnable", "fADOF_AutofocusSpeed", "fADOF_ManualfocusDepth",
        "fGPose_CocBias", "fGPose_CocScale", "fGPose_LensCoefficient",
        "fGPose_InverseDepthA", "fGPose_InverseDepthB", "fGPose_InverseDepthDenominator",
        "fGPose_FocalLengthMeters", "fADOF_ShapeRadius",
        "fGPose_BlurStrength", "fGPose_TestFocusMeters", "fGPose_TestAperture",
        "iGPose_DebugView",
        "bGPose_CaptureFocus", "bGPose_ReShadeOverride",
    };
    for (const char *name : names)
        runtime.uniforms[std::string(EffectName) + ":" + name] = runtime.uniforms.size() + 1;
    const bool deactivationBeforePing = HoldDeactivation;
    assert(ApplyState(&runtime, request, &error));
    assert(HoldDeactivation == deactivationBeforePing && runtime.values.empty());
    BridgeState activate = {};
    activate.activateAdof = true;
    activate.cocBias = 3;
    activate.cocScale = -15;
    activate.maxBlurRadiusPixels = 24;
    activate.blurStrength = 8;
    activate.focusDistanceMeters = 5;
    activate.apertureFNumber = 2.8;
    activate.autofocusEnabled = true;
    activate.blurVisualizerEnabled = true;
    assert(ApplyState(&runtime, activate, &error));
    assert(!HoldDeactivation && runtime.techniques[0].enabled);
    assert(!runtime.techniques[1].enabled && !runtime.techniques[2].enabled);
    assert(runtime.values[runtime.find_uniform_variable(EffectName, "bADOF_AutofocusEnable")] == 1);
    assert(runtime.values[runtime.find_uniform_variable(EffectName, "iGPose_DebugView")] == 2);
    assert(runtime.values[runtime.find_uniform_variable(EffectName, "fGPose_CocScale")] == -15);
    activate.blurVisualizerEnabled = false;
    assert(ApplyState(&runtime, activate, &error));
    assert(runtime.techniques[0].enabled);
    assert(runtime.values[runtime.find_uniform_variable(EffectName, "iGPose_DebugView")] == 0);

    activate.autofocusEnabled = false;
    activate.captureFocus = true;
    strcpy_s(activate.captureId, "12345678-1234-1234-1234-123456789012");
    activate.lensCoefficient = 14.880952;
    activate.focalLengthMeters = 0.05;
    activate.inverseDepthA = 10;
    activate.inverseDepthB = -9;
    activate.inverseDepthDenominator = 999;
    runtime.gpu.sourceDesc.texture.format = reshade::api::format::r32_typeless;
    const auto captureUniform = runtime.find_uniform_variable(EffectName, "bGPose_CaptureFocus");
    auto submit = [&](const BridgeState &state)
    {
        PendingState = state;
        ++PendingSequence;
        OnReshadePresent(&runtime);
    };
    submit(activate);
    assert(CaptureSequence == PendingSequence && CompletedSequence < PendingSequence);
    assert(runtime.values[captureUniform] == 1);
    OnReshadePresent(&runtime);
    assert(CompletedSequence < PendingSequence && runtime.gpu.allocations == 0);
    runtime.gpu.depth = 0.0004f;
    OnRenderTechnique(&runtime, runtime.find_technique(EffectName, "GPoseADOF"), nullptr, 0, 0);
    OnReshadePresent(&runtime);
    assert(LastOk && CaptureSequence == 0 && CompletedSequence == PendingSequence);
    const double expectedFocus = (1 + 999 * double(runtime.gpu.depth)) /
                                 (10 - 9 * double(runtime.gpu.depth));
    assert(std::abs(LastFocusDistanceMeters - expectedFocus) < 1e-9);
    assert(runtime.values[captureUniform] == 0 && !HoldDeactivation && runtime.techniques[0].enabled);
    const double expectedBias = activate.lensCoefficient / (expectedFocus - activate.focalLengthMeters);
    assert(std::abs(runtime.values[runtime.find_uniform_variable(EffectName, "fGPose_CocBias")] -
                    expectedBias) < 0.001);
    assert(std::abs(runtime.values[runtime.find_uniform_variable(EffectName, "fGPose_CocScale")] +
                    expectedBias * expectedFocus) < 0.001);
    assert(runtime.gpu.allocations == 1 && runtime.gpu.destructions == 1 && runtime.gpu.unmaps == 1);
    runtime.gpu.depth = 0.5f;
    submit(activate);
    assert(LastOk && LastFocusDistanceMeters == expectedFocus && runtime.gpu.allocations == 1);

    strcpy_s(activate.captureId, "12345678-1234-1234-1234-123456789013");
    submit(activate);
    submit(disable);
    assert(CaptureSequence == 0 && runtime.values[captureUniform] == 0);
    assert(HoldDeactivation && !runtime.techniques[0].enabled);
    submit(activate);
    OnReshadeReloadedEffects(&runtime);
    assert(!LastOk && CaptureSequence == 0 && runtime.values[captureUniform] == 0);
    submit(activate);
    auto automatic = activate;
    automatic.captureFocus = false;
    automatic.autofocusEnabled = true;
    submit(automatic);
    assert(LastOk && CaptureSequence == 0 && runtime.values[captureUniform] == 0);
    assert(runtime.values[runtime.find_uniform_variable(EffectName, "bADOF_AutofocusEnable")] == 1);
    submit(activate);
    runtime.gpu.map = false;
    OnRenderTechnique(&runtime, runtime.find_technique(EffectName, "GPoseADOF"), nullptr, 0, 0);
    OnReshadePresent(&runtime);
    assert(!LastOk && CaptureSequence == 0 && runtime.values[captureUniform] == 0);
    assert(runtime.gpu.allocations == runtime.gpu.destructions);
    runtime.gpu.map = true;
    runtime.gpu.depth = std::nanf("");
    submit(activate);
    OnRenderTechnique(&runtime, runtime.find_technique(EffectName, "GPoseADOF"), nullptr, 0, 0);
    OnReshadePresent(&runtime);
    assert(!LastOk && runtime.values[captureUniform] == 0);
    runtime.values[runtime.find_uniform_variable(EffectName, "bGPose_ReShadeOverride")] = 1;
    submit(activate);
    assert(!LastOk && CaptureSequence == 0);
    runtime.values[runtime.find_uniform_variable(EffectName, "bGPose_ReShadeOverride")] = 0;
    runtime.gpu.api = reshade::api::device_api::other;
    float depth = 0;
    assert(!ReadCapturedDepth(&runtime, &depth, &error));
    runtime.gpu.api = reshade::api::device_api::d3d11;
    runtime.gpu.sourceViewDesc.format = reshade::api::format::other;
    assert(!ReadCapturedDepth(&runtime, &depth, &error));
    runtime.gpu.sourceViewDesc.format = reshade::api::format::r32_float;
    runtime.gpu.sourceDesc.texture.width = 2;
    assert(!ReadCapturedDepth(&runtime, &depth, &error));
    runtime.gpu.sourceDesc.texture.width = 1;
    runtime.gpu.sourceDesc.texture.format = reshade::api::format::r16_float;
    runtime.gpu.sourceViewDesc.format = reshade::api::format::r16_float;
    runtime.gpu.halfDepth = 0x3800;
    assert(ReadCapturedDepth(&runtime, &depth, &error) && depth == 0.5f);
    runtime.gpu.sourceDesc.texture.format = reshade::api::format::r16_typeless;
    assert(ReadCapturedDepth(&runtime, &depth, &error) && depth == 0.5f);
    strcpy_s(activate.captureId, "12345678-1234-1234-1234-123456789014");
    submit(activate);
    OnRenderTechnique(&runtime, runtime.find_technique(EffectName, "GPoseADOF"), nullptr, 0, 0);
    OnReshadePresent(&runtime);
    assert(LastOk && CaptureSequence == 0 && runtime.values[captureUniform] == 0);
    assert(std::abs(LastFocusDistanceMeters - 91.0) < 1e-9);
    assert(std::abs(runtime.values[runtime.find_uniform_variable(EffectName, "fGPose_TestFocusMeters")] -
                    LastFocusDistanceMeters) < 1e-6);
    runtime.hasTexture = false;
    assert(!ReadCapturedDepth(&runtime, &depth, &error));
    runtime.hasTexture = true;
    runtime.gpu.allocate = false;
    assert(!ReadCapturedDepth(&runtime, &depth, &error));

    runtime.uniforms.erase(std::string(EffectName) + ":fGPose_CocBias");
    assert(ApplyState(&runtime, disable, &error));
    assert(runtime.values[runtime.find_uniform_variable(EffectName, "iGPose_DebugView")] == 0);
    assert(!ApplyState(&runtime, activate, &error));
    assert(HoldDeactivation && !runtime.techniques[0].enabled);
    std::puts("PASS: native deactivation, activation, autofocus capture/readback, retry cache, cancellation and failures.");
}
