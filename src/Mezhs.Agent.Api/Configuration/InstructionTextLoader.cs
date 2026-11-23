using System.Text.RegularExpressions;
using Mezhs.Configuration;
using YamlDotNet.RepresentationModel;

namespace Mezhs.Agent.Configuration;

public static class InstructionTextLoader
{
    // Instructions are always alongside the selected Agent configuration.
    public static IReadOnlyDictionary<string, string> Load(ConfigSource agentConfig)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(agentConfig.Path))!;
        var path = Path.Combine(directory, "instructions.yaml");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return result; // Legacy policies with no references need no sidecar.

        try
        {
            var root = YamlConfig.LoadOverridableYaml(
                new ConfigSource(path, agentConfig.Overridable)).Root;
            if (
                !root.Children.TryGetValue(new YamlScalarNode("texts"), out var textsNode) ||
                textsNode is not YamlMappingNode texts)
                throw new InvalidOperationException("Expected a single YAML document with a 'texts' mapping.");

            foreach (var entry in texts.Children)
            {
                if (entry.Key is not YamlScalarNode idNode || idNode.Value is not { } id ||
                    !Regex.IsMatch(id, "^[a-z][a-z0-9_-]*$", RegexOptions.CultureInvariant))
                    throw new InvalidOperationException("Instruction text IDs must be lowercase alphanumeric names (with _ or -).");
                if (entry.Value is not YamlScalarNode valueNode || string.IsNullOrWhiteSpace(valueNode.Value))
                    throw new InvalidOperationException($"Instruction text '{id}' must have a nonempty text value.");
                if (!result.TryAdd(id, valueNode.Value.Trim()))
                    throw new InvalidOperationException($"Duplicate instruction text '{id}'.");
            }
        }
        catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Invalid instructions file '{path}': {ex.Message}", ex);
        }
        return result;
    }
}
