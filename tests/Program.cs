using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ADOF_Dalamud_Controller;

await RegressionTests.Run();

static class RegressionTests
{
    public static async Task Run()
    {
        TestMath();
        NativeDepthOfFieldTests.Run();
        await TestLatestStateAndExit();
        await TestRejectedExitRetries();
        await TestReconnectAndSupersedingExit();
        await TestFocusCapture();
        await TestSupersededFocusCapture();
        await TestDisposal();
        await TestOnboarding();
        await TestGShadeOnboarding();
        await TestSetupProbe();
        TestBundledResources();
        Console.WriteLine("PASS: photographic math, protocol v4, autofocus, delivery/retries, disposal, onboarding install/update, IPC diagnostics and bundled assets.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Close(double actual, double expected, double tolerance, string message) =>
        Check(Math.Abs(actual - expected) <= tolerance, $"{message}: {actual} != {expected}");

    private static void TestMath()
    {
        foreach (var aperture in new[] { 0.1, 0.5, 2.8, 8, 32 })
        foreach (var focus in new[] { 0.1, 0.5, 1, 5, 50 })
        {
            Check(DepthOfFieldMath.TryFromCamera(aperture, focus, out var near, out var far), "Valid camera rejected");
            Check(DepthOfFieldMath.TryGetShaderParameters(aperture, focus, 24, out var p), "Shader parameters rejected");
            foreach (var distance in new[] { 0.1, 1, 5, 50, 1000 })
            {
                var depth = DepthOfFieldMath.ToLinearDepth(distance);
                var inverse = (p.InverseDepthA + p.InverseDepthB * depth) /
                              (1 + p.InverseDepthDenominator * depth);
                Close(inverse, 1 / distance, 1e-9, "Inverse depth conversion");
            }
            Close(p.CocBias + p.CocScale / focus, 0, 0.002, "Focus must have zero radius within float precision");
            Close(Math.Abs(p.CocBias + p.CocScale / near), 0.5, 0.002, "Near boundary radius");
            if (double.IsFinite(far))
            {
                Close(Math.Abs(p.CocBias + p.CocScale / far), 0.5, 0.002, "Far boundary radius");
                Check(DepthOfFieldMath.TryFromDepthLimits(near, far, out var inverseAperture, out var inverseFocus),
                    "Inverse calculation rejected");
                Close(inverseAperture, aperture, aperture * 1e-9, "Aperture round trip");
                Close(inverseFocus, focus, focus * 1e-9, "Focus round trip");
            }
            Check(DepthOfFieldMath.TryGetShaderParameters(aperture, focus, 100, out var larger), "Larger radius rejected");
            Check(p == larger, "Maximum blur must not change optical coefficients or the acceptable band");
        }
        Check(DepthOfFieldMath.TryFromCamera(32, 5, out var infiniteNear, out var infiniteFar) &&
              double.IsPositiveInfinity(infiniteFar), "Hyperfocal far limit must be infinity");
        Check(DepthOfFieldMath.TryFromDepthLimits(infiniteNear, double.PositiveInfinity, out _, out _),
            "Infinite far limit inverse rejected");
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -1, 0, 0.05 })
            Check(!DepthOfFieldMath.TryGetShaderParameters(2.8, invalid, 24, out _), "Invalid focus accepted");
        Check(!DepthOfFieldMath.TryFromDepthLimits(5, 4, out _, out _), "Reversed band accepted");
        Check(!DepthOfFieldMath.TryGetShaderParameters(0, 5, 24, out _), "Invalid aperture accepted");
        Check(!DepthOfFieldMath.TryGetShaderParameters(2.8, 5, 0, out _), "Invalid blur radius accepted");
        Close(DepthOfFieldMath.ToLinearDepth(double.PositiveInfinity), 1, 1e-10, "Infinity visualizer depth");
        foreach (var strength in new[] { 1.0, 8.0, 32.0 })
        foreach (var radius in new[] { -0.5, -0.25, 0, 0.25, 0.5 })
            Close(DepthOfFieldMath.BoostBlurRadius(radius, strength, 24), 0, 1e-12,
                "One-pixel reduction must remove blur inside the acceptable band");
        foreach (var sign in new[] { -1.0, 1.0 })
        {
            Close(DepthOfFieldMath.BoostBlurRadius(sign * 0.5625, 8, 24), 0, 1e-12,
                "Expanded zero-blur boundary");
            Close(DepthOfFieldMath.BoostBlurRadius(sign * 0.625, 8, 24), sign * 0.5, 1e-12,
                "Blur beyond expanded zero-blur boundary");
            Close(DepthOfFieldMath.BoostBlurRadius(sign * 1.0, 1, 24), 0, 1e-12,
                "Subtraction must clamp at zero without reversing near/far sign");
        }
        Close(DepthOfFieldMath.BoostBlurRadius(3, 8, 24), 19.5, 1e-12, "Far blur strength");
        Close(DepthOfFieldMath.BoostBlurRadius(-3, 8, 24), -19.5, 1e-12, "Near blur strength");
        Close(DepthOfFieldMath.BoostBlurRadius(5, 8, 24), 24, 1e-12, "Maximum radius cap");
        Close(DepthOfFieldMath.BoostBlurRadius(3.5625, 8, 24), 24, 1e-12,
            "Reduction must happen before the maximum radius cap");
        Close(DepthOfFieldMath.BoostBlurRadius(3, 1, 24), 2, 1e-12, "Unboosted radius reduction");
    }

