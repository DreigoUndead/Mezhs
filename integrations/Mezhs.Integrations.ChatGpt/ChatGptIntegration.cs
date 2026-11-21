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
    protected override string BrowserModuleEntryFileName => "entry.js";

    protected override Task<PromptResult> SendCoreAsync(
        ChatContext chat,
        PromptRequest request,
        CancellationToken cancellationToken = default) =>
        SendAnonymousAsync(chat, request, cancellationToken);

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
public sealed class ChatGptAccountIntegration : BrowserAccountIntegrationBase
{
    public ChatGptAccountIntegration(
        IntegrationConnection connection, IIntegrationHost host) : base(connection, host) { }

    protected override Assembly BrowserModuleAssembly => typeof(ChatGptAccountIntegration).Assembly;
    protected override string BrowserModuleResourceName => "Mezhs.Integrations.ChatGpt.BrowserModule";
    protected override string BrowserModuleEntryFileName => "entry.js";

    public override bool AllowFiles => true;
    public override string CreateMessageId() => Guid.NewGuid().ToString();

    // Uses the existing authorized Electron session; no separate browser/token owner.
    public Task<ChatGptModelCatalogProbe> ProbeTypedHttpAsync(
        Action<BrowserOperationProgress>? reportProgress = null,
        CancellationToken cancellationToken = default) =>
        AccountSession.UseAuthorizedAsync(
            (transport, token) => new BrowserHttpExecutor(transport).CallAsync(
                ChatGptTypedHttpEndpoints.Models, request: null,
                reportProgress, token),
            cancellationToken);

    public override Task<IReadOnlyList<IntegrationModel>> GetModelsAsync(
        CancellationToken cancellationToken = default) =>
        AccountSession.UseAuthorizedAsync<IReadOnlyList<IntegrationModel>>(async (transport, token) =>
        {
            var catalog = await new BrowserHttpExecutor(transport).CallAsync(
                ChatGptTypedHttpEndpoints.Models, request: null,
                cancellationToken: token);
            return catalog.ToSelectableModels();
        }, cancellationToken);

    // Browser protocol chooses the model per operation, never in connection-global C# state.
    protected override Task SetModelAsync(ChatContext chat, string model,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    protected override Task<PromptResult> SendCoreAsync(
        ChatContext chat, PromptRequest request,
        CancellationToken cancellationToken = default) =>
        AccountSession.UseAsync(async (transport, token) =>
        {
            var hasRemoteConversation =
                !string.IsNullOrWhiteSpace(chat.ProviderId) &&
                !string.IsNullOrWhiteSpace(chat.RemoteParentMessageId);
            var workspace = Connection.GetSetting("workspace");
            var files = request.Files.Select(file => new ChatGptInputFile(
                file.Path, file.Name, file.ContentType)).ToArray();

            async Task<(ChatGptSendResponse Response, string? ProjectId)> SendAsync(bool newChat)
            {
                if (newChat && !string.IsNullOrWhiteSpace(workspace))
                    chat.ReportActivity?.Invoke(new IntegrationActivity(
                        "preparing", "Resolving ChatGPT project."));
                var projectId = newChat
                    ? await ResolveProjectIdAsync(transport, workspace, token)
                    : null;
                var prompt = newChat && (chat.RestoreConversation || hasRemoteConversation)
                    ? await BuildRestorePromptAsync(chat, request.Text, token)
                    : request.Text;
                var payload = new ChatGptSendRequest(
                    prompt,
                    request.MessageId,
                    newChat ? null : chat.ProviderId,
                    newChat ? null : chat.RemoteParentMessageId,
                    projectId,
                    request.Model,
                    files);
                var response = await transport.InvokeWithProgressAsync<ChatGptSendResponse>(
                    newChat ? "newChat" : "send",
                    payload,
                    progress => chat.ReportActivity?.Invoke(new IntegrationActivity(
                        progress.State, progress.Detail, progress.Analysis)),
                    token);
                return (response, projectId);
            }

            var sent = await SendAsync(newChat: !hasRemoteConversation);
            if (hasRemoteConversation && sent.Response.ConversationUnavailable)
            {
                Console.Error.WriteLine(
                    $"ChatGPT conversation '{chat.ProviderId}' was unavailable before submission; restoring the new remote conversation from local history.");
                sent = await SendAsync(newChat: true);
            }

            if (sent.ProjectId is not null &&
                !string.Equals(sent.Response.ProjectId, sent.ProjectId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"ChatGPT created the chat outside project '{workspace}'.");

            if (string.IsNullOrWhiteSpace(sent.Response.ParentMessageId))
                throw new InvalidOperationException(
                    "ChatGPT did not return the final assistant message id.");

            return new PromptResult(
                sent.Response.Text,
                sent.Response.ParentMessageId,
                sent.Response.Model ?? request.Model ?? "default",
                sent.Response.ConversationId,
                OutputFiles(sent.Response.Artifacts),
                sent.Response.ParentMessageId,
                sent.Response.ChatUrl);
        }, cancellationToken,
        stage => chat.ReportActivity?.Invoke(new IntegrationActivity("preparing", stage)),
        chatId: chat.Id);

    private static async Task<string?> ResolveProjectIdAsync(
        IChatBrowserTransport transport,
        string? projectName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            return null;

        var executor = new BrowserHttpExecutor(transport);
        var projects = new List<ChatGptProject>();
        string? cursor = null;
        do
        {
            var page = await executor.CallAsync(
                ChatGptTypedHttpEndpoints.Projects(cursor), request: null,
                cancellationToken: cancellationToken);
            foreach (var item in page.Items ?? [])
            {
                var project = item.Gizmo?.Gizmo ?? item.Gizmo;
                var id = project?.Id ?? "";
                var name = project?.Display?.Name?.Trim() ?? "";
                if (id.StartsWith("g-p-", StringComparison.Ordinal) && name.Length > 0)
                    projects.Add(new ChatGptProject(id, name));
            }
            cursor = string.IsNullOrEmpty(page.Cursor) ? null : page.Cursor;
        } while (cursor is not null);
        var matches = projects
            .Where(project => project.Name.Equals(
                projectName.Trim(),
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 1)
            return matches[0].Id;

        throw new InvalidOperationException(matches.Length == 0
            ? $"ChatGPT project '{projectName}' was not found."
            : $"ChatGPT project '{projectName}' is ambiguous.");
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
        string MessageId,
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
}
