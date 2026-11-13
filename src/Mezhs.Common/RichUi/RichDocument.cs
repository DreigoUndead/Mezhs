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
    IReadOnlySet<string> AllowedAttributes);

// The shared parser owns UI syntax. Agent command authority stays with Agent Registry.
public static class RichTagRegistry
{
    private static RichTagDefinition Tag(string name, RichTagRole role, params string[] attributes) =>
        new(name, role, new HashSet<string>(attributes, StringComparer.Ordinal));

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
            Tag("box", RichTagRole.Container, "gap", "align", "justify", "padding", "border", "radius", "background", "width", "flex", "key"),
            Tag("row", RichTagRole.Container, "gap", "align", "justify", "key"),
            Tag("col", RichTagRole.Container, "gap", "align", "justify", "key"),
            Tag("grid", RichTagRole.Container, "columns", "gap", "key"),
            Tag("grid-item", RichTagRole.Container, "span", "key"),
            Tag("text", RichTagRole.Container, "size", "color", "weight", "strong", "key"),
            Tag("title", RichTagRole.Container, "size", "color", "weight", "key"),
            Tag("caption", RichTagRole.Container, "color", "key"),
            Tag("badge", RichTagRole.Container, "color", "variant", "key"),
            Tag("Link", RichTagRole.Container, "url", "title", "key"),
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
            Tag("divider", RichTagRole.Leaf, "color", "key"),
            Tag("icon", RichTagRole.Leaf, "name", "size", "color", "inline", "key"),
            Tag("Chart", RichTagRole.Leaf, "content"),
            Tag("Cite", RichTagRole.Leaf, "ref", "refs"),
            Tag("CodeBlock", RichTagRole.RawEditor, "language", "editable"),
            Tag("WritingBlock", RichTagRole.RawEditor, "variant", "recipient", "cc", "bcc", "subject", "id"),
            Tag("escape", RichTagRole.OpaqueLiteral, "char"),
            Tag("SH", RichTagRole.Command),
            Tag("ATTACH", RichTagRole.Command),
            Tag("DONE", RichTagRole.Marker)
        }.ToDictionary(tag => tag.Name, StringComparer.Ordinal);

    public static bool IsSvgTag(string name) => SvgNames.Contains(name);

    public static bool TryGet(string name, bool svgScope, out RichTagDefinition definition)
    {
        if (svgScope)
        {
            if (!SvgNames.Contains(name)) { definition = null!; return false; }
            if (name == "text") { definition = SvgText; return true; }
        }
        if (name != "svg" && name != "text" && SvgNames.Contains(name) && !svgScope)
        {
            definition = null!;
            return false;
        }
        return Tags.TryGetValue(name, out definition!);
    }

    public static bool TryGet(string name, out RichTagDefinition definition) =>
        TryGet(name, false, out definition);
}