    private static NamedPipeServerStream Server() => new(
        ReShadeIpcClient.PipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private static async Task<JsonElement> ReadRequest(NamedPipeServerStream server)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await server.WaitForConnectionAsync(timeout.Token);
        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
        var line = await reader.ReadLineAsync(timeout.Token);
        Check(line is not null, "Client did not send a request");
        using var json = JsonDocument.Parse(line!);
        return json.RootElement.Clone();
    }

    private static async Task Respond(NamedPipeServerStream server, bool ok = true, float? focusMeters = null)
    {
        await using (var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true))
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                ok,
                error = ok ? null : "Shader is loading",
                focusDistanceMeters = focusMeters,
            }));
            await writer.FlushAsync();
        }
        server.Disconnect();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(10);
        Check(condition(), "Timed out waiting for client state");
    }

    private static async Task TestLatestStateAndExit()
    {
        using var client = new ReShadeIpcClient();
        await using var server = Server();
        var config = new Configuration { ActivateAdof = true };
        client.SendState(config);
        var first = await ReadRequest(server);
        Check(first.GetProperty("protocolVersion").GetInt32() == 4, "Wrong protocol");
        Check(!first.GetProperty("blurVisualizerEnabled").GetBoolean(), "Unexpected visualizer at startup");
        Check(first.TryGetProperty("cocBias", out _) && first.GetProperty("blurStrength").GetSingle() == 8,
            "Bridge should receive calculated uniforms");
        for (var i = 0; i < 200; i++)
        {
            config.FocusDistanceMeters = 1 + i * 0.1f;
            client.SendState(config);
        }
        client.DeactivateAdof();
        await Respond(server);
        var exit = await ReadRequest(server);
        Check(exit.GetProperty("type").GetString() == "deactivate_adof",
            "Exit must supersede queued slider updates");
        Check(!exit.TryGetProperty("cocBias", out _), "Disable must not depend on valid settings");
        await Respond(server);
        await WaitUntil(() => client.Status == "Connected to GPose ADOF bridge");
    }

    private static async Task TestRejectedExitRetries()
    {
        using var client = new ReShadeIpcClient();
        await using var server = Server();
        client.DeactivateAdof();
        for (var i = 0; i < 4; i++)
        {
            var request = await ReadRequest(server);
            Check(request.GetProperty("type").GetString() == "deactivate_adof", "Exit retry changed command");
            await Respond(server, false);
        }
        var retry = await ReadRequest(server);
        Check(retry.GetProperty("type").GetString() == "deactivate_adof", "Exit stopped after three failures");
        await Respond(server);
    }

    private static async Task TestReconnectAndSupersedingExit()
    {
        using var client = new ReShadeIpcClient();
        client.DeactivateAdof();
        await WaitUntil(() => client.Status.Contains("retrying latest request"));
        await using var server = Server();
        var exit = await ReadRequest(server);
        Check(exit.GetProperty("type").GetString() == "deactivate_adof", "Reconnect lost exit");
        var config = new Configuration { ActivateAdof = true, AutofocusEnabled = true, BlurVisualizerEnabled = true };
        client.SendState(config);
        await Respond(server, false);
        var activated = await ReadRequest(server);
        Check(activated.GetProperty("activateAdof").GetBoolean(), "New activation must supersede old exit");
        Check(activated.GetProperty("autofocusEnabled").GetBoolean(), "Autofocus not wired");
        Check(activated.GetProperty("blurVisualizerEnabled").GetBoolean(), "Visualizer must work with autofocus");
        await Respond(server);
        config.BlurVisualizerEnabled = false;
        client.SendState(config);
        var normal = await ReadRequest(server);
        Check(normal.GetProperty("activateAdof").GetBoolean() &&
              !normal.GetProperty("blurVisualizerEnabled").GetBoolean(),
            "Turning visualizer off must preserve ADOF activation");
        await Respond(server);
    }

    private static async Task TestFocusCapture()
    {
        using var client = new ReShadeIpcClient();
        await using var server = Server();
        var config = new Configuration { ActivateAdof = true, BlurVisualizerEnabled = true };
        client.CaptureFocus(config);
        Check(client.IsFocusCapturePending, "Capture must be marked pending");
        var request = await ReadRequest(server);
        Check(request.GetProperty("type").GetString() == "capture_focus", "Missing capture command");
        Check(!request.GetProperty("autofocusEnabled").GetBoolean(), "Capture must preserve manual mode");
        Check(request.GetProperty("blurVisualizerEnabled").GetBoolean(), "Capture lost visualizer");
        Check(request.GetProperty("apertureFNumber").GetSingle() == config.ApertureFNumber,
            "Capture must preserve aperture");
        var id = request.GetProperty("captureId").GetString();
        Check(Guid.TryParse(id, out _), "Capture needs a unique retry-safe ID");
        foreach (var invalidFocus in new float?[] { null, -1, 0, 0.05f, 1001 })
        {
            await Respond(server, focusMeters: invalidFocus);
            var restore = await ReadRequest(server);
            Check(restore.GetProperty("type").GetString() == "set_state" &&
                  !restore.GetProperty("autofocusEnabled").GetBoolean(),
                "Failed capture must restore manual settings rather than repeat sampling");
            Check(!client.IsFocusCapturePending && client.FocusCaptureError is not null,
                "Failed capture must stop and display its error");
            Check(!client.TryTakeCapturedFocus(out _), "Invalid or missing capture result accepted");
            await Respond(server);
            client.CaptureFocus(config);
            request = await ReadRequest(server);
            Check(request.GetProperty("captureId").GetString() != id, "New button press needs a new capture ID");
            id = request.GetProperty("captureId").GetString();
        }
        server.Disconnect();
        request = await ReadRequest(server);
        Check(request.GetProperty("captureId").GetString() == id, "Transport retry must retain capture ID");
        await Respond(server, focusMeters: 12.5f);
        await WaitUntil(() => !client.IsFocusCapturePending);
        Check(client.TryTakeCapturedFocus(out var focus) && focus == 12.5f, "Capture distance not returned");
        Check(!client.TryTakeCapturedFocus(out _), "Capture must be consumed only once");
        Check(DepthOfFieldMath.TryGetShaderParameters(config.ApertureFNumber, focus,
                config.MaxBlurRadiusPixels, out var p), "Captured distance not usable for manual focus");
        var autofocusBias = p.LensCoefficient / (focus - DepthOfFieldMath.FocalLengthMeters);
        Close(p.CocBias, autofocusBias, 1e-6, "Manual bias must match GPU autofocus bias");
        Close(p.CocScale, -autofocusBias * focus, 1e-5, "Manual scale must match GPU autofocus scale");
        client.CaptureFocus(config);
        await ReadRequest(server);
        client.SendState(config);
        await Respond(server, focusMeters: 900);
        var normal = await ReadRequest(server);
        Check(normal.GetProperty("type").GetString() == "set_state", "Manual edits must supersede capture");
        Check(!client.TryTakeCapturedFocus(out _), "Stale capture overwrote newer manual settings");
        await Respond(server);
        client.CaptureFocus(config);
        await ReadRequest(server);
        config.AutofocusEnabled = true;
        client.SendState(config);
        await Respond(server, focusMeters: 7);
        var automatic = await ReadRequest(server);
        Check(automatic.GetProperty("autofocusEnabled").GetBoolean() &&
              automatic.GetProperty("type").GetString() == "set_state",
            "Switching to automatic mode must supersede capture");
        Check(!client.TryTakeCapturedFocus(out _), "Switching mode must discard late capture");
        await Respond(server);
        config.AutofocusEnabled = false;
        client.CaptureFocus(config);
        await ReadRequest(server);
        await Respond(server, false);
        var rejectedRestore = await ReadRequest(server);
        Check(rejectedRestore.GetProperty("type").GetString() == "set_state" &&
              !client.IsFocusCapturePending && client.FocusCaptureError == "Shader is loading",
            "Explicit capture rejection must restore manual mode and surface error");
        await Respond(server);
    }

    private static async Task TestSupersededFocusCapture()
    {
        using var client = new ReShadeIpcClient();
        await using var server = Server();
        var config = new Configuration { ActivateAdof = true };
        client.CaptureFocus(config);
        await ReadRequest(server);
        client.DeactivateAdof();
        Check(!client.IsFocusCapturePending, "GPose exit must cancel pending capture");
        await Respond(server, focusMeters: 2);
        var exit = await ReadRequest(server);
        Check(exit.GetProperty("type").GetString() == "deactivate_adof", "Capture must not reactivate on exit");
        Check(!client.TryTakeCapturedFocus(out _), "GPose exit must discard late capture result");
        await Respond(server);
    }

    private static async Task TestOnboarding()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gpose-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var client = new ReShadeIpcClient();
            var errors = new List<Exception>();
            var backups = Path.Combine(directory, "config", "backups");
            Stream Resource(string name) => new MemoryStream(Encoding.UTF8.GetBytes(name));
            var setup = new Onboarding(directory, backups, client, errors.Add, Resource, (_, _) => true);
            async Task Refresh()
            {
                setup.Refresh();
                await setup.Operation!;
            }
            await Refresh();
            Check(setup.State == SetupState.Error, "Invalid Dalamud game directory must fail");
            File.WriteAllText(Path.Combine(directory, "ffxiv_dx11.exe"), "");
            await Refresh();
            Check(setup.State == SetupState.Error && setup.Message.Contains("was not found"),
                "Missing ReShade must show an error, not install controls");
            File.WriteAllText(Path.Combine(directory, "d3d11.dll"), "");
            File.WriteAllText(Path.Combine(directory, "ReShade.ini"), "[GENERAL]\nEffectSearchPaths=.\\Shaders\\**");
            await Refresh();
            Check(setup.State == SetupState.Error && setup.Error!.Contains("qUINT_common"),
                "Missing shader includes must show a prerequisite error");
            var shaders = Path.Combine(directory, "Shaders", "qUINT");
            Directory.CreateDirectory(shaders);
            File.WriteAllText(Path.Combine(shaders, "qUINT_common.fxh"), "");
            File.WriteAllText(Path.Combine(shaders, "ReShade.fxh"), "");
            var ini = "[ADDON]\nAddonPath=.\\CustomAddons\n[GENERAL]\nEffectSearchPaths=.\\Unused,.\\Shaders\\**\nPerformanceMode=0";
            File.WriteAllText(Path.Combine(directory, "ReShade.ini"), ini);
            await Refresh();
            Check(setup.State == SetupState.InstallRequired, "ReShade-only setup should offer installation");
            Check(setup.Message.Contains("CustomAddons") && setup.Message.Contains(shaders),
                "Configured add-on and recursive effect paths must be honored");
            setup.Install();
            await setup.Operation!;
            Check(setup.State == SetupState.RestartRequired, "Installation must require restart");
            var addon = Path.Combine(directory, "CustomAddons", "adof_reshade_bridge.addon64");
            var shader = Path.Combine(shaders, "GPose_ADOF.fx");
            Check(File.ReadAllText(addon) == "GPoseADOF.Addon" &&
                  File.ReadAllText(shader) == "GPoseADOF.Shader", "Both bundled files must be installed exactly");
            Check(File.ReadAllText(Path.Combine(directory, "ReShade.ini")) == ini,
                "Installer must not edit ReShade configuration");
            await Refresh();
            Check(setup.State == SetupState.RestartRequired, "Recheck cannot bypass required restart in same session");
            File.WriteAllText(addon, "old-addon");
            File.WriteAllText(shader, "old-shader");
            var upgrade = new Onboarding(directory, backups, client, errors.Add, Resource, (_, _) => true);
            upgrade.Refresh();
            await upgrade.Operation!;
            Check(upgrade.State == SetupState.InstallRequired, "Mismatched installed assets need update");
            upgrade.Install();
            await upgrade.Operation!;
            Check(upgrade.State == SetupState.RestartRequired &&
                  Directory.GetFiles(backups).Length == 2, "Updates must preserve both existing files");
            Check(Directory.GetFiles(backups).Select(File.ReadAllText).Order().SequenceEqual(
                new[] { "old-addon", "old-shader" }), "Backup content must be preserved");
            File.WriteAllText(shader, "needs-update");
            var installationFailure = new Onboarding(directory, backups, client, errors.Add,
                name => name == "GPoseADOF.Shader" ? throw new IOException("Test resource read failure") : Resource(name),
                (_, _) => true);
            installationFailure.Refresh();
            await installationFailure.Operation!;
            Check(installationFailure.State == SetupState.Error && errors.Count > 0,
                "Resource access failures must be surfaced and logged");
            // Fail during the copy rather than the initial hash comparison.
            var failCopy = false;
            var failedInstall = new Onboarding(directory, backups, client, errors.Add,
                name => failCopy && name == "GPoseADOF.Shader" ?
                    throw new IOException("Test copy failure") : Resource(name), (_, _) => true);
            failedInstall.Refresh();
            await failedInstall.Operation!;
            failCopy = true;
            failedInstall.Install();
            await failedInstall.Operation!;
            Check(failedInstall.State == SetupState.Error && File.ReadAllText(shader) == "needs-update",
                "Failed installation must not report success or destroy the existing shader");
            File.WriteAllText(shader, "GPoseADOF.Shader");
            File.WriteAllText(Path.Combine(directory, "ReShade.ini"), ini.Replace("PerformanceMode=0", "PerformanceMode=1"));
            var performance = new Onboarding(directory, backups, client, errors.Add, Resource, (_, _) => true);
            performance.Refresh();
            await performance.Operation!;
            Check(performance.State == SetupState.Error && performance.Error!.Contains("Performance Mode"),
                "Performance Mode must show actionable error");
            File.WriteAllText(Path.Combine(directory, "ReShade.ini"), ini.Replace("[ADDON]",
                "[ADDON]\nDisabledAddons=ADOF ReShade Bridge@adof_reshade_bridge.addon64"));
            performance.Refresh();
            await performance.Operation!;
            Check(performance.State == SetupState.Error && performance.Error!.Contains("disabled"),
                "Disabled add-on must show actionable error");
            File.WriteAllText(Path.Combine(directory, "ReShade.ini"), ini);
            var ready = new Onboarding(directory, backups, client, errors.Add, Resource, (_, _) => true);
            await using var server = Server();
            ready.Refresh();
            var request = await ReadRequest(server);
            Check(request.GetProperty("type").GetString() == "ping", "Setup must probe without changing effects");
            await WriteProbeResponse(server, "{\"ok\":true,\"protocolVersion\":4}");
            await ready.Operation!;
            Check(ready.State == SetupState.Ready, "Verified files and live compatible IPC must unlock controls");
            ready.Refresh();
            await ReadRequest(server);
            await WriteProbeResponse(server, "{\"ok\":false,\"protocolVersion\":4,\"error\":\"Shader not loaded\"}");
            await ready.Operation!;
            Check(ready.State == SetupState.Error && ready.Message.Contains("Shader not loaded"),
                "Installed but non-ready IPC must surface error");
            ready.Refresh();
            await ReadRequest(server);
            await WriteProbeResponse(server, "{\"ok\":true,\"protocolVersion\":4}");
            await ready.Operation!;
            Check(ready.State == SetupState.Ready,
                "A later GPose-triggered refresh must recover from startup IPC/shader readiness errors");
            Check(!Directory.GetFiles(Path.Combine(directory, "CustomAddons")).Any(path => path.Contains("backup")),
                "No backups may be left in add-on directory");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task TestGShadeOnboarding()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gpose-gshade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var client = new ReShadeIpcClient();
            Stream Resource(string name) => new MemoryStream(Encoding.UTF8.GetBytes(name));
            var setup = new Onboarding(directory, Path.Combine(directory, "backups"), client, _ => { },
                Resource, (path, name) => File.ReadAllText(path) == name);
            File.WriteAllText(Path.Combine(directory, "ffxiv_dx11.exe"), "");
            var dll = Path.Combine(directory, "dxgi.dll");
            File.WriteAllText(dll, "GShade");
            var shaders = Path.Combine(directory, "gshade-shaders", "Shaders");
            Directory.CreateDirectory(shaders);
            File.WriteAllText(Path.Combine(shaders, "qUINT_common.fxh"), "");
            File.WriteAllText(Path.Combine(shaders, "ReShade.fxh"), "");
            var iniPath = Path.Combine(directory, "GShade.ini");
            var ini = "[ADDON]\nAddonPath=.\\gshade-addons\nDisabledAddons=\n[GENERAL]\n" +
                "EffectSearchPaths=.\\gshade-shaders\\Shaders,.\\gshade-shaders\\ComputeShaders\nPerformanceMode=0";
            File.WriteAllText(iniPath, ini);
            // A leftover ReShade config must not override GShade's actual DLL metadata.
            File.WriteAllText(Path.Combine(directory, "ReShade.ini"), "[GENERAL]\nEffectSearchPaths=.\\wrong");
            setup.Refresh();
            await setup.Operation!;
            Check(setup.State == SetupState.InstallRequired && setup.Message.Contains("for GShade"),
                "GShade DLL metadata and GShade.ini should select GShade installation");
            setup.Install();
            await setup.Operation!;
            Check(setup.State == SetupState.RestartRequired, "GShade installation requires full restart");
            Check(File.ReadAllText(Path.Combine(directory, "gshade-addons", "adof_reshade_bridge.addon64")) ==
                  "GPoseADOF.Addon" && File.ReadAllText(Path.Combine(shaders, "GPose_ADOF.fx")) == "GPoseADOF.Shader",
                "GShade configured add-on/shader folders must receive exact bundled files");
            Check(!File.Exists(Path.Combine(directory, "adof_reshade_bridge.addon64")) &&
                  File.ReadAllText(iniPath) == ini, "GShade installation must not use ReShade paths or edit config");
            var nextSession = new Onboarding(directory, Path.Combine(directory, "backups"), client, _ => { },
                Resource, (path, name) => File.ReadAllText(path) == name);
            await using var server = Server();
            nextSession.Refresh();
            await ReadRequest(server);
            await WriteProbeResponse(server, "{\"ok\":true,\"protocolVersion\":4}");
            await nextSession.Operation!;
            Check(nextSession.State == SetupState.Ready, "GShade requires compatible IPC before unlocking controls");
            File.WriteAllText(iniPath, ini.Replace("PerformanceMode=0", "PerformanceMode=1"));
            nextSession.Refresh();
            await nextSession.Operation!;
            Check(nextSession.State == SetupState.Error && nextSession.Error!.Contains("GShade Performance"),
                "GShade-specific errors must identify the correct injector");
            File.WriteAllText(iniPath, ini);
            File.WriteAllText(dll, "unrelated proxy");
            nextSession.Refresh();
            await nextSession.Operation!;
            Check(nextSession.State == SetupState.Error, "GShade.ini alone must not accept unrelated DLLs");
            File.WriteAllText(Path.Combine(directory, "GShade64.dll"), "GShade");
            nextSession.Refresh();
            await ReadRequest(server);
            await WriteProbeResponse(server, "{\"ok\":true,\"protocolVersion\":4}");
            await nextSession.Operation!;
            Check(nextSession.State == SetupState.Ready, "Named GShade64 DLL must be recognized");
            File.WriteAllText(Path.Combine(directory, "ReShade64.dll"), "ReShade");
            nextSession.Refresh();
            await nextSession.Operation!;
            Check(nextSession.State == SetupState.Error && nextSession.Error!.Contains("Both"),
                "Ambiguous active injector files must not choose an installation destination silently");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task WriteProbeResponse(NamedPipeServerStream server, string response)
    {
        await using (var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true))
        {
            await writer.WriteLineAsync(response);
            await writer.FlushAsync();
        }
        server.Disconnect();
    }

    private static async Task TestSetupProbe()
    {
        using var client = new ReShadeIpcClient();
        await using (var server = Server())
        {
            foreach (var response in new[] { "{\"ok\":true}", "not JSON", "{\"ok\":true,\"protocolVersion\":4}" })
            {
                var probeTask = client.ProbeAsync();
                var request = await ReadRequest(server);
                Check(request.GetProperty("type").GetString() == "ping", "Probe must be non-mutating");
                await WriteProbeResponse(server, response);
                var probe = await probeTask;
                Check(probe.Connected && probe.Ready == response.Contains("\"protocolVersion\":4"),
                    "Probe must reject incompatible or malformed IPC");
            }
            var hungTask = client.ProbeAsync();
            await ReadRequest(server);
            var hung = await hungTask;
            Check(hung.Connected && !hung.Ready && hung.Error is not null,
                "Connected IPC without a response must be an error, not missing installation");
        }
        var absent = await client.ProbeAsync();
        Check(!absent.Connected && !absent.Ready, "Missing live IPC must request restart");
    }

    private static void TestBundledResources()
    {
        var root = Path.GetFullPath("../../../../", AppContext.BaseDirectory);
        var assembly = System.Reflection.Assembly.LoadFile(Path.Combine(root, "bin/Debug/ADOF_Dalamud_Controller.dll"));
        foreach (var (name, path) in new[]
        {
            ("GPoseADOF.Shader", "docs/GPose_ADOF.fx"),
            ("GPoseADOF.Addon", "build/reshade-addon/adof_reshade_bridge.addon64"),
        })
        {
            using var resource = assembly.GetManifestResourceStream(name);
            Check(resource is not null, $"Plugin download must embed {name}");
            using var source = File.OpenRead(Path.Combine(root, path));
            Check(System.Security.Cryptography.SHA256.HashData(resource!).AsSpan().SequenceEqual(
                System.Security.Cryptography.SHA256.HashData(source)), $"Embedded {name} does not match built asset");
        }
    }

    private static async Task TestDisposal()
    {
        var client = new ReShadeIpcClient();
        await using var server = Server();
        client.DeactivateAdof();
        await ReadRequest(server);
        client.Dispose();
        client.Dispose();
        await Task.Delay(100);
        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Check(await reader.ReadLineAsync(timeout.Token) is null, "Disposal did not close the in-flight pipe");
        client.DeactivateAdof();
    }
}

namespace Dalamud.Configuration
{
    public interface IPluginConfiguration
    {
        int Version { get; set; }
    }
}
