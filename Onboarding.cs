using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;

namespace ADOF_Dalamud_Controller;

public enum SetupState { Checking, Error, InstallRequired, RestartRequired, Ready }

public sealed class Onboarding
{
    private readonly string gameDirectory;
    private readonly string backupDirectory;
    private readonly ReShadeIpcClient bridge;
    private readonly Action<Exception> reportError;
    private readonly Func<string, Stream> resource;
    private readonly Func<string, string, bool> isInjector;
    private string injectorName = "ReShade / GShade";
    private string? addonPath;
    private string? shaderPath;
    private bool installedThisSession;

    public SetupState State { get; private set; } = SetupState.Checking;
    public string Message { get; private set; } = "Checking ReShade / GShade installation...";
    public string? Error { get; private set; }
    public Task? Operation { get; private set; }

    public Onboarding(string gameDirectory, string backupDirectory, ReShadeIpcClient bridge,
        Action<Exception> reportError, Func<string, Stream>? resource = null,
        Func<string, string, bool>? isInjector = null)
    {
        this.gameDirectory = Path.GetFullPath(gameDirectory);
        this.backupDirectory = backupDirectory;
        this.bridge = bridge;
        this.reportError = reportError;
        this.resource = resource ?? (name => Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(name) ?? throw new InvalidDataException($"Bundled resource missing: {name}"));
        this.isInjector = isInjector ?? ((path, name) =>
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            return (version.ProductName?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (version.FileDescription?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false);
        });
    }

    public void Refresh()
    {
        Operation = RefreshAfterAsync(Operation);
    }

    public void Install()
    {
        if (Operation is { IsCompleted: false } || State != SetupState.InstallRequired)
            return;
        Operation = InstallAsync();
    }

