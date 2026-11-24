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
public sealed record RichTagDefinition(
    string Name,
    RichTagRole Role,
    IReadOnlySet<string>? AllowedAttributes,
    bool SearchChildren,
    bool AllowAsChild,
    Action<RichDocumentParser, RichTagContent>? ProcessContent = null)
{
    // Declared by the tag family, not inferred by the generic renderer.
    public string? Context { get; init; }
    public bool StrictChildren { get; init; }
    public bool LiteralChildren { get; init; }
    public bool PreserveChildren { get; init; }
    public bool PhrasingAllowed { get; init; }
    // A tag's content model is metadata: a new UI tag never requires edits
    // to the structural parser or Markdown content-model normalization.
    public bool Inline { get; init; }
    public bool Interactive { get; init; }
    public bool AvailableOutsideContext { get; init; }
    public Func<RichTagDefinition?, RichTagDefinition, IReadOnlyDictionary<string, string>, bool>? Validate { get; init; }
    public Action<RichDocumentParser, string>? ProcessLiteral { get; init; }
}

// A parser instance uses one immutable catalog. An Agent host can extend the
// UI definitions without changing or mutating the reusable Core catalog.
public sealed class RichTagCatalog
{
    private readonly IReadOnlyDictionary<string, RichTagDefinition[]> _tags;

    public RichTagCatalog(IEnumerable<RichTagDefinition> tags)
    {
        _tags = tags.GroupBy(tag => tag.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group =>
            {
                var variants = group.ToArray();
                if (variants.Select(tag => tag.Context).Distinct(StringComparer.Ordinal).Count()
                    != variants.Length)
                    throw new ArgumentException($"Duplicate tag registration: {group.Key}.");
                return variants;
            }, StringComparer.Ordinal);
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

    public bool TryGet(string name, out RichTagDefinition definition) =>
        TryGet(name, null, out definition);

    public bool TryGet(string name, RichTagDefinition? parent, out RichTagDefinition definition)
    {
        definition = null!;
        if (!_tags.TryGetValue(name, out var variants)) return false;

        var context = parent?.Context;
        var tag = variants.FirstOrDefault(candidate => candidate.Context == context);
        if (tag is null && context is null)
            tag = variants.FirstOrDefault(candidate => candidate.AvailableOutsideContext);
        // Discover root-only executable tags inside foreign contexts as inert
        // boundaries. Never promote a child through a registered parent.
        if (tag is null && context is not null)
            tag = variants.FirstOrDefault(candidate =>
                candidate.Context is null && !candidate.AllowAsChild);
        if (tag is null) return false;
        definition = tag;
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
            SearchChildren: role == RichTagRole.Container, AllowAsChild: true)
        { Inline = inline, Interactive = interactive };

    private static readonly string[] SvgCommon =
    ["fill", "stroke", "strokeWidth", "strokeLinecap", "strokeLinejoin",
     "strokeDasharray", "opacity", "fillOpacity", "strokeOpacity", "transform",
     "fontSize", "fontFamily", "fontWeight", "textAnchor", "dominantBaseline"];
    private static RichTagDefinition SvgTag(string name, RichTagRole role, params string[] attributes) =>
        Tag(name, role, attributes: [..SvgCommon, ..attributes]) with
        {
            Context = "svg",
            StrictChildren = true,
            LiteralChildren = true,
            PreserveChildren = true,
            PhrasingAllowed = true,
            Validate = RichSvgValidation.ValidForTag,
            ProcessContent = RichTagHandlers.SvgTag,
            ProcessLiteral = RichTagHandlers.SvgLiteral
        };
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
            // SVG uses the same tag registry, with SVG-specific attributes and nesting.
            SvgTag("svg", RichTagRole.Container, "viewBox", "width", "height", "preserveAspectRatio", "aria-label", "role")
                with { AvailableOutsideContext = true },
            SvgTag("g", RichTagRole.Container),
            SvgTag("rect", RichTagRole.Leaf, "x", "y", "rx", "ry", "width", "height"),
            SvgTag("path", RichTagRole.Leaf, "d"),
            SvgTag("line", RichTagRole.Leaf, "x1", "y1", "x2", "y2"),
            SvgTag("polyline", RichTagRole.Leaf, "points"),
            SvgTag("polygon", RichTagRole.Leaf, "points"),
            SvgTag("circle", RichTagRole.Leaf, "cx", "cy", "r"),
            SvgTag("ellipse", RichTagRole.Leaf, "cx", "cy", "rx", "ry"),
            SvgTag("tspan", RichTagRole.Container, "x", "y"),
            // Same symbolic name as the UI tag, resolved by declared context.
            SvgTag("text", RichTagRole.Container, "x", "y"),
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
