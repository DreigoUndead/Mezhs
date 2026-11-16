using System.Reflection;
using Mezhs.Integrations.Browser;

namespace Mezhs.Integrations.Grok;

/// <summary>
/// Persistent Grok account with a single session owner and per-chat isolated contexts.
/// The existing DOM provider protocol remains in place for this architecture update.
/// </summary>
[Integration("grok-web-account")]
public sealed class GrokAccountIntegration : BrowserAccountIntegrationBase
{
    public GrokAccountIntegration(
        IntegrationConnection connection, IIntegrationHost host)
        : base(Validate(connection), host) { }

    protected override Assembly BrowserModuleAssembly => typeof(GrokAccountIntegration).Assembly;
    protected override string BrowserModuleResourceName => "Mezhs.Integrations.Grok.BrowserModule";

    public override Task<IReadOnlyList<IntegrationModel>> GetModelsAsync(
        CancellationToken cancellationToken = default) =>
        AccountSession.UseAuthorizedAsync<IReadOnlyList<IntegrationModel>>(async (transport, token) =>
        {
            var discovered = await transport.InvokeAsync<IntegrationModel[]>(
                "getModels", cancellationToken: token);
            return [new IntegrationModel("default", "Default"), ..discovered
                .Where(model => !string.IsNullOrWhiteSpace(model.Id) &&
                                !string.IsNullOrWhiteSpace(model.Name) &&
                                !string.Equals(model.Id, "default", StringComparison.OrdinalIgnoreCase))
                .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)];
        }, cancellationToken);

    // Native browser module selects the model within the isolated chat operation.
    protected override Task SetModelAsync(ChatContext chat, string model,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    protected override Task<PromptResult> SendCoreAsync(
        ChatContext chat, PromptRequest request,
        CancellationToken cancellationToken = default) =>
        AccountSession.UseAsync(async (transport, token) =>
        {
            var prompt = await BuildRestorePromptAsync(chat, request.Text, token);
            var response = await transport.InvokeAsync<GrokSendResponse>(
                "newChat", new GrokSendRequest(prompt, request.Model, request.MessageId), token);
            return new PromptResult(
                response.Text, CreateMessageId(), request.Model ?? "default",
                RemoteChatUrl: response.ChatUrl);
        }, cancellationToken, chatId: chat.Id);

    private static IntegrationConnection Validate(IntegrationConnection connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.GetSetting("workspace")))
            throw new InvalidOperationException(
                $"workspace is not supported by connection '{connection.Id}'.");
        return connection;
    }

    private sealed record GrokSendRequest(string Prompt, string? Model, string MessageId);
    private sealed record GrokSendResponse(string Text, string? ChatUrl);
}
