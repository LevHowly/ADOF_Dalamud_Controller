using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ADOF_Dalamud_Controller;

public sealed class ReShadeIpcClient : IDisposable
{
    public const string PipeName = "adof-reshade";
    private readonly object stateLock = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim exchangeLock = new(1, 1);
    private string? pendingRequest;
    private long generation;
    private Task? worker;
    private bool disposed;
    private bool focusCapturePending;
    private float? capturedFocusMeters;
    private string? captureRestoreRequest;
    public string? FocusCaptureError { get; private set; }
    private readonly Action<Exception>? reportError;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Status { get; private set; } = "Connecting to ReShade bridge...";
    public bool IsFocusCapturePending
    {
        get { lock (stateLock) return focusCapturePending; }
    }

    public bool TryTakeCapturedFocus(out float focusMeters)
    {
        lock (stateLock)
        {
            focusMeters = capturedFocusMeters.GetValueOrDefault();
            if (capturedFocusMeters is null)
                return false;
            capturedFocusMeters = null;
            return true;
        }
    }

    public ReShadeIpcClient(Action<Exception>? reportError = null)
    {
        this.reportError = reportError;
    }

    public sealed record BridgeProbe(bool Connected, bool Ready, string? Error);

    public async Task<BridgeProbe> ProbeAsync()
    {
        var connected = false;
        CancellationToken cancellationToken;
        lock (stateLock)
        {
            if (disposed)
                return new BridgeProbe(false, false, "The plugin is shutting down.");
            cancellationToken = lifetime.Token;
        }
        await exchangeLock.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(500, timeout.Token);
            connected = true;
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new BridgeCommand(4, "ping"), JsonOptions).AsMemory(),
                timeout.Token);
            await writer.FlushAsync(timeout.Token);
            var line = await reader.ReadLineAsync(timeout.Token);
            var response = string.IsNullOrWhiteSpace(line) ? null :
                JsonSerializer.Deserialize<BridgeResponse>(line, JsonOptions);
            if (response is null)
                return new BridgeProbe(true, false, "IPC connected but returned no valid response.");
            if (response.ProtocolVersion != 4)
                return new BridgeProbe(true, false, "The running IPC bridge is incompatible. Close and reopen the game after installing the bundled add-on.");
            return new BridgeProbe(true, response.Ok, response.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or
                                   OperationCanceledException or JsonException)
        {
            return new BridgeProbe(connected, false, ex.Message);
        }
        finally
        {
            exchangeLock.Release();
        }
    }

    public void SendState(Configuration configuration)
    {
        SendState(configuration, false);
    }

    public void CaptureFocus(Configuration configuration)
    {
        if (!configuration.ActivateAdof || configuration.AutofocusEnabled)
        {
            Status = "Autofocus capture requires active GPose ADOF in manual focus mode.";
            return;
        }
        SendState(configuration, true);
    }

    private void SendState(Configuration configuration, bool captureFocus)
    {
        if (!configuration.ActivateAdof)
        {
            DeactivateAdof();
            return;
        }
        if (!DepthOfFieldMath.TryGetShaderParameters(
                configuration.ApertureFNumber, configuration.FocusDistanceMeters,
                configuration.MaxBlurRadiusPixels, out var parameters))
        {
            Status = "Cannot apply GPose ADOF: invalid camera settings or maximum blur radius.";
            if (!configuration.ActivateAdof)
                DeactivateAdof();
            return;
        }
        if (!float.IsFinite(configuration.BlurStrength) ||
            configuration.BlurStrength < 1 || configuration.BlurStrength > 32)
        {
            Status = "Cannot apply GPose ADOF: blur strength must be between 1 and 32.";
            return;
        }
        var request = new BridgeRequest(
            4,
            captureFocus ? "capture_focus" : "set_state",
            captureFocus ? Guid.NewGuid().ToString() : "",
            configuration.ActivateAdof,
            configuration.AutofocusEnabled,
            parameters.ManualFocusDepth,
            parameters.CocBias, parameters.CocScale, parameters.LensCoefficient,
            parameters.InverseDepthA, parameters.InverseDepthB, parameters.InverseDepthDenominator,
            (float)DepthOfFieldMath.FocalLengthMeters,
            configuration.MaxBlurRadiusPixels,
            configuration.BlurStrength,
            configuration.FocusDistanceMeters, configuration.ApertureFNumber,
            configuration.BlurVisualizerEnabled);
        var restoreRequest = captureFocus
            ? JsonSerializer.Serialize(request with { Type = "set_state", CaptureId = "" }, JsonOptions)
            : null;
        QueueRequest(JsonSerializer.Serialize(request, JsonOptions), captureFocus, restoreRequest);
    }

    public void DeactivateAdof()
    {
        QueueRequest(JsonSerializer.Serialize(new BridgeCommand(4, "deactivate_adof"), JsonOptions));
    }

    private void QueueRequest(string requestJson, bool captureFocus = false, string? restoreRequest = null)
    {
        lock (stateLock)
        {
            if (disposed)
                return;
            pendingRequest = requestJson;
            focusCapturePending = captureFocus;
            captureRestoreRequest = restoreRequest;
            FocusCaptureError = null;
            capturedFocusMeters = null;
            generation++;
            if (worker is null)
            {
                worker = Task.Run(RunSenderAsync);
                _ = worker.ContinueWith(task =>
                {
                    var error = task.Exception!.GetBaseException();
                    Status = $"GPose ADOF sender failed: {error.Message}";
                    reportError?.Invoke(error);
                }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }
    }

    private async Task RunSenderAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                string requestJson;
                long requestGeneration;
                bool captureFocus;
                lock (stateLock)
                {
                    if (pendingRequest is null)
                    {
                        worker = null;
                        return;
                    }
                    requestJson = pendingRequest;
                    requestGeneration = generation;
                    captureFocus = focusCapturePending;
                }

                var response = await SendRequestAsync(requestJson, captureFocus, lifetime.Token);
                lock (stateLock)
                {
                    if (requestGeneration == generation && response?.Ok == true)
                    {
                        pendingRequest = null;
                        if (captureFocus)
                            capturedFocusMeters = response.FocusDistanceMeters;
                        focusCapturePending = false;
                    }
                    else if (requestGeneration == generation && captureFocus && response?.Ok == false)
                    {
                        FocusCaptureError = response.Error ?? "The bridge rejected autofocus capture.";
                        pendingRequest = captureRestoreRequest;
                        captureRestoreRequest = null;
                        focusCapturePending = false;
                        generation++;
                    }
                }
                if (response?.Ok != true)
                    await Task.Delay(250, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Disposal cancels connection attempts and retry delays.
        }
    }

    private async Task<BridgeResponse?> SendRequestAsync(
        string requestJson, bool captureFocus, CancellationToken cancellationToken)
    {
        await exchangeLock.WaitAsync(cancellationToken);
        try
        {
            return await ExchangeRequestAsync(requestJson, captureFocus, cancellationToken);
        }
        finally
        {
            exchangeLock.Release();
        }
    }

    private async Task<BridgeResponse?> ExchangeRequestAsync(
        string requestJson, bool captureFocus, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await using var pipe = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(500, timeout.Token);

            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(requestJson.AsMemory(), timeout.Token);
            await writer.FlushAsync(timeout.Token);
            var responseLine = await reader.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(responseLine))
                throw new InvalidDataException("The bridge returned no response.");

            var response = JsonSerializer.Deserialize<BridgeResponse>(responseLine, JsonOptions);
            if (response is null)
                throw new InvalidDataException("The bridge returned an invalid response.");
            if (captureFocus && response.Ok &&
                (response.FocusDistanceMeters is not float focus || !float.IsFinite(focus) ||
                 focus < DepthOfFieldMath.ProjectionNearPlaneMeters ||
                 focus > DepthOfFieldMath.ProjectionFarPlaneMeters))
                response = new BridgeResponse(false, "The bridge returned no valid autofocus distance.");

            Status = response.Ok
                ? "Connected to GPose ADOF bridge"
                : captureFocus
                    ? $"Autofocus capture failed: {response.Error ?? "The bridge rejected the capture."}"
                    : $"ReShade bridge update failed; retrying latest request: {response.Error ?? "The bridge rejected the update."}";
            return response;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or JsonException)
        {
            if (cancellationToken.IsCancellationRequested)
                cancellationToken.ThrowIfCancellationRequested();
            Status = $"ReShade bridge unavailable; retrying latest request: {ex.Message}";
            return null;
        }
    }

    public void Dispose()
    {
        lock (stateLock)
        {
            if (disposed)
                return;
            disposed = true;
            capturedFocusMeters = null;
            focusCapturePending = false;
            lifetime.Cancel();
            if (worker is null)
                lifetime.Dispose();
            else
                _ = worker.ContinueWith(_ => lifetime.Dispose(), TaskScheduler.Default);
        }
    }

    private sealed record BridgeRequest(
        int ProtocolVersion,
        string Type,
        string CaptureId,
        bool ActivateAdof,
        bool AutofocusEnabled,
        float ManualFocusDepth,
        float CocBias, float CocScale, float LensCoefficient,
        float InverseDepthA, float InverseDepthB, float InverseDepthDenominator,
        float FocalLengthMeters,
        float MaxBlurRadiusPixels,
        float BlurStrength,
        float FocusDistanceMeters, float ApertureFNumber,
        bool BlurVisualizerEnabled);

    private sealed record BridgeCommand(int ProtocolVersion, string Type);

    public sealed record BridgeResponse(
        bool Ok,
        string? Error,
        float? FocusDistanceMeters = null,
        int ProtocolVersion = 0);
}
