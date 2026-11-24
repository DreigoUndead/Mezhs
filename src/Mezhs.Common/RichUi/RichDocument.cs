namespace Mezhs.Common.RichUi;

// Source positions are zero-based UTF-16 offsets into the original message.
// End is exclusive; nothing in this contract is a frontend event handler.
public sealed record RichSourceSpan(int Start, int End);

public sealed record RichNode(
    string Kind,
    string Id,
    RichSourceSpan SourceSpan,
    string? Text = null,
    string? Name = null,
    IReadOnlyDictionary<string, string>? Attributes = null,
    IReadOnlyList<RichNode>? Children = null);

public sealed record RichDiagnostic(string Code, RichSourceSpan SourceSpan);

// Only validated display nodes may be serialized to a Web frontend.
public sealed record RichDocument(
    int Version,
    bool Valid,
    IReadOnlyList<RichNode> Nodes,
    IReadOnlyList<RichDiagnostic> Diagnostics)
{
    public const int CurrentVersion = 1;
}

// Host-side only. Never serialize command candidates as executable frontend controls.
public sealed record RichCommandCandidate(string Name, string? Body, RichSourceSpan SourceSpan);
public sealed record RichParseResult(RichDocument Document, IReadOnlyList<RichCommandCandidate> Commands);

public enum RichTagRole { Container, Leaf, OpaqueLiteral, RawEditor, Command, Marker }
public sealed record RichTagDefinition(
    string Name,
    RichTagRole Role,
    IReadOnlySet<string>? AllowedAttributes,
    // SearchChildren: recursively discover registered nested tags inside this
    // tag's body. False means the complete body is opaque and never searched.
    bool SearchChildren,
    // AllowAsChild: whether this tag may be discovered while traversing any
    // other searchable tag. Independent of SearchChildren and Agent policy.
    bool AllowAsChild,
    // Optional host-only handler for opaque body content. A handler may discover
    // command candidates, but never performs an Agent action.
    Action<RichDocumentParser, int, int, int>? ProcessContent = null);

// The shared parser owns UI syntax. Agent command authority stays with Agent Registry.
public static class RichTagRegistry
{
    private static RichTagDefinition Tag(string name, RichTagRole role, params string[] attributes) =>
        new(name, role, new HashSet<string>(attributes, StringComparer.Ordinal),
            SearchChildren: role == RichTagRole.Container,
            AllowAsChild: name != "DONE");

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
    private static readonly RichTagDefinition SvgText = SvgTag("text", RichTagRole.Container, "x", "y");
    private static readonly HashSet<string> SvgNames = new(
        ["svg", "g", "rect", "path", "line", "polyline", "polygon", "circle", "ellipse", "text", "tspan"],
        StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, RichTagDefinition> Tags =
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
                with { ProcessContent = static (parser, start, end, depth) =>
                    parser.ProcessEscapedContent(start, end, depth) },
            // Agent commands are root-only and opaque, using the same
            // traversal policy as all other registered tags.
            WithDiscovery(Tag("SH", RichTagRole.Command), searchChildren: false, allowAsChild: false),
            WithDiscovery(Tag("ATTACH", RichTagRole.Command), searchChildren: false, allowAsChild: false),
            WithDiscovery(Tag("DONE", RichTagRole.Marker), searchChildren: false, allowAsChild: false)
        }.ToDictionary(tag => tag.Name, StringComparer.Ordinal);

    public static bool IsSvgTag(string name) => SvgNames.Contains(name);

    public static bool TryGet(string name, bool svgScope, out RichTagDefinition definition)
    {
        if (svgScope && name == "text") { definition = SvgText; return true; }
        if (!Tags.TryGetValue(name, out definition!)) return false;
        // Resolve root-only tags inside a child scope only so their eligibility
        // policy can keep them inert; other foreign SVG content remains invalid.
        return svgScope
            ? SvgNames.Contains(name) || !definition.AllowAsChild
            : !SvgNames.Contains(name) || (name is "svg" or "text");
    }

    public static bool TryGet(string name, out RichTagDefinition definition) =>
        TryGet(name, false, out definition);
}
