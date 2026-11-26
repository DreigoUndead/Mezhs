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
// SupportedChildren is the complete child policy:
// null = ordinary registered UI children; empty = opaque/no nested tags;
// explicit IDs = only those children (including contextual name variants).
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

    // Only variants explicitly listed by their parent can be nested-only.
    // Key distinguishes equal surface names such as UI text and SVG text.
    public string? Id { get; init; }
    public string Key => Id ?? Name;
    public bool AtRoot { get; init; } = true;
    public bool Inline { get; init; }
    public bool Interactive { get; init; }
}

// A parser instance uses one immutable catalog. An Agent host can extend the
// UI definitions without changing or mutating the reusable Core catalog.
public sealed class RichTagCatalog
{
    private readonly IReadOnlyDictionary<string, RichTagDefinition[]> _tags;

    public RichTagCatalog(IEnumerable<RichTagDefinition> tags)
    {
        var entries = tags.ToArray();
        _tags = entries.GroupBy(tag => tag.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group =>
            {
                var variants = group.ToArray();
                if (variants.Select(tag => tag.Key).Distinct(StringComparer.Ordinal).Count()
                    != variants.Length)
                    throw new ArgumentException($"Duplicate tag registration: {group.Key}.");
                return variants;
            }, StringComparer.Ordinal);

        // Misnamed explicit children should fail at registration, not silently
        // make a control unusable or weaken a parent's safety boundary.
        var knownIds = entries.Select(tag => tag.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var tag in entries)
            if (tag.SupportedChildren is { } children &&
                children.Any(id => !knownIds.Contains(id)))
                throw new ArgumentException($"Unknown supported child in {tag.Key}.");
    }

    public RichTagCatalog With(IEnumerable<RichTagDefinition> additional) =>
        new(_tags.Values.SelectMany(variants => variants).Concat(additional));

    // Structural matching must recognize registered opaque/leaf forms even
    // where the tag is not eligible for display. Eligibility is checked later.
    public bool TryGetStructural(string name, out RichTagDefinition definition)
    {
        if (_tags.TryGetValue(name, out var variants))
        {
            definition = variants[0];
            return true;
        }
        definition = null!;
        return false;
    }

    // A nested-only variant can share a source name with a root tag but have
    // different structural semantics (for example container versus raw editor).
    // Resolve the parent's declared variant before falling back to a structural
    // boundary for disallowed or unknown children.
    public bool TryGetStructural(string name, RichTagDefinition? parent,
        out RichTagDefinition definition) =>
        TryGet(name, parent, out definition) || TryGetStructural(name, out definition);

    public bool TryGet(string name, out RichTagDefinition definition) =>
        TryGet(name, null, out definition);

    public bool TryGet(string name, RichTagDefinition? parent, out RichTagDefinition definition)
    {
        definition = null!;
        if (!_tags.TryGetValue(name, out var variants)) return false;

        RichTagDefinition? selected;
        if (parent is null)
            selected = variants.FirstOrDefault(tag => tag.AtRoot);
        else if (parent.SupportedChildren is { Count: 0 })
            selected = null;
        else if (parent.SupportedChildren is { } supported)
            selected = variants.FirstOrDefault(tag =>
                tag.AllowAsChild && supported.Contains(tag.Key));
        else
            selected = variants.FirstOrDefault(tag => tag.AtRoot && tag.AllowAsChild);

        if (selected is null) return false;
        definition = selected;
        return true;
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
