using System.Reflection;
using Mezhs.Integrations.Browser;

namespace Mezhs.Integrations.Gemini;

[Integration("gemini-web")]
public class GeminiWebIntegration : BrowserIntegrationBase
{
    public GeminiWebIntegration(
        IntegrationConnection connection,
        IIntegrationHost host) : base(Validate(connection), host)
    {
    }

    protected override Assembly BrowserModuleAssembly => typeof(GeminiWebIntegration).Assembly;
    protected override string BrowserModuleResourceName => "Mezhs.Integrations.Gemini.BrowserModule";

    public override Task<IntegrationSendResult> SendMessageAsync(
        IntegrationSendContext context,
        CancellationToken cancellationToken = default) =>
        SendAnonymousAsync(context, cancellationToken);

    private static IntegrationConnection Validate(IntegrationConnection connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.GetSetting("workspace")))
            throw new InvalidOperationException(
                $"workspace is not supported by connection '{connection.Id}'.");
        return connection;
    }
}

[Integration("gemini-web-account")]
public sealed class GeminiAccountIntegration : GeminiWebIntegration
{
    private readonly ILoginModule _login;

    public GeminiAccountIntegration(IntegrationConnection connection, IIntegrationHost host)
        : base(connection, host)
    {
        _login = new LoginModule(CreateAccountSession());
    }

    public override ILoginModule Login => _login;

    public override Task<IntegrationSendResult> SendMessageAsync(
        IntegrationSendContext context,
        CancellationToken cancellationToken = default) =>
        CreateAccountSession().UseAsync(async (transport, token) =>
        {
            var response = await transport.InvokeAsync<GeminiSendResponse>(
                "sendPrompt",
                new { Prompt = (context with { RestoreConversation = true }).Prompt, NewChat = true, RequireAccount = true },
                token);
            if (!response.Ok)
                throw new InvalidOperationException(response.Error ?? "Gemini account request failed.");
            return new IntegrationSendResult(response.Text, response.ChatUrl);
        }, cancellationToken);

    private sealed record GeminiSendResponse(bool Ok, string Text, string? ChatUrl, string? Error);

    private sealed class LoginModule(BrowserAccountSession session) : ILoginModule
    {
        public Task LoginAsync(CancellationToken cancellationToken = default) =>
            session.LoginAsync(cancellationToken);

        public Task OpenBrowserAsync(CancellationToken cancellationToken = default) =>
            session.OpenBrowserAsync(cancellationToken);
    }
}
