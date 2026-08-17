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
        var registrations = DiscoverRegistrations();
        _integrations = options.Connections.ToDictionary(
            connection => connection.Id,
            connection => Create(connection, host, registrations),
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
        requiresLogin = integration.Login is not null,
        supportsModels = integration.Models is not null,
        defaultModel = integration.Connection.GetSetting("defaultModel"),
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
        IReadOnlyDictionary<string, Type> registrations)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(configured.Workspace))
            settings["workspace"] = configured.Workspace;
        if (!string.IsNullOrWhiteSpace(configured.DefaultModel))
            settings["defaultModel"] = configured.DefaultModel;
        var connection = new IntegrationConnection(
            configured.Id,
            configured.Name,
            configured.Integration,
            settings);

        if (!registrations.TryGetValue(connection.Type, out var integrationType))
            throw new InvalidOperationException(
                $"Unsupported integration '{connection.Type}' on connection '{connection.Id}'.");

        try
        {
            var integration = Activator.CreateInstance(integrationType, connection, host) as IChatIntegration
                ?? throw new InvalidOperationException(
                    $"Integration '{connection.Type}' could not be constructed.");
            if (configured.DefaultModel is not null && integration.Models is null)
                throw new InvalidOperationException(
                    $"defaultModel is not supported by connection '{connection.Id}'.");
            return integration;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new InvalidOperationException(
                $"Connection '{connection.Id}' is invalid: {ex.InnerException.Message}",
                ex.InnerException);
        }
    }

    private static IReadOnlyDictionary<string, Type> DiscoverRegistrations()
    {
        var result = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in DiscoverIntegrationAssemblies())
        {
            foreach (var type in GetLoadableTypes(assembly)
                         .Where(type => !type.IsAbstract && typeof(IChatIntegration).IsAssignableFrom(type)))
            {
                foreach (var registration in type.GetCustomAttributes<IntegrationAttribute>())
                {
                    if (!result.TryAdd(registration.Type, type))
                        throw new InvalidOperationException(
                            $"Multiple integrations registered type '{registration.Type}'.");
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
