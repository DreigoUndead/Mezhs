using System.Reflection;
using System.Runtime.Loader;
using Mezhs;
using Mezhs.Configuration;
using Mezhs.Integrations;

namespace Mezhs.Services;

public sealed class IntegrationRegistry : IAsyncDisposable
{
    private readonly Dictionary<string, IntegrationBase> _integrations;

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

    public bool TryGet(string connectionId, out IntegrationBase integration) =>
        _integrations.TryGetValue(connectionId, out integration!);

    public IntegrationBase Get(string connectionId) =>
        TryGet(connectionId, out var integration)
            ? integration
            : throw new ResourceNotFoundException($"Connection '{connectionId}' was not found.");

    public Task RetainConversationAsync(string connectionId, string chatId) =>
        Get(connectionId) is IConversationContextOwner owner
            ? owner.RetainConversationAsync(chatId)
            : Task.CompletedTask;

    public Task ReleaseConversationAsync(string connectionId, string chatId) =>
        Get(connectionId) is IConversationContextOwner owner
            ? owner.ReleaseConversationAsync(chatId)
            : Task.CompletedTask;

    public object[] GetConnections() => _integrations.Values.Select(integration => new
    {
        id = integration.Connection.Id,
        name = integration.Connection.Name,
        integration = integration.Connection.Type,
        requiresLogin = HasDirectOverride(integration, nameof(IntegrationBase.LoginAsync)),
        supportsDiagnostics = HasDirectOverride(integration, nameof(IntegrationBase.DiagnoseAsync)),
        defaultModel = integration.Connection.GetSetting("defaultModel"),
        workspace = integration.Connection.GetSetting("workspace"),
        allowFiles = integration.AllowFiles
    }).Cast<object>().ToArray();

    // A virtual method override expresses supported login/diagnostics. No second
    // capability interface or mutable feature-flag registry is maintained.
    public static bool HasLogin(IntegrationBase integration) =>
        HasDirectOverride(integration, nameof(IntegrationBase.LoginAsync));

    public static bool HasDiagnostics(IntegrationBase integration) =>
        HasDirectOverride(integration, nameof(IntegrationBase.DiagnoseAsync));

    private static bool HasDirectOverride(IntegrationBase integration, string methodName) =>
        integration.GetType().GetMethod(methodName)?.DeclaringType != typeof(IntegrationBase);

    public async ValueTask DisposeAsync()
    {
        foreach (var integration in _integrations.Values)
            await integration.DisposeAsync();
    }

    private static IntegrationBase Create(
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
            settings,
            configured.Limits);

        if (!registrations.TryGetValue(connection.Type, out var integrationType))
            throw new InvalidOperationException(
                $"Unsupported integration '{connection.Type}' on connection '{connection.Id}'.");

        try
        {
            var integration = Activator.CreateInstance(integrationType, connection, host) as IntegrationBase
                ?? throw new InvalidOperationException(
                    $"Integration '{connection.Type}' could not be constructed.");
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
                         .Where(type => !type.IsAbstract && typeof(IntegrationBase).IsAssignableFrom(type)))
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
