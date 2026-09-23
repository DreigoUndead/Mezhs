using System.ComponentModel.DataAnnotations;
using Mezhs.Agent.Policy;
using Mezhs.Configuration;
using YamlDotNet.RepresentationModel;

namespace Mezhs.Agent.Configuration;

public static class AgentConfigLoader
{
    public static AgentOptions Load(string path)
    {
        var options = MezhsConfigLoader.Load<AgentOptions>(path);
        var configDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;

        if (!Uri.TryCreate(options.Server.Listen, UriKind.Absolute, out var listen) || !listen.IsLoopback)
            throw new InvalidOperationException(
                "server.listen must use a loopback address. Host-shell Agent API cannot be exposed directly to the network.");

        options.AgentStorage = Resolve(configDirectory, options.AgentStorage);
        options.Workspace = Resolve(configDirectory, options.Workspace);
        if (!Directory.Exists(options.Workspace))
            throw new InvalidOperationException($"workspace directory '{options.Workspace}' does not exist.");

        Validate(options, "agent");
        Validate(options.Runtime, "runtime");
        Validate(options.Messages, "messages");

        var yaml = new YamlStream();
        using (var reader = File.OpenText(path))
            yaml.Load(reader);
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidOperationException("MEŽS Agent configuration must contain one YAML mapping document.");

        options.Policies = new PolicyDecoder().DecodePolicies(
            RequiredMapping(root, "policies", "policies"));
        ValidatePolicyConnections(options.Policies, options.Connections);
        options.ManualChats = NormalizeManualChats(
            options.ManualChats,
            options.Policies,
            options.Connections);
        return options;
    }

    private static void Validate(object value, string path)
    {
        var context = new ValidationContext(value);
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(value, context, results, validateAllProperties: true))
            return;

        var error = results[0];
        var member = error.MemberNames.FirstOrDefault();
        var memberPath = string.IsNullOrWhiteSpace(member)
            ? path
            : $"{path}.{char.ToLowerInvariant(member[0])}{member[1..]}";
        throw new InvalidOperationException($"{memberPath}: {error.ErrorMessage}");
    }

    private static YamlMappingNode RequiredMapping(YamlMappingNode parent, string key, string path)
    {
        if (!parent.Children.TryGetValue(new YamlScalarNode(key), out var node))
            throw new InvalidOperationException($"{path} configuration is required.");
        return node as YamlMappingNode
            ?? throw new InvalidOperationException($"{path} must be a YAML mapping.");
    }

    private static Dictionary<string, AgentManualChatOptions> NormalizeManualChats(
        IReadOnlyDictionary<string, AgentManualChatOptions> configured,
        IReadOnlyDictionary<string, PolicyContext> policies,
        IReadOnlyList<ConnectionOptions> connections)
    {
        var result = new Dictionary<string, AgentManualChatOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawId, preset) in configured)
        {
            var id = rawId?.Trim() ?? string.Empty;
            if (id.Length == 0)
                throw new InvalidOperationException("manualChats contains an empty id.");
            if (!result.TryAdd(id, preset))
                throw new InvalidOperationException($"Duplicate manual chat id '{id}'.");

            Validate(preset, $"manualChats.{id}");
            preset.PolicyId = preset.PolicyId!.Trim();
            preset.ConnectionId = string.IsNullOrWhiteSpace(preset.ConnectionId)
                ? null
                : preset.ConnectionId.Trim();
            preset.Model = string.IsNullOrWhiteSpace(preset.Model) ? null : preset.Model.Trim();
            if (!policies.TryGetValue(preset.PolicyId, out var policy))
                throw new InvalidOperationException(
                    $"manualChats.{id}.policyId references unknown policy '{preset.PolicyId}'.");
            ValidateConnection(
                preset.ConnectionId ?? policy.ConnectionId,
                connections,
                $"manualChats.{id}.connectionId");
        }
        return result;
    }

    private static void ValidatePolicyConnections(
        IReadOnlyDictionary<string, PolicyContext> policies,
        IReadOnlyList<ConnectionOptions> connections)
    {
        foreach (var policy in policies.Values)
            ValidateConnection(policy.ConnectionId, connections, $"policies.{policy.Id}.connectionId");
    }

    private static void ValidateConnection(
        string connectionId,
        IReadOnlyList<ConnectionOptions> connections,
        string path)
    {
        if (connections.Any(connection =>
                string.Equals(connection.Id, connectionId, StringComparison.OrdinalIgnoreCase)))
            return;
        throw new InvalidOperationException(
            $"{path} references unknown connection '{connectionId}'.");
    }

    private static string Resolve(string baseDirectory, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));
}
