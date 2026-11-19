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
        bool inlineOnly = false, IReadOnlyList<RichNode>? inlineElements = null,
        IReadOnlyList<RichSourceSpan>? hiddenSpans = null)
    {
        // Keep the exact UTF-16 positions while Markdig reads one uninterrupted
        // Markdown scope. Each recursively validated display element occupies
        // a private-use start marker followed by private-use padding. Markdig
        // treats the marker as plain inline text; it cannot become a new tag.
        var replacements = new Dictionary<int, (int End, RichNode? Element)>();
        // Never use model-authored characters as synthetic inline markers.
        // Pick a pair absent from the *entire unmodified source* before any
        // substitution. This makes IndexOf unambiguous even when an adversarial
        // message contains the old U+E000/U+E001 sentinel sequence.
        var markerStart = '\uE000';
        var markerPad = '\uE001';
        if (inlineElements is { Count: > 0 } || hiddenSpans is { Count: > 0 })
        {
            var occupied = new bool[0x1900]; // BMP private-use area E000..F8FF
            foreach (var c in source)
                if (c is >= '\uE000' and <= '\uF8FF')
                    occupied[c - '\uE000'] = true;
            var unused = Enumerable.Range(0, occupied.Length)
                .Where(index => !occupied[index]).Take(2).ToArray();
            if (unused.Length != 2)
                throw new FormatException("No unambiguous Markdown inline placeholders available.");
            markerStart = (char)('\uE000' + unused[0]);
            markerPad = (char)('\uE000' + unused[1]);
            var chars = source.ToCharArray();
            foreach (var element in inlineElements ?? [])
            {
                var start = element.SourceSpan.Start - offset;
                var end = element.SourceSpan.End - offset;
                if (start < 0 || end > chars.Length || start >= end ||
                    replacements.ContainsKey(start))
                    throw new FormatException("Invalid inline element source span.");
                for (var i = start; i < end; i++)
                    chars[i] = i == start ? markerStart : markerPad;
                replacements.Add(start, (end, element));
            }
            foreach (var hidden in hiddenSpans ?? [])
            {
                var start = hidden.Start - offset;
                var end = hidden.End - offset;
                if (start < 0 || end > chars.Length || start >= end ||
                    replacements.Any(item => item.Key < end && item.Value.End > start))
                    throw new FormatException("Invalid or overlapping hidden command span.");
                for (var i = start; i < end; i++)
                    chars[i] = i == start ? markerStart : markerPad;
                replacements.Add(start, (end, null));
            }
            source = new string(chars);
        }

        // Pre-sort just once. Binary search per literal is O(log N), rather
        // than scanning every replacement for every Markdown inline fragment.
        var replacementStarts = replacements.Keys.OrderBy(start => start).ToArray();
        var ast = Markdown.Parse(source, Pipeline);
        var result = new List<RichNode>();
        var count = 0;
        void AppendMapped(List<RichNode> nodes, MarkdownObject input, int depth) =>
            AddMapped(nodes, Map(input, source, offset, depth, ref count, replacements, replacementStarts,
                markerStart, markerPad));
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
        // A paragraph consisting solely of an invisible command placeholder
        // must not render as an extra empty paragraph between visible blocks.
        if (hiddenSpans is { Count: > 0 })
            result.RemoveAll(node => node.Kind == "paragraph" &&
                node.Children is null or { Count: 0 });
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
        int depth, ref int count, IReadOnlyDictionary<int, (int End, RichNode? Element)> replacements,
        int[] replacementStarts, char markerStart, char markerPad)
    {
        if (++count > 4_096 || depth > 24) throw new FormatException("Markdown node budget exceeded.");
        var start = Math.Clamp(input.Span.Start, 0, source.Length);
        var end = Math.Clamp(input.Span.End + 1, start, source.Length);
        var span = new RichSourceSpan(start + offset, end + offset);
        if (input is LiteralInline literalWithRich && replacements.Count > 0)
        {
            var first = Array.BinarySearch(replacementStarts, start);
            if (first < 0) first = ~first;
            if (first < replacementStarts.Length && replacementStarts[first] < end)
            {
                var literalText = literalWithRich.Content.ToString();
                var parts = new List<RichNode>();
                var at = 0;
                var original = start;
                for (var tokenIndex = first; tokenIndex < replacementStarts.Length; tokenIndex++)
                {
                    var tokenStart = replacementStarts[tokenIndex];
                    if (tokenStart >= end) break;
                    var (tokenEnd, element) = replacements[tokenStart];
                    var length = tokenEnd - tokenStart;
                    var marker = markerStart + new string(markerPad, length - 1);
                    var found = literalText.IndexOf(marker, at, StringComparison.Ordinal);
                    if (found < 0)
                        throw new FormatException("Markdown split an inline rich placeholder.");
                    if (found > at)
                        parts.Add(new RichNode("text", $"{offset + original}:text",
                            new RichSourceSpan(offset + original, offset + tokenStart),
                            Text: literalText[at..found]));
                    if (element is not null) parts.Add(element);
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
        var kind = input switch
        {
            ParagraphBlock => "paragraph",
            HeadingBlock => "heading",
            ListBlock => "list",
            ListItemBlock => "listItem",
            QuoteBlock => "blockquote",
            FencedCodeBlock or CodeBlock => "code",
            ThematicBreakBlock => "divider",
            LiteralInline => "text",
            CodeInline => "inlineCode",
            EmphasisInline { DelimiterChar: '~' } => "strikethrough",
            EmphasisInline { DelimiterCount: >= 2 } => "strong",
            EmphasisInline => "emphasis",
            LinkInline { IsImage: true } => "image",
            LinkInline or AutolinkInline => "link",
            TaskList => "taskCheckbox",
            LineBreakInline => "lineBreak",
            Table => "table",
            TableRow => "tableRow",
            TableCell => "tableCell",
            _ => "literal"
        };
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (input is HeadingBlock heading) attributes.Add("level", heading.Level.ToString());
        if (input is ListBlock list) attributes.Add("ordered", list.IsOrdered ? "true" : "false");
        if (input is TableRow row && row.IsHeader) attributes.Add("header", "true");
        if (input is TableCell { Parent: TableRow { IsHeader: true } }) attributes.Add("header", "true");
        if (input is TaskList task) attributes.Add("checked", task.Checked ? "true" : "false");
        var destination = input switch
        {
            LinkInline link => link.Url,
            AutolinkInline link when link.IsEmail => "mailto:" + link.Url,
            AutolinkInline link => link.Url,
            _ => null
        };
        if (!string.IsNullOrEmpty(destination) &&
            Uri.TryCreate(destination, UriKind.Absolute, out var uri) &&
            destination.Length <= 2_048 &&
            (uri.Scheme is "https" or "http" ||
             kind == "link" && uri.Scheme == "mailto"))
            attributes.Add("url", destination);
        if (kind is "link" or "image" && !attributes.ContainsKey("url")) kind = "literal";

        string? text = null;
        if (input is LiteralInline literal) text = literal.Content.ToString();
        else if (input is CodeInline code) text = code.Content;
        else if (kind == "code" && input is LeafBlock codeBlock)
            text = codeBlock.Lines.ToString();
        else if (kind == "literal") text = source[start..end];

        // Markdig normalizes code nodes and treats HTML blocks as inert literal
        // nodes, not LiteralInline children. Restore recognized rich elements
        // from their source spans in these representation layers as well.
        if (replacements.Count > 0 && text is not null && kind is "code" or "inlineCode" or "literal")
        {
            var first = Array.BinarySearch(replacementStarts, start);
            if (first < 0) first = ~first;
            var parts = new List<RichNode>();
            var at = 0;
            var original = start;
            var hasElement = false;
            for (var tokenIndex = first; tokenIndex < replacementStarts.Length; tokenIndex++)
            {
                var tokenStart = replacementStarts[tokenIndex];
                if (tokenStart >= end) break;
                var (tokenEnd, element) = replacements[tokenStart];
                var marker = markerStart + new string(markerPad, tokenEnd - tokenStart - 1);
                var found = text.IndexOf(marker, at, StringComparison.Ordinal);
                if (found < 0)
                {
                    // Fenced code metadata belongs to the source span but not
                    // the normalized Lines text. It can contain a recognized
                    // tag or root command without breaking the entire batch.
                    if (kind == "code")
                    {
                        if (element is not null) { parts.Add(element); hasElement = true; }
                        original = tokenEnd;
                        continue;
                    }
                    throw new FormatException("Markdown split an inline rich placeholder.");
                }
                if (found > at)
                    parts.Add(new RichNode(kind, $"{offset + original}:{kind}",
                        new RichSourceSpan(offset + original, offset + tokenStart),
                        Text: text[at..found]));
                if (element is not null)
                {
                    parts.Add(element);
                    hasElement = true;
                }
                at = found + marker.Length;
                original = tokenEnd;
            }
            if (hasElement)
            {
                if (at < text.Length)
                    parts.Add(new RichNode(kind, $"{offset + original}:{kind}",
                        new RichSourceSpan(offset + original, offset + end),
                        Text: text[at..]));
                return new RichNode("inlineFragment", $"{span.Start}:inlineFragment",
                    span, Children: parts);
            }
        }

        // In code or inert literal blocks, Markdig does not emit LiteralInline
        // children. Remove only our synthetic marker characters from display
        // text; the originals were chosen absent from the model source.
        if (replacements.Count > 0 && text is not null && kind is "code" or "inlineCode" or "literal" &&
            text.IndexOf(markerStart) >= 0)
            text = text.Replace(markerStart.ToString(), "", StringComparison.Ordinal)
                .Replace(markerPad.ToString(), "", StringComparison.Ordinal);

        var children = new List<RichNode>();
        if (kind != "literal" && kind != "code")
        {
            if (input is AutolinkInline)
            {
                var label = source[start..end];
                if (label.StartsWith('<') && label.EndsWith('>'))
                    label = label[1..^1];
                children.Add(new RichNode("text", $"{span.Start}:text", span, Text: label));
            }
            if (input is ContainerBlock blocks)
                foreach (var child in blocks)
                    AddMapped(children, Map(child, source, offset, depth + 1, ref count, replacements, replacementStarts,
                        markerStart, markerPad));
            if (input is LeafBlock leaf && leaf.Inline is not null)
                foreach (var child in leaf.Inline)
                    AddMapped(children, Map(child, source, offset, depth + 1, ref count, replacements, replacementStarts,
                        markerStart, markerPad));
            if (input is ContainerInline inlines)
                foreach (var child in inlines)
                    AddMapped(children, Map(child, source, offset, depth + 1, ref count, replacements, replacementStarts,
                        markerStart, markerPad));
        }
        return new RichNode(kind, $"{span.Start}:{kind}", span, text,
            Attributes: attributes.Count == 0 ? null : attributes,
            Children: children.Count == 0 ? null : children);
    }
}
