using Mezhs.Configuration;
using Mezhs.Services;
using System.Reflection;

namespace Mezhs.Providers;

public sealed class ProviderRegistry : IAsyncDisposable
{
    private readonly Dictionary<string, IChatProvider> _providers;

    public ProviderRegistry(MezhsOptions options, ChatStore store)
    {
        var factories = DiscoverFactories();
        _providers = options.Connections.ToDictionary(
            connection => connection.Id,
            connection => Create(connection, options, store, factories),
            StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGet(string connectionId, out IChatProvider provider) =>
        _providers.TryGetValue(connectionId, out provider!);

    public IChatProvider Get(string connectionId) =>
        TryGet(connectionId, out var provider)
            ? provider
            : throw new KeyNotFoundException($"Connection '{connectionId}' was not found.");

    public object[] GetConnections() => _providers.Values.Select(provider => new
    {
        id = provider.Connection.Id,
        name = provider.Connection.Name,
        provider = provider.Connection.Provider,
        providerName = provider.Name,
        requiresLogin = provider.RequiresLogin,
        workspace = provider.Connection.Workspace,
        capabilities = provider.Capabilities
    }).Cast<object>().ToArray();

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers.Values)
            await provider.DisposeAsync();
    }

    private static IChatProvider Create(
        ConnectionOptions connection,
        MezhsOptions options,
        ChatStore store,
        IReadOnlyDictionary<string, IChatProviderFactory> factories)
    {
        if (!factories.TryGetValue(connection.Provider, out var factory))
            throw new InvalidOperationException(
                $"Unsupported provider '{connection.Provider}' on connection '{connection.Id}'.");
        factory.Validate(connection);
        return factory.Create(connection, options, store);
    }

    private static IReadOnlyDictionary<string, IChatProviderFactory> DiscoverFactories()
    {
        var result = new Dictionary<string, IChatProviderFactory>(StringComparer.OrdinalIgnoreCase);
        var factoryTypes = Assembly.GetExecutingAssembly().GetTypes()
            .Where(type => !type.IsAbstract && typeof(IChatProviderFactory).IsAssignableFrom(type));
        foreach (var type in factoryTypes)
        {
            if (Activator.CreateInstance(type) is not IChatProviderFactory factory)
                continue;
            foreach (var providerType in factory.Types)
            {
                if (!result.TryAdd(providerType, factory))
                    throw new InvalidOperationException(
                        $"Multiple provider factories registered type '{providerType}'.");
            }
        }
        return result;
    }
}
