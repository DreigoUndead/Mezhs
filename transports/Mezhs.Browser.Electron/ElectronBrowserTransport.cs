using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Mezhs.Browser;

namespace Mezhs.Browser.Electron;

public sealed class ElectronBrowserTransport(string electronDirectory) : IChatBrowserTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan OperationPollInterval = TimeSpan.FromSeconds(2);
    private readonly string _electronDirectory = Path.GetFullPath(electronDirectory);
    // The loopback /authorize call itself awaits upstream provider authentication
    // probes and Chromium storage flushing. Keep a finite deadline, but allow
    // more than ten seconds for that external work. Stop/cancel still use their
    // own shorter cancellation tokens.
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly TaskCompletionSource<Uri> _ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;
    private Task? _stdoutTask;
    private Task? _stderrTask;

    public string Name => "Electron";

    public async Task InitializeAsync(
        BrowserTransportOptions options,
        CancellationToken cancellationToken = default)
    {
        if (_process is not null)
            throw new InvalidOperationException("Electron transport is already initialized.");

        var packageDirectory = Path.Combine(_electronDirectory, "node_modules", "electron");
        var pathFile = Path.Combine(packageDirectory, "path.txt");
        if (!File.Exists(pathFile))
        {
            throw new FileNotFoundException(
                $"Electron is not installed. Run 'npm install' in {_electronDirectory}.",
                pathFile);
        }

        var executable = Path.Combine(
            packageDirectory,
            "dist",
            (await File.ReadAllTextAsync(pathFile, cancellationToken)).Trim());
        if (!File.Exists(executable))
            throw new FileNotFoundException("Electron executable was not found.", executable);
        if (!File.Exists(options.ModulePath))
            throw new FileNotFoundException("Browser integration module was not found.", options.ModulePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = _electronDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(".");
        startInfo.Environment["MEZHS_PROFILE_DIRECTORY"] = Path.GetFullPath(options.ProfileDirectory);
        startInfo.Environment["MEZHS_SHOW_BROWSER"] = options.ShowBrowser ? "1" : "0";
        startInfo.Environment["MEZHS_BROWSER_MODULE"] = Path.GetFullPath(options.ModulePath);
        startInfo.Environment["MEZHS_REQUIRE_AUTHORIZATION"] = options.RequireAuthorization ? "1" : "0";
        startInfo.Environment["MEZHS_GUEST"] = options.Guest ? "1" : "0";
        startInfo.Environment["MEZHS_PARENT_PROCESS_ID"] = Environment.ProcessId.ToString();

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start Electron.");
        _stdoutTask = ReadStdoutAsync(_process);
        _stderrTask = ReadStderrAsync(_process);

        var baseAddress = await _ready.Task.WaitAsync(cancellationToken);
        _http.BaseAddress = baseAddress;
    }

    public Task<TResult> InvokeAsync<TResult>(
        string operation,
        object? arguments = null,
        CancellationToken cancellationToken = default) =>
        InvokeCoreAsync<TResult>(operation, arguments, reportProgress: null, cancellationToken, null, false);

    public Task<TResult> InvokeWithProgressAsync<TResult>(
        string operation,
        object? arguments,
        Action<BrowserOperationProgress>? reportProgress,
        CancellationToken cancellationToken = default) =>
        InvokeCoreAsync<TResult>(operation, arguments, reportProgress, cancellationToken, null, false);

    public Task<TResult> InvokeWithEventsAsync<TResult>(
        string operation,
        object? arguments,
        Action<JsonElement> reportEvent,
        Action<BrowserOperationProgress>? reportProgress = null,
        CancellationToken cancellationToken = default) =>
        InvokeCoreAsync<TResult>(operation, arguments, reportProgress,
            cancellationToken, null, false, reportEvent);

    private async Task<TResult> InvokeCoreAsync<TResult>(
        string operation,
        object? arguments,
        Action<BrowserOperationProgress>? reportProgress,
        CancellationToken cancellationToken,
        string? contextId,
        bool ephemeral,
        Action<JsonElement>? reportEvent = null)
    {
        EnsureRunning();
        string? operationId = null;
        try
        {
        using var startResponse = await _http.PostAsJsonAsync(
            "invoke",
            new { operation, arguments, contextId, ephemeral },
            JsonOptions,
            cancellationToken);
        if (!startResponse.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Electron provider operation could not start: {await startResponse.Content.ReadAsStringAsync(cancellationToken)}");

        var started = await startResponse.Content.ReadFromJsonAsync<ProviderOperationStarted>(
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Electron provider operation '{operation}' returned no operation id.");
        if (string.IsNullOrWhiteSpace(started.OperationId))
            throw new InvalidOperationException(
                $"Electron provider operation '{operation}' returned an empty operation id.");

        operationId = started.OperationId;
        BrowserOperationProgress? lastProgress = null;
        long acknowledgedEvent = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var statusResponse = await _http.GetAsync(
                $"invoke/{Uri.EscapeDataString(started.OperationId)}?after={acknowledgedEvent}",
                cancellationToken);
            if (!statusResponse.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Electron provider operation status failed: {await statusResponse.Content.ReadAsStringAsync(cancellationToken)}");

            var state = await statusResponse.Content.ReadFromJsonAsync<ProviderOperationState>(
                JsonOptions,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Electron provider operation '{operation}' returned no status.");

            foreach (var evt in state.Events ?? [])
            {
                if (evt.Sequence != acknowledgedEvent + 1)
                    throw new InvalidOperationException(
                        "Browser stream event sequence was not contiguous.");
                if (reportEvent is null)
                    throw new InvalidOperationException(
                        "Browser delivered protocol events without an event consumer.");
                reportEvent(evt.Value);
                acknowledgedEvent = evt.Sequence;
            }
            if (state.LastSequence < acknowledgedEvent)
                throw new InvalidOperationException("Browser stream event cursor regressed.");

            if (state.Progress is { } progress && progress != lastProgress)
            {
                lastProgress = progress;
                reportProgress?.Invoke(progress);
            }

            // A terminal status can precede the end of a bounded event batch.
            if (state.Status is "completed" or "failed" &&
                state.LastSequence > acknowledgedEvent)
                continue;

            switch (state.Status)
            {
                case "queued":
                case "running":
                    await Task.Delay(OperationPollInterval, cancellationToken);
                    continue;

                case "completed":
                    if (state.Result is null)
                    {
                        throw new InvalidOperationException(
                            $"Electron provider operation '{operation}' returned no result.");
                    }
                    return state.Result.Value.Deserialize<TResult>(JsonOptions)
                        ?? throw new InvalidOperationException(
                            $"Electron provider operation '{operation}' returned no result.");

                case "failed":
                    throw string.Equals(
                        state.Code,
                        "authorization_required",
                        StringComparison.OrdinalIgnoreCase)
                        ? new BrowserAuthorizationRequiredException(
                            state.Error ?? "Browser authorization is required.")
                        : string.Equals(state.Code, "request_not_submitted", StringComparison.Ordinal)
                        ? new BrowserRequestNotSubmittedException(
                            "Browser HTTP request was not submitted; native preparation failed.")
                        : string.Equals(state.Code, "stream_delivery_failed", StringComparison.Ordinal)
                        ? new BrowserStreamProtocolException(
                            "Browser HTTP protocol event framing or delivery failed.")
                        : new InvalidOperationException(
                            $"Electron provider operation failed: {state.Error ?? "Unknown provider error."}");

                default:
                    throw new InvalidOperationException(
                        $"Electron provider operation '{operation}' returned unknown status '{state.Status}'.");
            }
        }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (operationId is not null)
            {
                // Stop while the operation still owns its browser context, before
                // the existing local cancel endpoint destroys that context.
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await RequestStopAsync($"invoke/{Uri.EscapeDataString(operationId)}/stop",
                        body: null, timeout.Token);
                }
                catch (Exception) { /* A failed provider stop must not block local cancel. */ }
                await CancelOperationBestEffortAsync(operationId);
            }
            throw;
        }
        catch
        {
            if (operationId is not null)
                await CancelOperationBestEffortAsync(operationId);
            throw;
        }
    }

    private async Task CancelOperationBestEffortAsync(string operationId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var response = await _http.PostAsync(
                $"invoke/{Uri.EscapeDataString(operationId)}/cancel", null, timeout.Token);
        }
        catch (Exception)
        {
            // The local cancellation is final; remote cancellation is best effort.
        }
    }

    public async Task<BrowserStopOutcome> StopTurnAsync(string chatId, string messageId,
        CancellationToken cancellationToken = default)
    {
        if (_process is null || _process.HasExited || _http.BaseAddress is null)
            return BrowserStopOutcome.NotActive;
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        return await RequestStopAsync("turns/stop", new { contextId = chatId, messageId }, timeout.Token);
    }

    private async Task<BrowserStopOutcome> RequestStopAsync(
        string endpoint, object? body, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(endpoint, body, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ProviderStopState>(
            JsonOptions, cancellationToken);
        return result?.Outcome switch
        {
            "notActive" => BrowserStopOutcome.NotActive,
            "attempted" => BrowserStopOutcome.Attempted,
            "providerConfirmed" => BrowserStopOutcome.ProviderConfirmed,
            _ => BrowserStopOutcome.Unsupported
        };
    }

    public IChatBrowserTransport ForContext(string chatId, bool ephemeral = false) =>
        new ContextView(this, chatId, ephemeral);

    public async Task ReleaseContextAsync(string chatId, CancellationToken cancellationToken = default)
    {
        if (_process is null || _process.HasExited) return;
        using var response = await _http.PostAsJsonAsync(
            "contexts/release", new { contextId = chatId }, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task EnsureAuthorizedAsync(bool interactive, CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        var first = true;
        while (true)
        {
            using var response = await _http.PostAsJsonAsync(
                "authorize", new { interactive, first }, JsonOptions, cancellationToken);
            first = false;
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await response.Content.ReadAsStringAsync(cancellationToken));
            var state = await response.Content.ReadFromJsonAsync<AuthorizationState>(JsonOptions, cancellationToken);
            if (state?.Authorized == true) return;
            if (state?.Dismissed == true)
                throw new BrowserAuthorizationRequiredException("Browser login was closed before authorization completed.");
            if (!interactive)
                throw new BrowserAuthorizationRequiredException("Browser authorization is required.");
            await Task.Delay(1000, cancellationToken);
        }
    }

    private sealed record AuthorizationState(bool Authorized, bool Dismissed = false);

    private sealed class ContextView(ElectronBrowserTransport owner, string chatId, bool ephemeral)
        : IChatBrowserTransport
    {
        public string Name => owner.Name;
        public Task InitializeAsync(BrowserTransportOptions options, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A browser context cannot initialize the host.");
        public Task<TResult> InvokeAsync<TResult>(string operation, object? arguments = null,
            CancellationToken cancellationToken = default) =>
            owner.InvokeCoreAsync<TResult>(operation, arguments, null, cancellationToken, chatId, ephemeral);
        public Task<TResult> InvokeWithProgressAsync<TResult>(string operation, object? arguments,
            Action<BrowserOperationProgress>? reportProgress, CancellationToken cancellationToken = default) =>
            owner.InvokeCoreAsync<TResult>(operation, arguments, reportProgress, cancellationToken, chatId, ephemeral);
        public Task<TResult> InvokeWithEventsAsync<TResult>(string operation, object? arguments,
            Action<JsonElement> reportEvent, Action<BrowserOperationProgress>? reportProgress = null,
            CancellationToken cancellationToken = default) =>
            owner.InvokeCoreAsync<TResult>(operation, arguments, reportProgress,
                cancellationToken, chatId, ephemeral, reportEvent);
        public Task ShowAsync(CancellationToken cancellationToken = default) => owner.ShowAsync(cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public async Task ShowAsync(CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        using var response = await _http.PostAsync("show", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        var process = _process;
        _process = null;
        if (process is null)
            return;

        if (!process.HasExited)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _http.PostAsync("shutdown", content: null, timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
        }

        if (_stdoutTask is not null)
            await IgnoreFailureAsync(_stdoutTask);
        if (_stderrTask is not null)
            await IgnoreFailureAsync(_stderrTask);
        process.Dispose();
        _http.Dispose();
    }

    private async Task ReadStdoutAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    Console.Error.WriteLine($"Electron stdout: {line}");
                    continue;
                }

                using (document)
                {
                    var root = document.RootElement;
                    var eventName = root.TryGetProperty("event", out var eventValue)
                        ? eventValue.GetString()
                        : null;
                    if (eventName == "ready" && root.TryGetProperty("port", out var port))
                        _ready.TrySetResult(new Uri($"http://127.0.0.1:{port.GetInt32()}/"));
                    else if (eventName == "error")
                    {
                        var message = root.TryGetProperty("error", out var errorValue)
                            ? errorValue.GetString() ?? "Electron initialization failed."
                            : "Electron initialization failed.";
                        var code = root.TryGetProperty("code", out var codeValue)
                            ? codeValue.GetString()
                            : null;
                        _ready.TrySetException(string.Equals(
                            code,
                            "authorization_required",
                            StringComparison.OrdinalIgnoreCase)
                            ? new BrowserAuthorizationRequiredException(message)
                            : new InvalidOperationException(message));
                    }
                }
            }

            if (!_ready.Task.IsCompleted)
            {
                _ready.TrySetException(new InvalidOperationException(
                    $"Electron exited before initialization with code " +
                    $"{(process.HasExited ? process.ExitCode : -1)}."));
            }
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
        }
    }

    private static async Task ReadStderrAsync(Process process)
    {
        while (await process.StandardError.ReadLineAsync() is { } line)
            Console.Error.WriteLine($"Electron: {line}");
    }

    private void EnsureRunning()
    {
        if (_process is null || _process.HasExited || _http.BaseAddress is null)
            throw new InvalidOperationException("Electron process is not running.");
    }

    private sealed record ProviderStopState(string Outcome);
    private sealed record ProviderOperationStarted(string OperationId);
    private sealed record ProviderOperationState(
        string Status,
        JsonElement? Result,
        string? Error,
        string? Code,
        BrowserOperationProgress? Progress,
        IReadOnlyList<ProviderOperationEvent>? Events = null,
        long LastSequence = 0);
    private sealed record ProviderOperationEvent(long Sequence, JsonElement Value);

    private static async Task IgnoreFailureAsync(Task task)
    {
        try { await task; }
        catch { }
    }
}
