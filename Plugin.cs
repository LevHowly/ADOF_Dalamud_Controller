using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;

namespace ADOF_Dalamud_Controller;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/adofcontrol";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IClientState clientState;
    private readonly IFramework framework;
    private readonly ICommandManager commandManager;
    private readonly WindowSystem windowSystem = new("ADOF Controller");
    private readonly ReShadeIpcClient bridge;
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private readonly MainWindow mainWindow;
    private bool wasInGpose;
    private readonly Onboarding onboarding;
    private bool setupWasReady;
    private bool bridgeInitialized;
    private readonly NativeDepthOfField? nativeDepthOfField;
    private readonly string? nativeDepthOfFieldError;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IClientState clientState,
        IFramework framework,
        ICommandManager commandManager,
        IPluginLog log,
        IDataManager dataManager,
        IGameInteropProvider interop)
    {
        this.log = log;
        bridge = new ReShadeIpcClient(error => log.Error(error, "GPose ADOF IPC sender failed."));
        this.pluginInterface = pluginInterface;
        this.clientState = clientState;
        this.framework = framework;
        try
        {
            nativeDepthOfField = new NativeDepthOfField(interop, framework);
        }
        catch (Exception ex)
        {
            nativeDepthOfFieldError = "Automatic native blur suppression is unavailable. Disable the game's GPose depth of field manually.";
            log.Error(ex, "Could not initialize native depth-of-field suppression.");
        }
        this.commandManager = commandManager;
        var gameDirectory = Directory.GetParent(dataManager.GameData.DataPath.FullName)?.FullName
            ?? throw new InvalidDataException("Dalamud did not provide a valid game data directory.");
        onboarding = new Onboarding(gameDirectory,
            Path.Combine(pluginInterface.GetPluginConfigDirectory(), "install-backups"),
            bridge, error => log.Error(error, "GPose ADOF onboarding failed."));
        onboarding.Refresh();
        wasInGpose = clientState.IsGPosing;
        pluginInterface.UiBuilder.DisableGposeUiHide = true;
        configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        configuration.Version = 2;
        configuration.ActivateAdof = false;
        configuration.AutofocusEnabled = false;
        configuration.BlurVisualizerEnabled = false;
        if (DepthOfFieldMath.TryFromCamera(configuration.ApertureFNumber, configuration.FocusDistanceMeters,
                out var near, out var far))
        {
            configuration.NearFocusMeters = (float)near;
            configuration.FarFocusInfinite = double.IsPositiveInfinity(far);
            if (!configuration.FarFocusInfinite)
                configuration.FarFocusMeters = (float)far;
        }
        mainWindow = new MainWindow(
            configuration,
            bridge,
            SaveConfiguration,
            SendState,
            () => clientState.IsGPosing,
            onboarding,
            () => nativeDepthOfFieldError ?? nativeDepthOfField?.Error);
        windowSystem.AddWindow(mainWindow);
        mainWindow.IsOpen = wasInGpose;

        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;
        framework.Update += OnFrameworkUpdate;
        commandManager.AddHandler(CommandName, new CommandInfo((_, _) => mainWindow.Toggle())
        {
            HelpMessage = "Open the ADOF controller",
        });

        SaveConfiguration();
        SendState();
    }

    public string Name => "ADOF Dalamud Controller";

    public void Dispose()
    {
        nativeDepthOfField?.Dispose();
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;
        framework.Update -= OnFrameworkUpdate;
        pluginInterface.UiBuilder.DisableGposeUiHide = false;
        commandManager.RemoveHandler(CommandName);
        windowSystem.RemoveAllWindows();
        bridge.Dispose();
    }

    private void Draw()
    {
        windowSystem.Draw();
        mainWindow.DrawCameraGrid();
    }

    private void OnFrameworkUpdate(IFramework frameworkContext)
    {
        var setupReady = onboarding.State == SetupState.Ready;
        if (setupReady && !setupWasReady)
        {
            bridgeInitialized = true;
            SendState();
        }
        setupWasReady = setupReady;
        var isInGpose = clientState.IsGPosing;
        if (isInGpose && !wasInGpose)
        {
            mainWindow.IsOpen = true;
            onboarding.Refresh();
            setupWasReady = false;
        }
        else if (!isInGpose && wasInGpose)
        {
            mainWindow.IsOpen = false;
            mainWindow.DisableCameraGrid();
            configuration.BlurVisualizerEnabled = false;
            if (configuration.DeactivateOnExit)
            {
                configuration.ActivateAdof = false;
                if (bridgeInitialized)
                    bridge.DeactivateAdof();
            }
            else
                SendState();
            SaveConfiguration();
        }

        wasInGpose = isInGpose;
        if (nativeDepthOfField is not null)
            nativeDepthOfField.Suppress = isInGpose && configuration.ActivateAdof &&
                                          onboarding.State == SetupState.Ready;
        mainWindow.ApplyCapturedFocus();
    }

    private void ToggleMainWindow() => mainWindow.Toggle();

    private void SaveConfiguration() => pluginInterface.SavePluginConfig(configuration);

    private void SendState()
    {
        if (!configuration.ActivateAdof && bridgeInitialized)
        {
            bridge.DeactivateAdof();
            return;
        }
        if (onboarding.State != SetupState.Ready)
            return;
        log.Debug("GPose ADOF requested: active={Active}, autofocus={Autofocus}",
            configuration.ActivateAdof, configuration.AutofocusEnabled);
        bridge.SendState(configuration);
    }
}
