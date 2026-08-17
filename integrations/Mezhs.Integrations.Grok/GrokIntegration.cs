using System.Reflection;
using Mezhs.Integrations.Browser;

namespace Mezhs.Integrations.Grok;

[Integration("grok-web-account")]
public sealed class GrokAccountIntegration : BrowserIntegrationBase
{
    private readonly BrowserAccountSession _session;
    private readonly ILoginModule _login;

    public GrokAccountIntegration(
        IntegrationConnection connection,
        IIntegrationHost host) : base(Validate(connection), host)
    {
        _session = CreateAccountSession();
        _login = new LoginModule(_session);
    }

    protected override Assembly BrowserModuleAssembly => typeof(GrokAccountIntegration).Assembly;
    protected override string BrowserModuleResourceName => "Mezhs.Integrations.Grok.BrowserModule";

    public override ILoginModule Login => _login;

    public override Task<IntegrationSendResult> SendMessageAsync(
        IntegrationSendContext context,
        CancellationToken cancellationToken = default) =>
        _session.UseAsync(async (transport, token) =>
        {
            var newChat = string.IsNullOrWhiteSpace(context.Chat.RemoteChatUrl);
            var response = await transport.InvokeAsync<GrokSendResponse>(
                newChat ? "newChat" : "send",
                new GrokSendRequest(
                    newChat ? ComposeConversation(context) : context.Message.Content,
                    context.Chat.RemoteChatUrl),
                token);
            return new IntegrationSendResult(
                response.Text,
                response.ChatUrl);
        }, cancellationToken);

    public override ValueTask DisposeAsync() => _session.DisposeAsync();

    private static IntegrationConnection Validate(IntegrationConnection connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.GetSetting("workspace")))
            throw new InvalidOperationException(
                $"workspace is not supported by connection '{connection.Id}'.");
        return connection;
    }

    private sealed record GrokSendRequest(
        string Prompt,
        string? ChatUrl);

    private sealed record GrokSendResponse(
        string Text,
        string ChatUrl);

    private sealed class LoginModule(BrowserAccountSession session) : ILoginModule
    {
        public Task LoginAsync(CancellationToken cancellationToken = default) =>
            session.LoginAsync(cancellationToken);
    }
}
