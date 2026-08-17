using System.Reflection;
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

            var newChat = string.IsNullOrWhiteSpace(context.Chat.RemoteConversationId) ||
                          string.IsNullOrWhiteSpace(context.Chat.RemoteParentMessageId);
            var workspace = Connection.GetSetting("workspace");
            var projectId = newChat
                ? await ResolveProjectIdAsync(workspace, cancellationToken)
                : null;
            var request = new ChatGptSendRequest(
                newChat ? ComposeConversation(context) : context.Message.Content,
                context.Chat.RemoteConversationId,
                context.Chat.RemoteParentMessageId,
                projectId,
                context.Files.Select(file => new ChatGptInputFile(
                    file.Path,
                    file.Name,
                    file.ContentType)).ToArray());
            var response = await _transport!.InvokeAsync<ChatGptSendResponse>(
                newChat ? "newChat" : "send",
                request,
                cancellationToken);
            if (projectId is not null &&
                !string.Equals(response.ProjectId, projectId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"ChatGPT created the chat outside project '{workspace}'.");

            return new IntegrationSendResult(
                response.Text,
                response.ChatUrl,
                response.ConversationId,
                response.ParentMessageId,
                OutputFiles(response.Artifacts));
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

        var projects = await _transport!.InvokeAsync<ChatGptProject[]>(
            "getProjects",
            cancellationToken: cancellationToken);
        var matches = projects
            .Where(project => project.Name.Equals(
                projectName.Trim(),
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 1)
            return matches[0].Id;

        Console.Error.WriteLine(matches.Length == 0
            ? $"ChatGPT project '{projectName}' was not found; using no project."
            : $"ChatGPT project '{projectName}' is ambiguous; using no project.");
        return null;
    }

    private static IReadOnlyList<IntegrationOutputFile> OutputFiles(
        IReadOnlyList<BrowserArtifact>? artifacts)
    {
        if (artifacts is null)
            return [];

        return artifacts
            .Where(artifact => !string.IsNullOrWhiteSpace(artifact.LocalPath) &&
                               File.Exists(artifact.LocalPath))
            .Select(artifact => new IntegrationOutputFile(
                artifact.LocalPath!,
                SanitizeFileName(artifact.Name),
                artifact.ContentType ?? "application/octet-stream"))
            .ToArray();
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

    private sealed record ChatGptProject(string Id, string Name);

    private sealed record ChatGptInputFile(
        string Path,
        string Name,
        string ContentType);

    private sealed record ChatGptSendRequest(
        string Prompt,
        string? ConversationId,
        string? ParentMessageId,
        string? ProjectId,
        IReadOnlyList<ChatGptInputFile> Files);

    private sealed record ChatGptSendResponse(
        string Text,
        string ConversationId,
        string ParentMessageId,
        string? ProjectId,
        string? ChatUrl,
        IReadOnlyList<BrowserArtifact>? Artifacts);

    private sealed class LoginModule(ChatGptAccountIntegration owner) : ILoginModule
    {
        public Task LoginAsync(CancellationToken cancellationToken = default) =>
            owner.LoginAsync(cancellationToken);
    }
}
