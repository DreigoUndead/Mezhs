namespace Mezhs.Agent.Commands;

// Agent markup roles are protocol semantics, not React component definitions.
// The Web Lib retains ownership of presentation validation/rendering.
public enum MarkupKind
{
    Container,
    RawSource,
    Leaf,
    Command
}

public sealed record MarkupDefinition(string Name, MarkupKind Kind, CommandDefinition? Command = null);

public static class MarkupRegistry
{
    // These are the existing static layout scopes that the Agent parser must
    // recognize. A later recursive reader may enter validated containers;
    // until that reader is approved, the current parser keeps them inert.
    private static readonly MarkupDefinition[] Presentation =
    [
        new("box", MarkupKind.Container),
        new("row", MarkupKind.Container),
        new("col", MarkupKind.Container),
        new("grid", MarkupKind.Container),
        new("grid-item", MarkupKind.Container),
        new("text", MarkupKind.Container),
        new("title", MarkupKind.Container),
        new("badge", MarkupKind.Container),
        new("caption", MarkupKind.Container),
        new("Link", MarkupKind.Container),
        new("svg", MarkupKind.Container),
        new("CodeBlock", MarkupKind.RawSource),
        new("WritingBlock", MarkupKind.RawSource),
        new("divider", MarkupKind.Leaf),
        new("icon", MarkupKind.Leaf),
        new("Chart", MarkupKind.Leaf),
        new("Cite", MarkupKind.Leaf)
    ];

    // CommandRegistry is still the single authority for command names/forms.
    // The structural parser can classify all known tags through this one map.
    public static readonly IReadOnlyDictionary<string, MarkupDefinition> All =
        Presentation.Concat(Registry.All.Select(command =>
            new MarkupDefinition(command.Name, MarkupKind.Command, command)))
            .ToDictionary(tag => tag.Name, StringComparer.Ordinal);

    public static readonly IReadOnlyList<string> Containers =
        Presentation.Where(tag => tag.Kind == MarkupKind.Container)
            .Select(tag => tag.Name).ToArray();

    public static readonly IReadOnlyList<string> RawSources =
        Presentation.Where(tag => tag.Kind == MarkupKind.RawSource)
            .Select(tag => tag.Name).ToArray();

    public static bool TryGet(string name, out MarkupDefinition definition) =>
        All.TryGetValue(name, out definition!);
}
