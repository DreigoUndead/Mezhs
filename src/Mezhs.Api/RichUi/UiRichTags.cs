using Mezhs.Common.RichUi;

namespace Mezhs.RichUi;

// Only the Core API owns presentation tag definitions.
public static class UiRichTags
{
    private static RichTagDefinition Tag(string name, RichTagRole role, params string[] attributes) =>
        new(name, role, new HashSet<string>(attributes, StringComparer.Ordinal),
            SearchChildren: role == RichTagRole.Container, AllowAsChild: true);

    private static RichTagDefinition Presentation(string name, RichTagRole role) =>
        Tag(name, role) with { AllowedAttributes = null };

    private static readonly string[] SvgCommon =
        ["fill", "stroke", "strokeWidth", "strokeLinecap", "strokeLinejoin",
         "strokeDasharray", "opacity", "fillOpacity", "strokeOpacity", "transform",
         "fontSize", "fontFamily", "fontWeight", "textAnchor", "dominantBaseline"];

    private static RichTagDefinition SvgTag(string name, RichTagRole role, params string[] attributes) =>
        Tag(name, role, [..SvgCommon, ..attributes]);

    private static readonly RichTagDefinition SvgText =
        SvgTag("text", RichTagRole.Container, "x", "y");

    public static readonly RichTagRegistry Tags = new(
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
            SvgTag("svg", RichTagRole.Container, "viewBox", "width", "height",
                "preserveAspectRatio", "aria-label", "role"),
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
            Tag("Cite", RichTagRole.Leaf, "ref", "refs"),
            Tag("CodeBlock", RichTagRole.RawEditor, "language", "editable"),
            Tag("WritingBlock", RichTagRole.RawEditor, "variant", "recipient",
                "cc", "bcc", "subject", "id"),
            Tag("escape", RichTagRole.OpaqueLiteral, "char"),
        },
        SvgText);
}
