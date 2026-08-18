using System.Reflection;
using Mezhs.Integrations.Browser;

namespace Mezhs.Integrations.Grok;

/// <summary>
/// Connects a persistent Grok account to Mezhs and delegates provider-browser work
/// to the embedded Grok browser module.
/// </summary>
[Integration("grok-web-account")]
public sealed class GrokAccountIntegration : BrowserIntegrationBase
{
    private readonly BrowserAccountSession _session;
    private readonly ILoginModule _login;
    private readonly IModelModule _models;

    public GrokAccountIntegration(
        IntegrationConnection connection,
        IIntegrationHost host) : base(Validate(connection), host)
    {
        _session = CreateAccountSession();
        _login = new LoginModule(_session);
        _models = new ModelModule(_session);
    }

    protected override Assembly BrowserModuleAssembly => typeof(GrokAccountIntegration).Assembly;
    protected override string BrowserModuleResourceName => "Mezhs.Integrations.Grok.BrowserModule";

    public override ILoginModule Login => _login;
    public override IModelModule Models => _models;

    public override Task<IntegrationSendResult> SendMessageAsync(
        IntegrationSendContext context,
        CancellationToken cancellationToken = default) =>
        _session.UseAsync(async (transport, token) =>
        {
            var response = await transport.InvokeAsync<GrokSendResponse>(
                "newChat",
                new GrokSendRequest(
                    ComposeConversation(context),
                    context.Message.Model),
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
        string? Model);

    private sealed record GrokSendResponse(
        string Text,
        string? ChatUrl);

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
                await transport.InvokeAsync<IntegrationModel[]>(
                    "getModels",
                    cancellationToken: token),
                cancellationToken);
    }
}