    private async Task RefreshAfterAsync(Task? previous)
    {
        if (previous is not null)
            await previous;
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        State = SetupState.Checking;
        Message = "Checking ReShade / GShade installation automatically...";
        Error = null;
        try
        {
            await Task.Run(InspectFiles);
            if (State != SetupState.Checking)
                return;
            if (installedThisSession)
            {
                State = SetupState.RestartRequired;
                Message = "Installation complete. Close the game completely and reopen it to load the native IPC add-on.";
                return;
            }
            Message = "Checking GPose ADOF IPC automatically...";
            var probe = await bridge.ProbeAsync();
            for (var retry = 0; !probe.Ready && retry < 2; retry++)
            {
                Message = "Waiting for GPose ADOF IPC to become ready; retrying automatically...";
                await Task.Delay(500);
                probe = await bridge.ProbeAsync();
            }
            State = probe.Ready ? SetupState.Ready :
                probe.Connected ? SetupState.Error : SetupState.RestartRequired;
            Message = probe.Ready ? "GPose ADOF is installed and IPC is responding." :
                probe.Connected ? $"The IPC add-on is running but not ready: {probe.Error}" :
                $"Files are installed, but the IPC add-on is not running. Close and reopen the game. If this persists, enable ADOF ReShade Bridge in {injectorName} and check that your {injectorName} build supports ReShade API 20 add-ons.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidDataException or ArgumentException or NotSupportedException or OperationCanceledException)
        {
            Fail(ex);
        }
    }

    private void InspectFiles()
    {
        if (!File.Exists(Path.Combine(gameDirectory, "ffxiv_dx11.exe")))
            throw new InvalidDataException($"Dalamud's game directory is invalid: {gameDirectory}");
        var installations = new[] { "GShade", "ReShade" }.Where(name =>
            File.Exists(Path.Combine(gameDirectory, name + ".ini")) &&
            new[] { "dxgi.dll", "d3d11.dll", name + "64.dll" }
                .Select(file => Path.Combine(gameDirectory, file))
                .Any(path => File.Exists(path) && isInjector(path, name))).ToArray();
        if (installations.Length == 0)
        {
            State = SetupState.Error;
            Message = "ReShade or GShade was not found in the game folder. Install a Direct3D 11 build with ReShade add-on support first, then recheck. The plugin does not install either injector.";
            return;
        }
        if (installations.Length != 1)
            throw new InvalidDataException("Both ReShade and GShade installations were detected. Remove the unused injector/configuration before installing GPose ADOF so the destination is unambiguous.");
        injectorName = installations[0];
        var iniPath = Path.Combine(gameDirectory, injectorName + ".ini");
        var ini = ReadIni(iniPath);
        var addonDirectory = ResolvePath(Get(ini, "ADDON", "AddonPath") ?? ".");
        addonPath = Path.Combine(addonDirectory, "adof_reshade_bridge.addon64");
        var searchPaths = Get(ini, "GENERAL", "EffectSearchPaths");
        if (string.IsNullOrWhiteSpace(searchPaths))
            throw new InvalidDataException($"{injectorName}.ini has no EffectSearchPaths. Configure a shader search folder in {injectorName} first.");
        var directories = new List<string>();
        foreach (var entry in searchPaths.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var recursive = entry.EndsWith("**", StringComparison.Ordinal);
            var directory = ResolvePath(recursive ? entry[..^2].TrimEnd('\\', '/') : entry);
            if (directory.Contains('*') || directory.Contains('?'))
                throw new InvalidDataException($"Unsupported {injectorName} search path: {entry}");
            directories.Add(directory);
            if (recursive && Directory.Exists(directory))
                directories.AddRange(Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories));
        }
        var includesAvailable = directories.Any(include => File.Exists(Path.Combine(include, "ReShade.fxh")));
        // Keep the new effect next to its existing qUINT include, inside an actual effect search path.
        var shaderDirectory = directories.FirstOrDefault(dir =>
            File.Exists(Path.Combine(dir, "qUINT_common.fxh")) &&
            includesAvailable);
        if (shaderDirectory is null)
            throw new InvalidDataException($"{injectorName}'s effect search paths are missing qUINT_common.fxh or ReShade.fxh. Install the qUINT shader package in {injectorName}, then recheck.");
        shaderPath = Path.Combine(shaderDirectory, "GPose_ADOF.fx");
        if (!Matches(addonPath, "GPoseADOF.Addon") || !Matches(shaderPath, "GPoseADOF.Shader"))
        {
            State = SetupState.InstallRequired;
            Message = $"Install or update GPose ADOF's bundled files for {injectorName}:\nAdd-on: {addonPath}\nShader: {shaderPath}\nExisting files are backed up in the plugin configuration folder. {injectorName} settings and presets are not changed.";
            return;
        }
        if ((Get(ini, "ADDON", "DisabledAddons") ?? "").Contains("ADOF ReShade Bridge", StringComparison.OrdinalIgnoreCase) ||
            (Get(ini, "ADDON", "DisabledAddons") ?? "").Contains("adof_reshade_bridge", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"ADOF ReShade Bridge is disabled in {injectorName}. Enable it in {injectorName}'s Add-ons tab and restart the game.");
        if (Get(ini, "GENERAL", "PerformanceMode") == "1")
            throw new InvalidDataException($"Disable {injectorName} Performance Mode so GPose ADOF's uniforms are accessible, then recheck.");
    }

    private async Task InstallAsync()
    {
        State = SetupState.Checking;
        Error = null;
        Message = "Installing bundled GPose ADOF files...";
        try
        {
            await Task.Run(() =>
            {
                if (addonPath is null || shaderPath is null)
                    throw new InvalidDataException("Installation paths have not been checked.");
                InstallFile(addonPath, "GPoseADOF.Addon");
                InstallFile(shaderPath, "GPoseADOF.Shader");
            });
            installedThisSession = true;
            State = SetupState.RestartRequired;
            Message = "Installation complete. Close the game completely and reopen it to load the native IPC add-on.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidDataException or ArgumentException or NotSupportedException)
        {
            Fail(ex);
        }
    }

    private void InstallFile(string destination, string name)
    {
        if (Matches(destination, name))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            Directory.CreateDirectory(backupDirectory);
            File.Copy(destination, Path.Combine(backupDirectory,
                $"{Path.GetFileName(destination)}.{DateTime.UtcNow:yyyyMMdd-HHmmss}.{Guid.NewGuid():N}.backup"));
        }
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = resource(name))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                input.CopyTo(output);
            File.Move(temporary, destination, true);
            if (!Matches(destination, name))
                throw new IOException($"Installed file verification failed: {destination}");
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private bool Matches(string path, string name)
    {
        if (!File.Exists(path))
            return false;
        using var installed = File.OpenRead(path);
        using var bundled = resource(name);
        return SHA256.HashData(installed).AsSpan().SequenceEqual(SHA256.HashData(bundled));
    }

    private string ResolvePath(string value) =>
        Path.GetFullPath(value.Trim().Trim('"').Replace('\\', Path.DirectorySeparatorChar), gameDirectory);

    private void Fail(Exception ex)
    {
        reportError(ex);
        Error = ex.Message;
        Message = "GPose ADOF setup could not complete. Correct the error and recheck.";
        State = SetupState.Error;
    }

    private static Dictionary<string, string> ReadIni(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith(';') || line.StartsWith('#') || line.Length == 0)
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
                section = line[1..^1];
            else if (line.IndexOf('=') is var separator && separator > 0)
                values[section + ":" + line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return values;
    }

    private static string? Get(Dictionary<string, string> ini, string section, string key) =>
        ini.GetValueOrDefault(section + ":" + key);
}
