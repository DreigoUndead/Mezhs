using System.Reflection;
using Mezhs.Browser;
using Mezhs.Integrations.Browser;

namespace Mezhs.Integrations.ChatGpt;

public class ChatGptWebIntegration(
    IntegrationConnection connection,
    IBrowserIntegrationHost host) : BrowserIntegrationBase(connection, host)
{
    public override string Name => "ChatGPT Web";
    protected override Assembly BrowserModuleAssembly => typeof(ChatGptWebIntegration).Assembly;
    protected override string BrowserModuleResourceName => "Mezhs.Integrations.ChatGpt.BrowserModule";

    public override Task<IntegrationSendResult> SendMessageAsync(
        IntegrationSendContext context,
        CancellationToken cancellationToken = default) =>
        SendAnonymousAsync(context, cancellationToken);
}

public sealed class ChatGptAccountIntegration : ChatGptWebIntegration
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILoginModule _login;
    private IChatBrowserTransport? _transport;
    private CancellationTokenSource? _idleCancellation;
    private bool _disposed;

    public ChatGptAccountIntegration(
        IntegrationConnection connection,
        IBrowserIntegrationHost host) : base(connection, host)
    {
        _login = new LoginModule(this);
    }

    public override string Name => "ChatGPT Account";
    public override IntegrationCapabilities Capabilities => new(
        FileInput: true,
        ImageInput: true,
        FileOutput: true,
        ImageOutput: true);
    public override ILoginModule Login => _login;

    public override async Task<IntegrationSendResult> SendMessageAsync(
        IntegrationSendContext context,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            CancelIdle();
            await EnsureAuthorizedTransportAsync(cancellationToken);
            var response = await _transport!.SendPromptAsync(new BrowserPromptRequest(
                context.Message.Content,
                NewChat: string.IsNullOrWhiteSpace(context.Chat.RemoteChatUrl),
                ChatUrl: context.Chat.RemoteChatUrl,
                Workspace: string.IsNullOrWhiteSpace(context.Chat.RemoteChatUrl)
                    ? Connection.GetSetting("workspace")
                    : null,
                FilePaths: context.Files.Select(file => file.Path).ToArray()), cancellationToken);
            if (!response.Ok)
                throw new InvalidOperationException(response.Error ?? "Chat request failed.");
            var outputFiles = await DownloadArtifactsAsync(response.Artifacts, cancellationToken);
            return new IntegrationSendResult(response.Text, response.ChatUrl, Files: outputFiles);
        }
        finally
        {
            if (!_disposed)
                ScheduleIdle();
            _gate.Release();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        CancelIdle();
        await _gate.WaitAsync();
        try
        {
            if (_transport is not null)
                await _transport.DisposeAsync();
            _transport = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            CancelIdle();
            await EnsureInteractiveLoginAsync(cancellationToken);
        }
        finally
        {
            if (!_disposed)
                ScheduleIdle();
            _gate.Release();
        }
    }

    private async Task EnsureAuthorizedTransportAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureTransportAsync(
                showBrowser: false,
                requireAuthorization: true,
                disableWebAuthn: false,
                cancellationToken);
        }
        catch (BrowserAuthorizationRequiredException)
        {
            await EnsureInteractiveLoginAsync(cancellationToken);
        }
    }

    private async Task EnsureInteractiveLoginAsync(CancellationToken cancellationToken)
    {
        if (_transport is not null)
        {
            await _transport.DisposeAsync();
            _transport = null;
        }
        await EnsureTransportAsync(
            showBrowser: true,
            requireAuthorization: true,
            disableWebAuthn: true,
            cancellationToken);
    }

    private async Task EnsureTransportAsync(
        bool showBrowser,
        bool requireAuthorization,
        bool disableWebAuthn,
        CancellationToken cancellationToken)
    {
        if (_transport is not null) return;
        _transport = Host.CreateBrowserTransport();
        try
        {
            await _transport.InitializeAsync(TransportOptions(
                PersistentProfilePath,
                showBrowser,
                requireAuthorization,
                disableWebAuthn), cancellationToken);
        }
        catch
        {
            await _transport.DisposeAsync();
            _transport = null;
            throw;
        }
    }

    private async Task<IReadOnlyList<IntegrationOutputFile>> DownloadArtifactsAsync(
        IReadOnlyList<BrowserArtifact>? artifacts,
        CancellationToken cancellationToken)
    {
        if (artifacts is null || artifacts.Count == 0)
            return [];

        var files = new List<IntegrationOutputFile>();
        foreach (var artifact in artifacts)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(artifact.LocalPath) && File.Exists(artifact.LocalPath))
                {
                    files.Add(new IntegrationOutputFile(
                        artifact.LocalPath,
                        SanitizeFileName(artifact.Name),
                        artifact.ContentType ?? "application/octet-stream"));
                    continue;
                }
                var response = await _transport!.SendWebRequestAsync(new BrowserWebRequest(
                    artifact.Url,
                    Headers: new Dictionary<string, string> { ["Accept"] = "*/*" },
                    Base64Response: true), cancellationToken);
                if (response.Status is < 200 or >= 300 || !response.BodyIsBase64)
                    continue;

                var directory = Path.Combine(Path.GetTempPath(), "mezhs", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var name = SanitizeFileName(artifact.Name);
                var path = Path.Combine(directory, name);
                await File.WriteAllBytesAsync(path, Convert.FromBase64String(response.Body), cancellationToken);
                var contentType = artifact.ContentType
                    ?? response.Headers.FirstOrDefault(header =>
                        header.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase)).Value
                    ?? "application/octet-stream";
                files.Add(new IntegrationOutputFile(path, name, contentType));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not import browser artifact '{artifact.Name}': {ex.Message}");
            }
        }
        return files;
    }

    private void ScheduleIdle()
    {
        CancelIdle();
        if (Host.BrowserIdleMinutes == 0 || _disposed) return;
        var cancellation = new CancellationTokenSource();
        _idleCancellation = cancellation;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Host.BrowserIdleMinutes), cancellation.Token);
                await _gate.WaitAsync(cancellation.Token);
                try
                {
                    if (ReferenceEquals(_idleCancellation, cancellation) && _transport is not null)
                    {
                        await _transport.DisposeAsync();
                        _transport = null;
                        _idleCancellation = null;
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(_idleCancellation, cancellation))
                    _idleCancellation = null;
                cancellation.Dispose();
            }
        });
    }

    private void CancelIdle()
    {
        var cancellation = _idleCancellation;
        _idleCancellation = null;
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ChatGptAccountIntegration));
    }

    private static string SanitizeFileName(string value)
    {
        var name = Path.GetFileName(value);
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "download" : name;
    }

    private sealed class LoginModule(ChatGptAccountIntegration owner) : ILoginModule
    {
        public Task LoginAsync(CancellationToken cancellationToken = default) =>
            owner.LoginAsync(cancellationToken);
    }
}

public sealed class ChatGptIntegrationFactory()
    : IntegrationFactory("chatgpt-web", "chatgpt-web-account")
{
    public override void Validate(IntegrationConnection connection)
    {
        if (connection.Type.Equals("chatgpt-web", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(connection.GetSetting("workspace")))
            throw new InvalidOperationException(
                $"workspace is not supported by connection '{connection.Id}'.");
    }

    public override IChatIntegration Create(
        IntegrationConnection connection,
        IIntegrationHost host)
    {
        var browserHost = BrowserIntegrationHost.Require(host);
        return connection.Type.ToLowerInvariant() switch
        {
            "chatgpt-web" => new ChatGptWebIntegration(connection, browserHost),
            "chatgpt-web-account" => new ChatGptAccountIntegration(connection, browserHost),
            _ => throw new InvalidOperationException($"Unsupported ChatGPT integration type '{connection.Type}'.")
        };
    }
}
