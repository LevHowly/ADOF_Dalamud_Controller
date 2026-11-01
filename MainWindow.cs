using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace ADOF_Dalamud_Controller;

public sealed class MainWindow : Window
{
    private readonly Configuration configuration;
    private readonly ReShadeIpcClient bridge;
    private readonly Action saveConfiguration;
    private readonly Action stateChanged;
    private readonly Func<bool> isInGpose;
    private bool cameraGridEnabled;
    private string? calculationError;
    private readonly Onboarding onboarding;
    private readonly Func<string?> nativeDepthOfFieldError;

    public MainWindow(
        Configuration configuration,
        ReShadeIpcClient bridge,
        Action saveConfiguration,
        Action stateChanged,
        Func<bool> isInGpose,
        Onboarding onboarding,
        Func<string?> nativeDepthOfFieldError)
        : base("GPose ADOF")
    {
        this.configuration = configuration;
        this.bridge = bridge;
        this.saveConfiguration = saveConfiguration;
        this.stateChanged = stateChanged;
        this.isInGpose = isInGpose;
        this.onboarding = onboarding;
        this.nativeDepthOfFieldError = nativeDepthOfFieldError;
        if (!DepthOfFieldMath.TryGetShaderParameters(configuration.ApertureFNumber,
                configuration.FocusDistanceMeters, configuration.MaxBlurRadiusPixels, out _))
            calculationError = "saved camera settings are outside the supported range; adjust the controls before activating.";
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(360, 380),
            MaximumSize = new System.Numerics.Vector2(720, 700),
        };
    }

    public override void Draw()
    {
        if (onboarding.State != SetupState.Ready)
        {
            ImGui.TextUnformatted("GPose ADOF setup");
            ImGui.Separator();
            ImGui.TextWrapped(onboarding.Message);
            if (onboarding.Error is not null)
                ImGui.TextWrapped(onboarding.Error);
            ImGui.BeginDisabled(onboarding.Operation is { IsCompleted: false });
            if (onboarding.State == SetupState.InstallRequired)
            {
                if (ImGui.Button("Install GPose ADOF"))
                    onboarding.Install();
            }
            else if (ImGui.Button("Recheck setup"))
                onboarding.Refresh();
            ImGui.EndDisabled();
            return;
        }
        var activate = configuration.ActivateAdof;
        if (ImGui.Checkbox("Activate depth of field", ref activate))
        {
            configuration.ActivateAdof = activate;
            if (!activate)
                configuration.BlurVisualizerEnabled = false;
            Changed();
        }
        if (activate && nativeDepthOfFieldError() is string nativeError)
            ImGui.TextWrapped(nativeError);

        var deactivateOnExit = configuration.DeactivateOnExit;
        if (ImGui.Checkbox("Deactivate ADOF on exiting GPose", ref deactivateOnExit))
        {
            configuration.DeactivateOnExit = deactivateOnExit;
            saveConfiguration();
        }

        ImGui.BeginDisabled(!isInGpose());
        ImGui.Checkbox("Camera grid", ref cameraGridEnabled);
        var whiteLines = configuration.CameraGridWhiteLines;
        if (ImGui.Checkbox("White grid lines", ref whiteLines))
        {
            configuration.CameraGridWhiteLines = whiteLines;
            saveConfiguration();
        }
        ImGui.EndDisabled();

        ImGui.Separator();
        ImGui.TextUnformatted("Photographer Mode");

        var manualFocus = !configuration.AutofocusEnabled;
        if (ImGui.Checkbox("Manual focus", ref manualFocus))
        {
            configuration.AutofocusEnabled = !manualFocus;
            Changed();
        }
        var capturingFocus = bridge.IsFocusCapturePending;
        if (!manualFocus)
            ImGui.TextWrapped("Continuous GPU autofocus is active. Distance limits below are the manual-focus preview; the visualizer shows the actual GPU blur.");
        else
        {
            ImGui.BeginDisabled(!configuration.ActivateAdof || !isInGpose() || capturingFocus);
            if (ImGui.Button("Autofocus"))
                bridge.CaptureFocus(configuration);
            ImGui.EndDisabled();
            if (capturingFocus && ImGui.Button("Cancel autofocus"))
                stateChanged();
            ImGui.TextWrapped(capturingFocus ? "Capturing focus from the shader..." :
                "Autofocus captures the ReShade target once and updates the manual distance and depth limits. Requires active ADOF in GPose.");
        }

        ImGui.BeginDisabled(!manualFocus || capturingFocus);
        var aperture = configuration.ApertureFNumber;
        if (ImGui.SliderFloat(
                "Aperture (f-number)",
                ref aperture,
                0.1f,
                Math.Max(32f, aperture),
                "f/%.1f"))
        {
            RecalculateDepthLimits(aperture, configuration.FocusDistanceMeters);
        }

        var focus = configuration.FocusDistanceMeters;
        if (ImGui.SliderFloat(
                "Focus Distance (m)",
                ref focus,
                0.1f,
                Math.Max(50f, focus),
                "%.2f m",
                ImGuiSliderFlags.Logarithmic))
        {
            RecalculateDepthLimits(configuration.ApertureFNumber, focus);
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Depth Mode");

        var near = configuration.NearFocusMeters;
        if (ImGui.SliderFloat(
                "Near Focus (m)",
                ref near,
                0.1f,
                Math.Max(50f, near),
                "%.2f m",
                ImGuiSliderFlags.Logarithmic))
        {
            RecalculateCameraSettings(near, configuration.FarFocusInfinite
                ? double.PositiveInfinity : configuration.FarFocusMeters);
        }

        var infinite = configuration.FarFocusInfinite;
        if (ImGui.Checkbox("Far focus at infinity", ref infinite))
            RecalculateCameraSettings(configuration.NearFocusMeters,
                infinite ? double.PositiveInfinity : Math.Max(50, configuration.NearFocusMeters * 2));

        ImGui.BeginDisabled(configuration.FarFocusInfinite);
        var far = configuration.FarFocusMeters;
        if (ImGui.SliderFloat(
                "Far Focus (m)",
                ref far,
                0.1f,
                Math.Max(50f, far),
                "%.2f m",
                ImGuiSliderFlags.Logarithmic))
        {
            RecalculateCameraSettings(configuration.NearFocusMeters, far);
        }
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        ImGui.TextWrapped("Near/far limits describe a physical 1-pixel blur diameter. The global radius reduction widens the rendered sharp band.");

        var strength = configuration.BlurStrength;
        if (ImGui.SliderFloat("Out-of-focus blur strength", ref strength, 1, 32, "%.1fx"))
        {
            configuration.BlurStrength = strength;
            Changed();
        }
        var maxBlur = configuration.MaxBlurRadiusPixels;
        if (ImGui.SliderFloat("Maximum blur radius (px)", ref maxBlur, 1, 100, "%.1f px"))
        {
            configuration.MaxBlurRadiusPixels = maxBlur;
            Changed();
        }

        var visualizer = configuration.BlurVisualizerEnabled;
        if (ImGui.Checkbox("Enable depth visualizer", ref visualizer))
        {
            configuration.BlurVisualizerEnabled = visualizer;
            if (visualizer)
                configuration.ActivateAdof = true;
            Changed();
        }
        ImGui.TextWrapped("Green to red shows rendered blur amount, including autofocus and blur strength. Enabling this also activates ADOF.");

        if (calculationError is not null)
            ImGui.TextWrapped($"Calculation unavailable: {calculationError}");
        if (bridge.FocusCaptureError is not null)
            ImGui.TextWrapped($"Autofocus capture failed: {bridge.FocusCaptureError}");

        ImGui.Separator();
        ImGui.TextWrapped(bridge.Status);
        ImGui.BeginDisabled(bridge.IsFocusCapturePending);
        if (ImGui.Button("Recheck installation / IPC"))
            onboarding.Refresh();
        ImGui.EndDisabled();
    }

    private void RecalculateDepthLimits(float aperture, float focus)
    {
        if (!DepthOfFieldMath.TryFromCamera(
                aperture,
                focus,
                out var near,
                out var far))
        {
            calculationError = "the chosen camera settings do not produce valid depth limits.";
            return;
        }

        if (!DepthOfFieldMath.TryGetShaderParameters(aperture, focus, configuration.MaxBlurRadiusPixels, out _) ||
            !float.IsFinite((float)near) || (double.IsFinite(far) && !float.IsFinite((float)far)))
        {
            calculationError = "the chosen settings exceed the supported camera or shader range.";
            return;
        }
        configuration.ApertureFNumber = aperture;
        configuration.FocusDistanceMeters = focus;
        configuration.NearFocusMeters = (float)near;
        configuration.FarFocusInfinite = double.IsPositiveInfinity(far);
        if (!configuration.FarFocusInfinite)
            configuration.FarFocusMeters = (float)far;
        calculationError = null;
        Changed();
    }

    private void RecalculateCameraSettings(double near, double far)
    {
        if (!DepthOfFieldMath.TryFromDepthLimits(
                near,
                far,
                out var aperture,
                out var focus))
        {
            calculationError = "near and far limits must describe a valid depth-of-field range.";
            return;
        }

        RecalculateDepthLimits((float)aperture, (float)focus);
    }

    private void Changed()
    {
        saveConfiguration();
        stateChanged();
    }

    public void ApplyCapturedFocus()
    {
        if (bridge.TryTakeCapturedFocus(out var focus) &&
            isInGpose() && configuration.ActivateAdof && !configuration.AutofocusEnabled)
            RecalculateDepthLimits(configuration.ApertureFNumber, focus);
    }

    public void DisableCameraGrid() => cameraGridEnabled = false;

    public void DrawCameraGrid()
    {
        if (!cameraGridEnabled || !isInGpose())
            return;

        var viewport = ImGui.GetMainViewport();
        var position = viewport.Pos;
        var size = viewport.Size;
        var color = configuration.CameraGridWhiteLines ? 0xFFFFFFFFu : 0xFF000000u;
        var drawList = ImGui.GetForegroundDrawList();

        for (var division = 1; division <= 2; division++)
        {
            var x = position.X + size.X * division / 3f;
            var y = position.Y + size.Y * division / 3f;
            drawList.AddLine(
                new System.Numerics.Vector2(x, position.Y),
                new System.Numerics.Vector2(x, position.Y + size.Y),
                color,
                2f);
            drawList.AddLine(
                new System.Numerics.Vector2(position.X, y),
                new System.Numerics.Vector2(position.X + size.X, y),
                color,
                2f);
        }

        var center = position + size * 0.5f;
        const float crosshairHalfSize = 12f;
        drawList.AddLine(
            center - new System.Numerics.Vector2(crosshairHalfSize, 0f),
            center + new System.Numerics.Vector2(crosshairHalfSize, 0f),
            color,
            2f);
        drawList.AddLine(
            center - new System.Numerics.Vector2(0f, crosshairHalfSize),
            center + new System.Numerics.Vector2(0f, crosshairHalfSize),
            color,
            2f);
    }
}
