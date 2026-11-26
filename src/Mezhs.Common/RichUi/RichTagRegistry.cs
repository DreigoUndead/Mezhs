namespace Mezhs.Common.RichUi;

// Parsing-only validated tag data. A handler can collect host candidates,
// but this object (including its parent) is never part of RichDocument JSON.
public sealed record RichTagContent(
    RichTagDefinition Definition,
    IReadOnlyDictionary<string, string> Parameters,
    string Content,
    RichSourceSpan SourceSpan,
    RichSourceSpan BodySpan,
    RichTagContent? Parent,
    bool IsDocumentRoot,
    bool SelfClosing,
    int Depth);

public enum RichTagRole { Container, Leaf, OpaqueLiteral, RawEditor, Command, Marker }
// SupportedChildren controls a parent's direct registered UI children:
// null = normal children, empty = no children, explicit names = only those names.
// RequiredAncestors independently limits where a uniquely named tag may occur:
// null = unrestricted; nonempty names = at least one structural ancestor matches.
// Eligibility never changes a tag's structural role or its source boundaries.
public sealed record RichTagDefinition(
    string Name,
    RichTagRole Role,
    IReadOnlySet<string>? AllowedAttributes,
    IReadOnlySet<string>? SupportedChildren,
    bool AllowAsChild,
    Action<RichDocumentParser, RichTagContent>? ProcessContent = null)
{
    public static readonly IReadOnlySet<string> NoChildren =
        new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlySet<string>? RequiredAncestors { get; init; }
    public bool Inline { get; init; }
    public bool Interactive { get; init; }
}

// A parser instance uses one immutable catalog. An Agent host can extend the
// UI definitions without changing or mutating the reusable Core catalog.
// One source name has exactly one structural definition in each composed catalog.
public sealed class RichTagCatalog
{
    private readonly IReadOnlyDictionary<string, RichTagDefinition> _tags;

    public RichTagCatalog(IEnumerable<RichTagDefinition> tags)
    {
        var entries = tags.ToArray();
        var byName = new Dictionary<string, RichTagDefinition>(StringComparer.Ordinal);
        foreach (var tag in entries)
            if (!byName.TryAdd(tag.Name, tag))
                throw new ArgumentException($"Duplicate tag registration: {tag.Name}.");
        _tags = byName;

        // Parent/ancestor contracts reference unique, registered source names.
        foreach (var tag in entries)
        {
            if (tag.SupportedChildren is { } children &&
                children.Any(name => !byName.ContainsKey(name)))
                throw new ArgumentException($"Unknown supported child in {tag.Name}.");
            if (tag.RequiredAncestors is { } required &&
                (required.Count == 0 || required.Any(name => !byName.ContainsKey(name))))
                throw new ArgumentException($"Invalid required ancestor in {tag.Name}.");
        }
    }

    public RichTagCatalog With(IEnumerable<RichTagDefinition> additional) =>
        new(_tags.Values.Concat(additional));

    // Structural lookup deliberately ignores contextual admission. Even a
    // forbidden wrapper must keep its exact boundary and shield host commands.
    public bool TryGetStructural(string name, out RichTagDefinition definition) =>
        _tags.TryGetValue(name, out definition!);

    public bool TryGet(string name, out RichTagDefinition definition) =>
        TryGetStructural(name, out definition);

    // Parent eligibility does not override or select a tag definition.
    // RequiredAncestors is checked against the complete structural parent
    // chain by RichDocumentParser only after structural matching.
    public bool TryGet(string name, RichTagDefinition? parent,
        out RichTagDefinition definition)
    {
        if (!TryGetStructural(name, out definition)) return false;
        return parent is null ||
            definition.AllowAsChild && (parent.SupportedChildren is null ||
                parent.SupportedChildren.Contains(name));
    }
}

// Core-facing declarative UI catalog. Agent command tags are contributed
// separately by Mezhs.Agent.Api/Commands/Registry.cs.
public static class RichTagRegistry
{
    // One general tag factory. An explicit attribute list means restricted
    // input; null keeps the existing bounded presentation attribute policy.
    private static RichTagDefinition Tag(string name,
        RichTagRole role = RichTagRole.Container, bool inline = false,
        bool interactive = false, string[]? attributes = null) =>
        new(name, role,
            attributes is null ? null : new HashSet<string>(attributes, StringComparer.Ordinal),
            SupportedChildren: role == RichTagRole.Container ? null : RichTagDefinition.NoChildren,
            AllowAsChild: true)
        { Inline = inline, Interactive = interactive };

    public static readonly RichTagCatalog Ui = new(
        new[]
        {
            Tag("box"),
            Tag("row"),
            Tag("col"),
            Tag("grid"),
            Tag("grid-item"),
            Tag("text", inline: true),
            Tag("title", inline: true),
            Tag("caption", inline: true),
            Tag("badge", inline: true),
            Tag("Link", inline: true, interactive: true),
            // SVG is one inert raw-editor boundary. Its trusted TSX control
            // parses and validates the finite SVG language without C# sub-tags.
            Tag("svg", RichTagRole.RawEditor,
                attributes: ["viewBox", "width", "height", "preserveAspectRatio", "role", "aria-label"]),
            Tag("divider", RichTagRole.Leaf),
            Tag("icon", RichTagRole.Leaf, inline: true),
            Tag("Chart", RichTagRole.Leaf),
            // Citation provenance is a security-sensitive namespace, not general presentation.
            Tag("Cite", RichTagRole.Leaf, inline: true, attributes: ["ref", "refs"]),
            Tag("CodeBlock", RichTagRole.RawEditor, attributes: ["language", "editable"]),
            Tag("WritingBlock", RichTagRole.RawEditor, attributes: ["variant", "recipient", "cc", "bcc", "subject", "id"]),
            Tag("escape", RichTagRole.OpaqueLiteral, attributes: ["char"])
                with { ProcessContent = RichTagHandlers.Escape }
        });

}
