using System.Globalization;
using Mezhs.Agent.Policy;
using YamlDotNet.RepresentationModel;

namespace Mezhs.Agent.Configuration;

public static class AgentConfigLoader
{
    public static AgentOptions Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("MEŽS Agent configuration file was not found.", path);

        var yaml = new YamlStream();
        using (var reader = File.OpenText(path))
            yaml.Load(reader);
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidOperationException("MEŽS Agent configuration must contain one YAML mapping document.");

        EnsureOnlyKeys(root, "agent", "version", "listen", "mezhsApi", "storage", "messages", "policies");
        var versionText = RequiredValue(root, "version", "version");
        if (!int.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version != 1)
            throw new InvalidOperationException($"Unsupported Agent config version {versionText}.");

        var configDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return new AgentOptions
        {
            Listen = RequiredHttpUri(root, "listen", "listen"),
            MezhsApi = RequiredHttpUri(root, "mezhsApi", "mezhsApi"),
            Storage = Resolve(configDirectory, RequiredValue(root, "storage", "storage")),
            Messages = YamlModelMapper.Map<AgentRuntimeMessages>(
                RequiredMapping(root, "messages", "messages"),
                "messages"),
            Policies = new PolicyDecoder().DecodePolicies(
                RequiredMapping(root, "policies", "policies"))
        };
    }

    private static void EnsureOnlyKeys(YamlMappingNode mapping, string path, params string[] allowed)
    {
        var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var keyNode in mapping.Children.Keys)
        {
            var key = keyNode as YamlScalarNode;
            if (key?.Value is null)
                throw new InvalidOperationException($"{path} property names must be scalar values.");
            if (!allowedSet.Contains(key.Value))
                throw new InvalidOperationException($"Unknown configuration property '{key.Value}'.");
        }
    }

    private static YamlMappingNode RequiredMapping(YamlMappingNode parent, string key, string path)
    {
        var node = RequiredNode(parent, key, path);
        return node as YamlMappingNode
            ?? throw new InvalidOperationException($"{path} must be a YAML mapping.");
    }

    private static string RequiredValue(YamlMappingNode parent, string key, string path)
    {
        var node = RequiredNode(parent, key, path);
        var value = (node as YamlScalarNode)?.Value;
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{path} is required and must be a scalar value.");
        return value;
    }

    private static Uri RequiredHttpUri(YamlMappingNode parent, string key, string path)
    {
        var value = RequiredValue(parent, key, path);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException($"{path} must be an absolute HTTP or HTTPS URL.");
        return uri;
    }

    private static YamlNode RequiredNode(YamlMappingNode parent, string key, string path)
    {
        if (parent.Children.TryGetValue(new YamlScalarNode(key), out var node))
            return node;
        throw new InvalidOperationException($"{path} configuration is required.");
    }

    private static string Resolve(string baseDirectory, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));
}
