using Mezhs.Configuration;
using Mezhs.Services;

namespace Mezhs.Providers.ChatGpt;

public sealed class ChatGptFreeProvider(
    ConnectionOptions connection,
    MezhsOptions options,
    ChatStore store) : ChatGptSubscriptionProvider(connection, options, store)
{
    public override string Name => "ChatGPT Free";

    public new sealed class Factory : ChatProviderFactory
    {
        public Factory() : base("chatgpt-web-free") { }

        public override void Validate(ConnectionOptions connection) { }

        public override IChatProvider Create(
            ConnectionOptions connection,
            MezhsOptions options,
            ChatStore store) => new ChatGptFreeProvider(connection, options, store);
    }
}
