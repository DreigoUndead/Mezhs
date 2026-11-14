using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Mezhs.Common.RichUi;

// Convert actual Markdig syntax nodes to a bounded, inert display contract.
// React must not reparse Markdown source to discover actionable elements.
internal static class RichMarkdownNodes
{
    // Use the same bounded Markdown extensions as RichDocumentParser. Generic
    // attributes are deliberately unsupported: braces remain visible text.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseEmphasisExtras().UseTaskLists().Build();

    public static IReadOnlyList<RichNode> Convert(string source, int offset,
        bool inlineOnly = false, IReadOnlyList<RichNode>? inlineElements = null)
    {
        // Keep the exact UTF-16 positions while Markdig reads one uninterrupted
        // Markdown scope. Each recursively validated display element occupies
        // one private-use start marker followed by private-use padding. Markdig
        // treats the marker as plain inline text; it cannot become a new tag.
        var replacements = new Dictionary<int, RichNode>();
        if (inlineElements is { Count: > 0 })
        {
            var chars = source.ToCharArray();
            foreach (var element in inlineElements)
            {
                var start = element.SourceSpan.Start - offset;
                var end = element.SourceSpan.End - offset;
                if (start < 0 || end > chars.Length || start >= end ||
                    replacements.ContainsKey(start))
                    throw new FormatException("Invalid inline element source span.");
                for (var i = start; i < end; i++)
                    chars[i] = i == start ? '\uE000' : '\uE001';
                replacements.Add(start, element);
            }
            source = new string(chars);
        }

        var ast = Markdown.Parse(source, Pipeline);
        var result = new List<RichNode>();
        var count = 0;
        void AppendMapped(List<RichNode> nodes, MarkdownObject input, int depth) =>
            AddMapped(nodes, Map(input, source, offset, depth, ref count, replacements));
        foreach (var block in ast)
        {
            if (inlineOnly && block is ParagraphBlock { Inline: { } inlines })
            {
                foreach (var child in inlines)
                    AppendMapped(result, child, 0);
            }
            else
                AppendMapped(result, block, 0);
        }
        return result;
    }

    private static void AddMapped(List<RichNode> nodes, RichNode node)
    {
        if (node.Kind == "inlineFragment" && node.Children is not null)
            nodes.AddRange(node.Children);
        else
            nodes.Add(node);
    }

    private static RichNode Map(MarkdownObject input, string source, int offset,
        int depth, ref int count, IReadOnlyDictionary<int, RichNode> replacements)
    {
        if (++count > 4_096 || depth > 24) throw new FormatException("Markdown node budget exceeded.");
        var start = Math.Clamp(input.Span.Start, 0, source.Length);
        var end = Math.Clamp(input.Span.End + 1, start, source.Length);
        var span = new RichSourceSpan(start + offset, end + offset);
        if (input is LiteralInline literalWithRich && replacements.Count > 0)
        {
            var embedded = replacements
                .Where(entry => entry.Key >= start && entry.Key < end)
                .OrderBy(entry => entry.Key).ToArray();
            if (embedded.Length > 0)
            {
                var literalText = literalWithRich.Content.ToString();
                var parts = new List<RichNode>();
                var at = 0;
                var original = start;
                foreach (var (tokenStart, element) in embedded)
                {
                    var length = element.SourceSpan.End - element.SourceSpan.Start;
                    var marker = "\uE000" + new string('\uE001', length - 1);
                    var found = literalText.IndexOf(marker, at, StringComparison.Ordinal);
                    if (found < 0)
                        throw new FormatException("Markdown split an inline rich placeholder.");
                    if (found > at)
                        parts.Add(new RichNode("text", $"{offset + original}:text",
                            new RichSourceSpan(offset + original, offset + tokenStart),
                            Text: literalText[at..found]));
                    parts.Add(element);
                    at = found + marker.Length;
                    original = tokenStart + length;
                }
                if (at < literalText.Length)
                    parts.Add(new RichNode("text", $"{offset + original}:text",
                        new RichSourceSpan(offset + original, offset + end), Text: literalText[at..]));
                return new RichNode("inlineFragment", $"{span.Start}:inlineFragment",
                    span, Children: parts);
            }
        }
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
            "LinkInline" => input is LinkInline { IsImage: true } ? "image" : "link",
            "AutolinkInline" => "link",
            "TaskList" => "taskCheckbox",
            "LineBreakInline" => "lineBreak",
            "Table" => "table",
            "TableRow" => "tableRow",
            "TableCell" => "tableCell",
            _ => "literal"
        };
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (input is HeadingBlock heading) attributes.Add("level", heading.Level.ToString());
        if (input is ListBlock list) attributes.Add("ordered", list.IsOrdered ? "true" : "false");
        if (input is TableRow row && row.IsHeader) attributes.Add("header", "true");
        if (input is TableCell { Parent: TableRow { IsHeader: true } }) attributes.Add("header", "true");
        if (input is TaskList task) attributes.Add("checked", task.Checked ? "true" : "false");
        if (input is LinkInline link && !string.IsNullOrEmpty(link.Url) &&
            Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) &&
            link.Url.Length <= 2_048 &&
            (uri.Scheme is "https" or "http" ||
             !link.IsImage && uri.Scheme == "mailto"))
            attributes.Add("url", link.Url);
        if (kind is "link" or "image" && !attributes.ContainsKey("url")) kind = "literal";

        string? text = null;
        if (input is LiteralInline literal) text = literal.Content.ToString();
        else if (input is CodeInline code) text = code.Content;
        else if (kind == "code" && input is LeafBlock codeBlock)
            text = codeBlock.Lines.ToString();
        else if (kind == "literal") text = source[start..end];

        var children = new List<RichNode>();
        if (kind != "literal" && kind != "code")
        {
            if (input is ContainerBlock blocks)
                foreach (var child in blocks)
                    AddMapped(children, Map(child, source, offset, depth + 1, ref count, replacements));
            if (input is LeafBlock leaf && leaf.Inline is not null)
                foreach (var child in leaf.Inline)
                    AddMapped(children, Map(child, source, offset, depth + 1, ref count, replacements));
            if (input is ContainerInline inlines)
                foreach (var child in inlines)
                    AddMapped(children, Map(child, source, offset, depth + 1, ref count, replacements));
        }
        return new RichNode(kind, $"{span.Start}:{kind}", span, text,
            Attributes: attributes.Count == 0 ? null : attributes,
            Children: children.Count == 0 ? null : children);
    }
}
