using System.Reflection;
using System.Text.Json;
using Mezhs.Browser;
using Mezhs.Integrations.Browser;

namespace Mezhs.Integrations.ChatGpt;

[Integration("chatgpt-web")]
public class ChatGptWebIntegration : BrowserIntegrationBase
{
    public ChatGptWebIntegration(
        IntegrationConnection connection,
        IIntegrationHost host) : base(Validate(connection), host)
    {
    }

    protected override Assembly BrowserModuleAssembly => typeof(ChatGptWebIntegration).Assembly;
    protected override string BrowserModuleResourceName => "Mezhs.Integrations.ChatGpt.BrowserModule";

    public override Task<IntegrationSendResult> SendMessageAsync(
        IntegrationSendContext context,
        CancellationToken cancellationToken = default) =>
        SendAnonymousAsync(context, cancellationToken);

    private static IntegrationConnection Validate(IntegrationConnection connection)
    {
        if (connection.Type.Equals("chatgpt-web", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(connection.GetSetting("workspace")))
            throw new InvalidOperationException(
                $"workspace is not supported by connection '{connection.Id}'.");
        return connection;
    }
}

[Integration("chatgpt-web-account")]
public sealed class ChatGptAccountIntegration : ChatGptWebIntegration
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILoginModule _login;
    private IChatBrowserTransport? _transport;
    private CancellationTokenSource? _idleCancellation;
    private Task? _idleTask;
    private bool _disposed;

    public ChatGptAccountIntegration(
        IntegrationConnection connection,
        IIntegrationHost host) : base(connection, host)
    {
        _login = new LoginModule(this);
    }

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
            var newChat = string.IsNullOrWhiteSpace(context.Chat.RemoteChatUrl);
            var projectId = newChat
                ? await ResolveProjectIdAsync(Connection.GetSetting("workspace"), cancellationToken)
                : null;
            var response = await _transport!.SendPromptAsync(new BrowserPromptRequest(
                newChat ? ComposeConversation(context) : context.Message.Content,
                NewChat: newChat,
                ChatUrl: context.Chat.RemoteChatUrl,
                WorkspaceId: projectId,
                FilePaths: context.Files.Select(file => file.Path).ToArray()), cancellationToken);
            if (!response.Ok)
                throw new InvalidOperationException(response.Error ?? "Chat request failed.");
            if (projectId is not null)
                await VerifyProjectAsync(response.ChatUrl, projectId, cancellationToken);
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
        var idleTask = _idleTask;
        CancelIdle();
        if (idleTask is not null)
            await idleTask;
        _idleTask = null;

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

    private async Task<string?> ResolveProjectIdAsync(
        string? projectName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            return null;

        var matches = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; page < 50; page++)
        {
            var url = "/backend-api/gizmos/snorlax/sidebar?conversations_per_gizmo=0";
            if (!string.IsNullOrWhiteSpace(cursor))
                url += $"&cursor={Uri.EscapeDataString(cursor)}";

            var response = await _transport!.SendWebRequestAsync(
                new BrowserWebRequest(url), cancellationToken);
            if (response.Status is < 200 or >= 300)
                throw new InvalidOperationException(
                    $"ChatGPT project list request failed with HTTP {response.Status}.");

            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (!TryReadProject(item, out var id, out var name))
                        continue;
                    if (id.StartsWith("g-p-", StringComparison.Ordinal) &&
                        name.Equals(projectName.Trim(), StringComparison.OrdinalIgnoreCase))
                        matches.Add(id);
                }
            }

            cursor = document.RootElement.TryGetProperty("cursor", out var cursorElement) &&
                     cursorElement.ValueKind == JsonValueKind.String
                ? cursorElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(cursor))
                break;
            if (page == 49)
                throw new InvalidOperationException("ChatGPT project list pagination did not terminate.");
        }

        if (matches.Count == 1)
            return matches.Single();

        Console.Error.WriteLine(matches.Count == 0
            ? $"ChatGPT project '{projectName}' was not found; using no project."
            : $"ChatGPT project '{projectName}' is ambiguous; using no project.");
        return null;
    }

    private async Task VerifyProjectAsync(
        string? chatUrl,
        string projectId,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(chatUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException(
                "ChatGPT created a response but did not return a conversation URL for project verification.");

        const string marker = "/c/";
        var markerIndex = uri.AbsolutePath.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
            throw new InvalidOperationException(
                "ChatGPT created a response but did not return a conversation URL for project verification.");
        var conversationId = uri.AbsolutePath[(markerIndex + marker.Length)..].Split('/')[0];
        if (string.IsNullOrWhiteSpace(conversationId))
            throw new InvalidOperationException(
                "ChatGPT created a response but did not return a conversation ID for project verification.");

        var response = await _transport!.SendWebRequestAsync(
            new BrowserWebRequest(
                $"/backend-api/conversation/{Uri.EscapeDataString(conversationId)}"),
            cancellationToken);
        if (response.Status is < 200 or >= 300)
            throw new InvalidOperationException(
                $"ChatGPT conversation verification failed with HTTP {response.Status}.");

        using var document = JsonDocument.Parse(response.Body);
        var actualProjectId = document.RootElement.TryGetProperty("gizmo_id", out var gizmoId) &&
                              gizmoId.ValueKind == JsonValueKind.String
            ? gizmoId.GetString()
            : null;
        if (!string.Equals(actualProjectId, projectId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"ChatGPT conversation was created outside the configured project " +
                $"(expected {projectId}, got {actualProjectId ?? "no project"}).");
    }

    private static bool TryReadProject(
        JsonElement item,
        out string id,
        out string name)
    {
        id = "";
        name = "";
        if (!item.TryGetProperty("gizmo", out var gizmo) ||
            gizmo.ValueKind != JsonValueKind.Object)
            return false;
        if (gizmo.TryGetProperty("gizmo", out var nested) &&
            nested.ValueKind == JsonValueKind.Object)
            gizmo = nested;
        if (!gizmo.TryGetProperty("id", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String ||
            !gizmo.TryGetProperty("display", out var display) ||
            display.ValueKind != JsonValueKind.Object ||
            !display.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
            return false;

        id = idElement.GetString() ?? "";
        name = nameElement.GetString() ?? "";
        return id.Length > 0 && name.Length > 0;
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
            cancellationToken);
    }

    private async Task EnsureTransportAsync(
        bool showBrowser,
        bool requireAuthorization,
        CancellationToken cancellationToken)
    {
        if (_transport is not null) return;
        _transport = Host.CreateBrowserTransport();
        try
        {
            await _transport.InitializeAsync(TransportOptions(
                PersistentProfilePath,
                showBrowser,
                requireAuthorization), cancellationToken);
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
        _idleTask = DisposeWhenIdleAsync(cancellation);
    }

    private async Task DisposeWhenIdleAsync(CancellationTokenSource cancellation)
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
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_idleCancellation, cancellation))
            {
                _idleCancellation = null;
                _idleTask = null;
            }
            cancellation.Dispose();
        }
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
