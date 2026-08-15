using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Mezhs.Browser;

namespace Mezhs.Browser.Electron;

public sealed class ElectronBrowserTransport(string electronDirectory) : IChatBrowserTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _electronDirectory = Path.GetFullPath(electronDirectory);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(6) };
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
        startInfo.Environment["MEZHS_PARENT_PROCESS_ID"] = Environment.ProcessId.ToString();

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start Electron.");
        _stdoutTask = ReadStdoutAsync(_process);
        _stderrTask = ReadStderrAsync(_process);

        var baseAddress = await _ready.Task.WaitAsync(cancellationToken);
        _http.BaseAddress = baseAddress;
    }

    public async Task<ChatTransportResponse> SendPromptAsync(
        BrowserPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        using var response = await _http.PostAsJsonAsync(
            "prompt",
            request,
            JsonOptions,
            cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<ChatTransportResponse>(
            JsonOptions,
            cancellationToken);
        if (response.IsSuccessStatusCode && result is not null)
            return result;

        var detail = result?.Error ?? await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"Electron bridge failed: {detail}");
    }

    public async Task<BrowserWebResponse> SendWebRequestAsync(
        BrowserWebRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        using var response = await _http.PostAsJsonAsync(
            "fetch",
            request,
            JsonOptions,
            cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<BrowserWebResponse>(
            JsonOptions,
            cancellationToken);
        if (response.IsSuccessStatusCode && result is not null)
            return result;

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"Electron web request failed: {detail}");
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

    private static async Task IgnoreFailureAsync(Task task)
    {
        try { await task; }
        catch { }
    }
}
