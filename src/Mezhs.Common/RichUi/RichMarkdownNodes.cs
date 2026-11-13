using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Mezhs.Common.RichUi;

// Convert actual Markdig syntax nodes to a bounded, inert display contract.
// React must not reparse Markdown source to discover actionable elements.
internal static class RichMarkdownNodes
{
    // Use the same bounded Markdown extensions as RichDocumentParser. Generic
    // attributes are deliberately unsupported: braces remain visible text.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseEmphasisExtras().Build();

    public static IReadOnlyList<RichNode> Convert(string source, int offset)
    {
        var ast = Markdown.Parse(source, Pipeline);
        var result = new List<RichNode>();
        var count = 0;
        foreach (var block in ast)
            result.Add(Map(block, source, offset, 0, ref count));
        return result;
    }

    private static RichNode Map(MarkdownObject input, string source, int offset,
        int depth, ref int count)
    {
        if (++count > 4_096 || depth > 24) throw new FormatException("Markdown node budget exceeded.");
        var start = Math.Clamp(input.Span.Start, 0, source.Length);
        var end = Math.Clamp(input.Span.End + 1, start, source.Length);
        var span = new RichSourceSpan(start + offset, end + offset);
        var name = input.GetType().Name;
        var kind = name switch
        {
            "ParagraphBlock" => "paragraph",
            "HeadingBlock" => "heading",
            "ListBlock" => "list",
            "ListItemBlock" => "listItem",
            "QuoteBlock" => "blockquote",
            "FencedCodeBlock" or "CodeBlock" => "code",
            "ThematicBreakBlock" => "divider",
            "LiteralInline" => "text",
            "CodeInline" => "inlineCode",
            "EmphasisInline" => input is EmphasisInline e && e.DelimiterChar == '~'
                ? "strikethrough" : input is EmphasisInline strong && strong.DelimiterCount >= 2
                    ? "strong" : "emphasis",
            "LinkInline" or "AutolinkInline" => "link",
            "LineBreakInline" => "lineBreak",
            "Table" => "table",
            "TableRow" => "tableRow",
            "TableCell" => "tableCell",
            _ => "literal"
        };
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (input is HeadingBlock heading) attributes.Add("level", heading.Level.ToString());
        if (input is ListBlock list) attributes.Add("ordered", list.IsOrdered ? "true" : "false");
        if (input is LinkInline link && !string.IsNullOrEmpty(link.Url) &&
            Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) &&
            uri.Scheme is "https" or "http" or "mailto")
            attributes.Add("url", link.Url);
        if (kind == "link" && !attributes.ContainsKey("url")) kind = "literal";

        string? text = null;
        if (input is LiteralInline literal) text = literal.Content.ToString();
        else if (input is CodeInline code) text = code.Content;
        else if (kind is "code" or "literal") text = source[start..end];

        var children = new List<RichNode>();
        if (kind != "literal" && kind != "code")
        {
            if (input is ContainerBlock blocks)
                foreach (var child in blocks) children.Add(Map(child, source, offset, depth + 1, ref count));
            if (input is LeafBlock leaf && leaf.Inline is not null)
                foreach (var child in leaf.Inline) children.Add(Map(child, source, offset, depth + 1, ref count));
            if (input is ContainerInline inlines)
                foreach (var child in inlines) children.Add(Map(child, source, offset, depth + 1, ref count));
        }
        return new RichNode(kind, $"{span.Start}:{kind}", span, text,
            Attributes: attributes.Count == 0 ? null : attributes,
            Children: children.Count == 0 ? null : children);
    }
}
