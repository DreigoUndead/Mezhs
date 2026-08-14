using Mezhs.Browser;
using Mezhs.Configuration;
using Mezhs.Services;

namespace Mezhs.Providers.ChatGpt;

public class ChatGptSubscriptionProvider(
    ConnectionOptions connection,
    MezhsOptions options,
    ChatStore store) : WebChatProvider(connection, options, store)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChatBrowserTransport? _transport;
    private CancellationTokenSource? _idleCancellation;

    public override string Name => "ChatGPT Subscription";
    protected override string AutomationId => "chatgpt";
    public override bool RequiresLogin => true;
    public override ProviderCapabilities Capabilities => new(
        FileInput: true,
        ImageInput: true,
        FileOutput: true,
        ImageOutput: true);

    public override async Task InitializeAsync(
        bool showBrowser,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            CancelIdle();
            await EnsureTransportAsync(showBrowser, cancellationToken);
            if (showBrowser)
                await _transport!.ShowAsync(cancellationToken);
        }
        finally
        {
            ScheduleIdle();
            _gate.Release();
        }
    }

    public override async Task<ProviderSendResult> SendMessageAsync(
        ProviderSendContext context,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            CancelIdle();
            await EnsureTransportAsync(showBrowser: false, cancellationToken);
            var response = await _transport!.SendPromptAsync(new BrowserPromptRequest(
                context.Message.Content,
                NewChat: string.IsNullOrWhiteSpace(context.Chat.RemoteChatUrl),
                ChatUrl: context.Chat.RemoteChatUrl,
                Workspace: string.IsNullOrWhiteSpace(context.Chat.RemoteChatUrl)
                    ? Connection.Workspace
                    : null,
                FilePaths: context.Files.Select(file => file.Path).ToArray()), cancellationToken);
            if (!response.Ok)
                throw new InvalidOperationException(response.Error ?? "Chat request failed.");
            var outputFiles = await DownloadArtifactsAsync(response.Artifacts, cancellationToken);
            return new ProviderSendResult(response.Text, response.ChatUrl, Files: outputFiles);
        }
        finally
        {
            ScheduleIdle();
            _gate.Release();
        }
    }

    public override async ValueTask DisposeAsync()
    {
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

    private async Task<IReadOnlyList<ProviderOutputFile>> DownloadArtifactsAsync(
        IReadOnlyList<BrowserArtifact>? artifacts,
        CancellationToken cancellationToken)
    {
        if (artifacts is null || artifacts.Count == 0)
            return [];

        var files = new List<ProviderOutputFile>();
        foreach (var artifact in artifacts)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(artifact.LocalPath) && File.Exists(artifact.LocalPath))
                {
                    files.Add(new ProviderOutputFile(
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
                files.Add(new ProviderOutputFile(path, name, contentType));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not import browser artifact '{artifact.Name}': {ex.Message}");
            }
        }
        return files;
    }

    private async Task EnsureTransportAsync(bool showBrowser, CancellationToken cancellationToken)
    {
        if (_transport is not null) return;
        _transport = CreateTransport();
        try
        {
            await _transport.InitializeAsync(TransportOptions(
                PersistentProfilePath,
                showBrowser,
                requireLogin: true), cancellationToken);
        }
        catch
        {
            await _transport.DisposeAsync();
            _transport = null;
            throw;
        }
    }

    private void ScheduleIdle()
    {
        CancelIdle();
        if (Options.Transport.IdleMinutes == 0) return;
        var cancellation = new CancellationTokenSource();
        _idleCancellation = cancellation;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Options.Transport.IdleMinutes), cancellation.Token);
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

    private static string SanitizeFileName(string value)
    {
        var name = Path.GetFileName(value);
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "download" : name;
    }

    public sealed class Factory : ChatProviderFactory
    {
        public Factory() : base("chatgpt-web-subscription") { }

        public override void Validate(ConnectionOptions connection) { }

        public override IChatProvider Create(
            ConnectionOptions connection,
            MezhsOptions options,
            ChatStore store) => new ChatGptSubscriptionProvider(connection, options, store);
    }
}
