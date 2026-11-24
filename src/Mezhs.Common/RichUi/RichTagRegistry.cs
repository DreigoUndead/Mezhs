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
    public bool LiteralChildren { get; init; }
    public bool PreserveChildren { get; init; }
    public bool PhrasingAllowed { get; init; }
    public bool Inline { get; init; }
    public bool Interactive { get; init; }
    public Func<RichTagDefinition, IReadOnlyDictionary<string, string>, bool>? Validate { get; init; }
    public Action<RichDocumentParser, string>? ProcessLiteral { get; init; }
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

    private static readonly string[] SvgCommon =
    ["fill", "stroke", "strokeWidth", "strokeLinecap", "strokeLinejoin",
     "strokeDasharray", "opacity", "fillOpacity", "strokeOpacity", "transform",
     "fontSize", "fontFamily", "fontWeight", "textAnchor", "dominantBaseline"];
    // SVG tags are ordinary nested-only variants, admitted by their parent's
    // explicit SupportedChildren list. No SVG-specific resolver or parser scope.
    private static RichTagDefinition SvgTag(string name, RichTagRole role, params string[] attributes) =>
        Tag(name, role, attributes: [..SvgCommon, ..attributes]) with
        {
            Id = "svg:" + name,
            AtRoot = false,
            LiteralChildren = true,
            PreserveChildren = true,
            PhrasingAllowed = true,
            Validate = RichSvgValidation.ValidForTag,
            ProcessContent = RichTagHandlers.SvgTag,
            ProcessLiteral = RichTagHandlers.SvgLiteral
        };

    private static readonly IReadOnlySet<string> SvgGraphics = new HashSet<string>(
        ["svg:g", "svg:rect", "svg:path", "svg:line", "svg:polyline",
         "svg:polygon", "svg:circle", "svg:ellipse", "svg:text"],
        StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> SvgTextChildren =
        new HashSet<string>(["svg:tspan"], StringComparer.Ordinal);
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
                with { AtRoot = true, SupportedChildren = SvgGraphics },
            SvgTag("g", RichTagRole.Container)
                with { SupportedChildren = SvgGraphics },
            SvgTag("rect", RichTagRole.Leaf, "x", "y", "rx", "ry", "width", "height"),
            SvgTag("path", RichTagRole.Leaf, "d"),
            SvgTag("line", RichTagRole.Leaf, "x1", "y1", "x2", "y2"),
            SvgTag("polyline", RichTagRole.Leaf, "points"),
            SvgTag("polygon", RichTagRole.Leaf, "points"),
            SvgTag("circle", RichTagRole.Leaf, "cx", "cy", "r"),
            SvgTag("ellipse", RichTagRole.Leaf, "cx", "cy", "rx", "ry"),
            SvgTag("tspan", RichTagRole.Container, "x", "y")
                with { SupportedChildren = SvgTextChildren },
            // Same name as UI text; parent's child IDs select this variant.
            SvgTag("text", RichTagRole.Container, "x", "y")
                with { SupportedChildren = SvgTextChildren },
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
