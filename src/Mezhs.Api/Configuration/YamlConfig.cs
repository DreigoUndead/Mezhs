using System.Globalization;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace Mezhs.Configuration;

public static class YamlConfig
{
    public static LoadedYaml Load(ConfigSource source)
    {
        var root = ReadMapping(source.Path);
        if (source.OverridePath is { } overridePath && !ContainsValues(overridePath))
            return new(root, Serialize(root));
        if (source.OverridePath is { } populatedOverride)
            Merge(root, ReadMapping(populatedOverride));
        return new(root, Serialize(root));
    }

    private static YamlMappingNode ReadMapping(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("MEŽS configuration file was not found.", path);
        var yaml = new YamlStream();
        using (var reader = File.OpenText(path))
            yaml.Load(reader);
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidOperationException($"Configuration '{path}' must contain one YAML mapping document.");
        return root;
    }

    private static bool ContainsValues(string path) =>
        File.ReadLines(path).Any(line =>
            !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'));

    private static void Merge(YamlMappingNode target, YamlMappingNode overlay)
    {
        foreach (var entry in overlay.Children)
        {
            if (target.Children.TryGetValue(entry.Key, out var current))
                target.Children[entry.Key] = Merge(current, entry.Value);
            else
                target.Add(entry.Key, entry.Value);
        }
    }

    private static YamlNode Merge(YamlNode target, YamlNode overlay)
    {
        if (target is YamlMappingNode targetMap && overlay is YamlMappingNode overlayMap)
        {
            Merge(targetMap, overlayMap);
            return targetMap;
        }
        if (target is YamlSequenceNode targetSequence && overlay is YamlSequenceNode overlaySequence &&
            TryIndexById(targetSequence, out var targetById) && TryIndexById(overlaySequence, out _))
        {
            foreach (var item in overlaySequence.Children.Cast<YamlMappingNode>())
            {
                var id = Id(item);
                if (targetById.TryGetValue(id, out var existing))
                    Merge(existing, item);
                else
                    targetSequence.Add(item);
            }
            return targetSequence;
        }
        return overlay;
    }

    private static bool TryIndexById(
        YamlSequenceNode sequence, out Dictionary<string, YamlMappingNode> items)
    {
        items = new(StringComparer.OrdinalIgnoreCase);
        foreach (var item in sequence.Children)
        {
            if (item is not YamlMappingNode map || Id(map) is not { Length: > 0 } id ||
                !items.TryAdd(id, map))
                return false;
        }
        return items.Count > 0;
    }

    private static string Id(YamlMappingNode map) =>
        map.Children.TryGetValue(new YamlScalarNode("id"), out var node) && node is YamlScalarNode id
            ? id.Value ?? string.Empty
            : string.Empty;

    private static string Serialize(YamlNode node)
    {
        var yaml = new YamlStream(new YamlDocument(node));
        var buffer = new StringBuilder();
        using var writer = new StringWriter(buffer, CultureInfo.InvariantCulture);
        yaml.Save(writer, assignAnchors: false);
        return buffer.ToString();
    }
}

public sealed record LoadedYaml(YamlMappingNode Root, string Text);
