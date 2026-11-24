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
    bool SelfClosing);

public enum RichTagRole { Container, Leaf, OpaqueLiteral, RawEditor, Command, Marker }
public sealed record RichTagDefinition(
    string Name,
    RichTagRole Role,
    IReadOnlySet<string>? AllowedAttributes,
    bool SearchChildren,
    bool AllowAsChild,
    Action<RichDocumentParser, RichTagContent>? ProcessContent = null);

// A parser instance uses one immutable catalog. An Agent host can extend the
// UI definitions without changing or mutating the reusable Core catalog.
public sealed class RichTagCatalog
{
    private readonly IReadOnlyDictionary<string, RichTagDefinition> _tags;
    public RichTagCatalog(IEnumerable<RichTagDefinition> tags) =>
        _tags = tags.ToDictionary(tag => tag.Name, StringComparer.Ordinal);

    public RichTagCatalog With(IEnumerable<RichTagDefinition> additional) =>
        new(_tags.Values.Concat(additional));

    public bool TryGet(string name, out RichTagDefinition definition) =>
        TryGet(name, false, out definition);

    public bool TryGet(string name, bool svgScope, out RichTagDefinition definition)
    {
        if (svgScope && name == "text")
        {
            definition = RichTagRegistry.SvgText;
            return true;
        }
        if (!_tags.TryGetValue(name, out definition!)) return false;
        return svgScope
            ? RichTagRegistry.IsSvgTag(name) || !definition.AllowAsChild
            : !RichTagRegistry.IsSvgTag(name) || (name is "svg" or "text");
    }
}

// Core-facing declarative UI catalog. Agent command tags are contributed
// separately by Mezhs.Agent.Api/Commands/Registry.cs.
public static class RichTagRegistry
{
    private static RichTagDefinition Tag(string name, RichTagRole role, params string[] attributes) =>
        new(name, role, new HashSet<string>(attributes, StringComparer.Ordinal),
            SearchChildren: role == RichTagRole.Container,
            AllowAsChild: true);

    // Presentation components receive bounded inert attributes and interpret them locally.
    // Restricted namespaces (SVG, raw editors, Agent commands) keep explicit contracts.
    private static RichTagDefinition Presentation(string name, RichTagRole role) =>
        Tag(name, role) with { AllowedAttributes = null };

    // An individual tag may override either traversal policy without
    // changing its role, syntax, attribute allowlist or siblings.
    private static RichTagDefinition WithDiscovery(RichTagDefinition tag,
        bool? searchChildren = null, bool? allowAsChild = null) =>
        tag with
        {
            SearchChildren = searchChildren ?? tag.SearchChildren,
            AllowAsChild = allowAsChild ?? tag.AllowAsChild
        };

    private static readonly string[] SvgCommon =
    ["fill", "stroke", "strokeWidth", "strokeLinecap", "strokeLinejoin",
     "strokeDasharray", "opacity", "fillOpacity", "strokeOpacity", "transform",
     "fontSize", "fontFamily", "fontWeight", "textAnchor", "dominantBaseline"];
    private static RichTagDefinition SvgTag(string name, RichTagRole role, params string[] attributes) =>
        Tag(name, role, [..SvgCommon, ..attributes]);
    internal static readonly RichTagDefinition SvgText = SvgTag("text", RichTagRole.Container, "x", "y");
    private static readonly HashSet<string> SvgNames = new(
        ["svg", "g", "rect", "path", "line", "polyline", "polygon", "circle", "ellipse", "text", "tspan"],
        StringComparer.Ordinal);

    public static readonly RichTagCatalog Ui = new(
        new[]
        {
            Presentation("box", RichTagRole.Container),
            Presentation("row", RichTagRole.Container),
            Presentation("col", RichTagRole.Container),
            Presentation("grid", RichTagRole.Container),
            Presentation("grid-item", RichTagRole.Container),
            Presentation("text", RichTagRole.Container),
            Presentation("title", RichTagRole.Container),
            Presentation("caption", RichTagRole.Container),
            Presentation("badge", RichTagRole.Container),
            Presentation("Link", RichTagRole.Container),
            // SVG uses the same tag registry, with SVG-specific attributes and nesting.
            SvgTag("svg", RichTagRole.Container, "viewBox", "width", "height", "preserveAspectRatio", "aria-label", "role"),
            SvgTag("g", RichTagRole.Container),
            SvgTag("rect", RichTagRole.Leaf, "x", "y", "rx", "ry", "width", "height"),
            SvgTag("path", RichTagRole.Leaf, "d"),
            SvgTag("line", RichTagRole.Leaf, "x1", "y1", "x2", "y2"),
            SvgTag("polyline", RichTagRole.Leaf, "points"),
            SvgTag("polygon", RichTagRole.Leaf, "points"),
            SvgTag("circle", RichTagRole.Leaf, "cx", "cy", "r"),
            SvgTag("ellipse", RichTagRole.Leaf, "cx", "cy", "rx", "ry"),
            SvgTag("tspan", RichTagRole.Container, "x", "y"),
            Presentation("divider", RichTagRole.Leaf),
            Presentation("icon", RichTagRole.Leaf),
            Presentation("Chart", RichTagRole.Leaf),
            // Citation provenance is a security-sensitive namespace, not general presentation.
            Tag("Cite", RichTagRole.Leaf, "ref", "refs"),
            Tag("CodeBlock", RichTagRole.RawEditor, "language", "editable"),
            Tag("WritingBlock", RichTagRole.RawEditor, "variant", "recipient", "cc", "bcc", "subject", "id"),
            WithDiscovery(Tag("escape", RichTagRole.OpaqueLiteral, "char"), searchChildren: false)
                with { ProcessContent = RichTagHandlers.Escape }
        });

    public static bool IsSvgTag(string name) => SvgNames.Contains(name);
}
