using Mezhs.Integrations;

namespace Mezhs.Integrations.Browser;

// Account-specific virtual operations share the existing BrowserAccountSession.
// BrowserIntegrationBase retains ownership of transport lifecycle and send limits.
public abstract class BrowserAccountIntegrationBase(
    IntegrationConnection connection, IIntegrationHost host)
    : BrowserIntegrationBase(connection, host), IConversationContextOwner
{
    protected BrowserAccountSession AccountSession => CreateAccountSession();

    public override Task LoginAsync(CancellationToken cancellationToken = default) =>
        AccountSession.LoginAsync(cancellationToken);

    public override Task OpenBrowserAsync(CancellationToken cancellationToken = default) =>
        AccountSession.OpenBrowserAsync(cancellationToken);

    public override Task<System.Text.Json.JsonElement> DiagnoseAsync(
        string operation,
        System.Text.Json.JsonElement arguments,
        CancellationToken cancellationToken = default) =>
        BrowserDiagnostics.InvokeAsync(AccountSession, operation, arguments, cancellationToken);

    public Task RetainConversationAsync(string chatId) =>
        AccountSession.RetainConversationAsync(chatId);

    public Task ReleaseConversationAsync(string chatId) =>
        AccountSession.ReleaseConversationAsync(chatId);
}
