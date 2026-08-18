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
    private readonly BrowserAccountSession _session;
    private readonly ILoginModule _login;
    private readonly IModelModule _models;

    public ChatGptAccountIntegration(
        IntegrationConnection connection,
        IIntegrationHost host) : base(connection, host)
    {
        _session = CreateAccountSession();
        _login = new LoginModule(_session);
        _models = new ModelModule(_session);
    }

    public override IntegrationCapabilities Capabilities => new(
        FileInput: true,
        ImageInput: true,
        FileOutput: true,
        ImageOutput: true);
    public override ILoginModule Login => _login;
    public override IModelModule Models => _models;

    public override Task<IntegrationSendResult> SendMessageAsync(
        IntegrationSendContext context,
        CancellationToken cancellationToken = default) =>
        _session.UseAsync(async (transport, token) =>
        {
            var hasRemoteConversation =
                !string.IsNullOrWhiteSpace(context.Chat.RemoteConversationId) &&
                !string.IsNullOrWhiteSpace(context.Chat.RemoteParentMessageId);
            var workspace = Connection.GetSetting("workspace");
            var files = context.Files.Select(file => new ChatGptInputFile(
                file.Path,
                file.Name,
                file.ContentType)).ToArray();

            async Task<(ChatGptSendResponse Response, string? ProjectId)> SendAsync(bool newChat)
            {
                var projectId = newChat
                    ? await ResolveProjectIdAsync(transport, workspace, token)
                    : null;
                var request = new ChatGptSendRequest(
                    newChat ? ComposeConversation(context) : context.Message.Content,
                    newChat ? null : context.Chat.RemoteConversationId,
                    newChat ? null : context.Chat.RemoteParentMessageId,
                    projectId,
                    context.Message.Model,
                    files);
                var response = await transport.InvokeAsync<ChatGptSendResponse>(
                    newChat ? "newChat" : "send",
                    request,
                    token);
                return (response, projectId);
            }

            var sent = await SendAsync(newChat: !hasRemoteConversation);
            if (hasRemoteConversation && sent.Response.ConversationUnavailable)
            {
                Console.Error.WriteLine(
                    $"ChatGPT conversation '{context.Chat.RemoteConversationId}' is unavailable; starting a new remote conversation from local history.");
                sent = await SendAsync(newChat: true);
            }

            if (sent.ProjectId is not null &&
                !string.Equals(sent.Response.ProjectId, sent.ProjectId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"ChatGPT created the chat outside project '{workspace}'.");

            return new IntegrationSendResult(
                sent.Response.Text,
                sent.Response.ChatUrl,
                sent.Response.ConversationId,
                sent.Response.ParentMessageId,
                OutputFiles(sent.Response.Artifacts),
                sent.Response.Model);
        }, cancellationToken);

    public override ValueTask DisposeAsync() => _session.DisposeAsync();

    private static async Task<string?> ResolveProjectIdAsync(
        IChatBrowserTransport transport,
        string? projectName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            return null;

        var projects = await transport.InvokeAsync<ChatGptProject[]>(
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
        string? Model,
        IReadOnlyList<ChatGptInputFile> Files);

    private sealed record ChatGptSendResponse(
        string Text,
        string ConversationId,
        string ParentMessageId,
        string? ProjectId,
        string? ChatUrl,
        IReadOnlyList<BrowserArtifact>? Artifacts,
        string? Model = null,
        bool ConversationUnavailable = false);

    private sealed class LoginModule(BrowserAccountSession session) : ILoginModule
    {
        public Task LoginAsync(CancellationToken cancellationToken = default) =>
            session.LoginAsync(cancellationToken);
    }

    private sealed class ModelModule(BrowserAccountSession session) : IModelModule
    {
        public Task<IReadOnlyList<IntegrationModel>> GetModelsAsync(
            CancellationToken cancellationToken = default) =>
            session.UseAuthorizedAsync<IReadOnlyList<IntegrationModel>>(async (transport, token) =>
            {
                var discovered = await transport.InvokeAsync<IntegrationModel[]>(
                    "getModels",
                    cancellationToken: token);
                return discovered
                    .Where(model => !string.IsNullOrWhiteSpace(model.Id) &&
                                    !string.IsNullOrWhiteSpace(model.Name))
                    .DistinctBy(model => model.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }, cancellationToken);
    }
}
