using Mezhs.Browser;
using Mezhs.Configuration;
using Mezhs.Services;

namespace Mezhs.Providers.Gemini;

public sealed class GeminiGuestProvider(
    ConnectionOptions connection,
    MezhsOptions options,
    ChatStore store) : WebChatProvider(connection, options, store)
{
    public override string Name => "Gemini Guest";
    protected override string AutomationId => "gemini";

    public override async Task<ProviderSendResult> SendMessageAsync(
        ProviderSendContext context,
        CancellationToken cancellationToken = default)
    {
        var sessionPath = CreateSessionPath();
        var transport = CreateTransport();
        try
        {
            await transport.InitializeAsync(TransportOptions(
                sessionPath,
                showBrowser: false,
                requireLogin: false), cancellationToken);
            var response = await transport.SendPromptAsync(new BrowserPromptRequest(
                ComposeConversation(context),
                NewChat: true), cancellationToken);
            if (!response.Ok)
                throw new InvalidOperationException(response.Error ?? "Guest request failed.");
            return new ProviderSendResult(response.Text);
        }
        finally
        {
            await transport.DisposeAsync();
            TryDeleteSession(sessionPath);
        }
    }

    public sealed class Factory : ChatProviderFactory
    {
        public Factory() : base("gemini-web") { }

        public override void Validate(ConnectionOptions connection)
        {
            if (!string.IsNullOrWhiteSpace(connection.Workspace))
                throw new InvalidOperationException(
                    $"workspace is not supported by connection '{connection.Id}'.");
        }

        public override IChatProvider Create(
            ConnectionOptions connection,
            MezhsOptions options,
            ChatStore store) => new GeminiGuestProvider(connection, options, store);
    }
}
