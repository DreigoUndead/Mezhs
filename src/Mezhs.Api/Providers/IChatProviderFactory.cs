using Mezhs.Configuration;
using Mezhs.Services;

namespace Mezhs.Providers;

public interface IChatProviderFactory
{
    IReadOnlyCollection<string> Types { get; }
    void Validate(ConnectionOptions connection);
    IChatProvider Create(ConnectionOptions connection, MezhsOptions options, ChatStore store);
}

public abstract class ChatProviderFactory(params string[] types) : IChatProviderFactory
{
    public IReadOnlyCollection<string> Types { get; } = types;
    public virtual void Validate(ConnectionOptions connection) { }
    public abstract IChatProvider Create(
        ConnectionOptions connection,
        MezhsOptions options,
        ChatStore store);
}
