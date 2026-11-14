using System.ComponentModel.DataAnnotations;
using Mezhs.Agent.Policy;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.RepresentationModel;

namespace Mezhs.Agent.Configuration;

public static class AgentConfigLoader
{
    public static AgentOptions Load(string path, IReadOnlyCollection<string>? coreConnectionIds = null)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("MEŽS Agent configuration file was not found.", path);
        var yaml = new YamlStream();
        using (var reader = File.OpenText(path))
            yaml.Load(reader);
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidOperationException("MEŽS Agent configuration must contain one YAML mapping document.");

        foreach (var key in root.Children.Keys.OfType<YamlScalarNode>())
        {
            if (key.Value is not null && new[] { "connections", "transport", "storage", "extensions" }
                    .Contains(key.Value, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"agent.yaml cannot declare '{key.Value}'; move Core configuration to mezhs.yaml.");
        }

        var options = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build().Deserialize<AgentOptions>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("MEŽS Agent configuration is empty.");
        if (options.Version != 1)
            throw new InvalidOperationException($"Unsupported Agent config version {options.Version}.");
        var configDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (!Uri.TryCreate(options.Server.Listen, UriKind.Absolute, out var listen) ||
            !listen.IsLoopback ||
            (listen.Scheme != Uri.UriSchemeHttp && listen.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException(
                "server.listen must use a loopback address. Host-shell Agent API cannot be exposed directly to the network.");

        options.AgentStorage = Resolve(configDirectory, options.AgentStorage);
        options.Workspace = Resolve(configDirectory, options.Workspace);
        if (!Directory.Exists(options.Workspace))
            throw new InvalidOperationException($"workspace directory '{options.Workspace}' does not exist.");

        Validate(options, "agent");
        Validate(options.Messages, "messages");

        options.Policies = new PolicyDecoder().DecodePolicies(
            RequiredMapping(root, "policies", "policies"));
        if (coreConnectionIds is not null)
            ValidatePolicyConnections(options.Policies, coreConnectionIds);
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

    private static void ValidatePolicyConnections(
        IReadOnlyDictionary<string, PolicyContext> policies,
        IReadOnlyCollection<string> connectionIds)
    {
        foreach (var policy in policies.Values)
            ValidateConnection(policy.ConnectionId, connectionIds, $"policies.{policy.Id}.connectionId");
    }

    private static void ValidateConnection(
        string connectionId,
        IReadOnlyList<ConnectionOptions> connections,
        string path)
    {
        if (connectionIds.Any(id =>
                string.Equals(id, connectionId, StringComparison.OrdinalIgnoreCase)))
            return;
        throw new InvalidOperationException(
            $"{path} references unknown connection '{connectionId}'.");
    }

    private static string Resolve(string baseDirectory, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));
}
