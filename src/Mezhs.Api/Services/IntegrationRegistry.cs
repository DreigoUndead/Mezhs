using System.Reflection;
using System.Runtime.Loader;
using Mezhs.Configuration;
using Mezhs.Integrations;

namespace Mezhs.Services;

public sealed class IntegrationRegistry : IAsyncDisposable
{
    private readonly Dictionary<string, IChatIntegration> _integrations;

    public IntegrationRegistry(
        MezhsOptions options,
        IIntegrationHost host)
    {
        var factories = DiscoverFactories();
        _integrations = options.Connections.ToDictionary(
            connection => connection.Id,
            connection => Create(connection, host, factories),
            StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGet(string connectionId, out IChatIntegration integration) =>
        _integrations.TryGetValue(connectionId, out integration!);

    public IChatIntegration Get(string connectionId) =>
        TryGet(connectionId, out var integration)
            ? integration
            : throw new KeyNotFoundException($"Connection '{connectionId}' was not found.");

    public object[] GetConnections() => _integrations.Values.Select(integration => new
    {
        id = integration.Connection.Id,
        name = integration.Connection.Name,
        integration = integration.Connection.Type,
        integrationName = integration.Name,
        requiresLogin = integration.Login is not null,
        workspace = integration.Connection.GetSetting("workspace"),
        capabilities = integration.Capabilities
    }).Cast<object>().ToArray();

    public async ValueTask DisposeAsync()
    {
        foreach (var integration in _integrations.Values)
            await integration.DisposeAsync();
    }

    private static IChatIntegration Create(
        ConnectionOptions configured,
        IIntegrationHost host,
        IReadOnlyDictionary<string, IIntegrationFactory> factories)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(configured.Workspace))
            settings["workspace"] = configured.Workspace;
        var connection = new IntegrationConnection(
            configured.Id,
            configured.Name,
            configured.Integration,
            settings);

        if (!factories.TryGetValue(connection.Type, out var factory))
            throw new InvalidOperationException(
                $"Unsupported integration '{connection.Type}' on connection '{connection.Id}'.");
        factory.Validate(connection);
        return factory.Create(connection, host);
    }

    private static IReadOnlyDictionary<string, IIntegrationFactory> DiscoverFactories()
    {
        var result = new Dictionary<string, IIntegrationFactory>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in DiscoverIntegrationAssemblies())
        {
            foreach (var type in GetLoadableTypes(assembly)
                         .Where(type => !type.IsAbstract && typeof(IIntegrationFactory).IsAssignableFrom(type)))
            {
                if (Activator.CreateInstance(type) is not IIntegrationFactory factory)
                    continue;
                foreach (var integrationType in factory.Types)
                {
                    if (!result.TryAdd(integrationType, factory))
                        throw new InvalidOperationException(
                            $"Multiple integration factories registered type '{integrationType}'.");
                }
            }
        }

        if (result.Count == 0)
            throw new InvalidOperationException(
                $"No integration plugins were found in '{AppContext.BaseDirectory}'.");
        return result;
    }

    private static IEnumerable<Assembly> DiscoverIntegrationAssemblies()
    {
        foreach (var path in Directory.EnumerateFiles(
                     AppContext.BaseDirectory,
                     "Mezhs.Integrations.*.dll",
                     SearchOption.TopDirectoryOnly))
        {
            var fullPath = Path.GetFullPath(path);
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly =>
                !assembly.IsDynamic &&
                !string.IsNullOrWhiteSpace(assembly.Location) &&
                string.Equals(Path.GetFullPath(assembly.Location), fullPath, StringComparison.OrdinalIgnoreCase));
            yield return loaded ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}
