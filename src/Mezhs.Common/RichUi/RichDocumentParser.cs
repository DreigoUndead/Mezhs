using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Mezhs.Common.RichUi;

/// <summary>
/// Single authority for bounded rich document syntax and source spans. Parsing
/// never performs an action. Invalid documents return inert literal source and
/// no command records; a host must never recover executable substrings.
/// </summary>
public sealed class RichDocumentParser
{
    private const int MaxLength = 180_000;
    private const int MaxNodes = 6_000;
    private const int MaxDepth = 32;
    private const int MaxTagLength = 4_096;
    private const int MaxAttributes = 16;
    // These display-only elements participate in their parent's inline Markdown
    // flow. Their bodies are still parsed recursively by this same C# parser.
    private static readonly IReadOnlySet<string> InlineElements =
        new HashSet<string>(["text", "title", "badge", "caption", "Link", "icon", "Cite"],
            StringComparer.Ordinal);
    private string _source = "";
    private bool[] _radioLiteral = [];
    // The same tag tokenizer discovers commands regardless of their surrounding text.
    private readonly Dictionary<int, RichSourceSpan> _commandSpans = new();
    private List<RichCommandCandidate> _commands = [];
    private int _count;
    private int _svgCount;
    private int _svgChars;

    public RichParseResult Parse(string source)
    {
        if (source.Length > MaxLength)
            return Failed(source, "Message exceeds the document size limit.");
        _source = source;
        _commands = [];
        _count = 0;
        _svgCount = 0;
        _svgChars = 0;
        _commandSpans.Clear();
        try
        {
            ScanCommands();
            _radioLiteral = MarkdownLiteralMask(_source);
            var nodes = ReadChildren(0, source.Length, null, 0, out _);
            // Validate the final typed tree before releasing any command
            // candidates. Rich inline wrappers render as HTML phrasing
            // elements; they must not contain blocks or nested interactive
            // controls, even when Markdown lowering introduces those nodes.
            ValidateContentModel(nodes);
            var done = _commands.FindIndex(c => c.Name == "DONE");
            if (done >= 0 && (done != _commands.Count - 1 ||
                _commands.Count(c => c.Name == "DONE") != 1))
                throw new RichDocumentException("DONE must be the final Agent command and occur once.");
            return new RichParseResult(
                new RichDocument(RichDocument.CurrentVersion, true, nodes, []),
                _commands.ToArray());
        }
        catch (RichDocumentException e)
        {
            return Failed(source, e.Message);
        }
        catch (Exception)
        {
            // Parse failures must not grant host capabilities or return a
            // partially parsed command list.
            return Failed(source, "The document could not be parsed safely.");
        }
    }

    // This runs over the canonical C# display AST, never over React markup.
    // The independent SearchChildren/AllowAsChild tag discovery flags do not
    // override HTML content-model constraints for the resulting DOM.
    private static void ValidateContentModel(IReadOnlyList<RichNode> nodes,
        bool phrasingOnly = false, bool insideInteractive = false)
    {
        foreach (var node in nodes)
        {
            var isElement = node.Kind == "element";
            var name = node.Name ?? "";
            var inlineElement = isElement && InlineElements.Contains(name);
            var richLink = isElement && name == "Link";
            var markdownLink = node.Kind == "link";
            if (phrasingOnly &&
                !(node.Kind is "text" or "literal" or "inlineCode" or
                    "strong" or "emphasis" or "strikethrough" or
                    "lineBreak" or "link" or "image" or "taskCheckbox" ||
                  inlineElement || isElement && name == "svg"))
                throw new RichDocumentException(
                    "Block or interactive content is forbidden inside inline rich elements.");
            // Markdown links and rich Link both render as anchors. A nested
            // anchor (or input) is invalid even if intermediate children
            // are strong/emphasis/text wrappers.
            if (insideInteractive &&
                (richLink || markdownLink || node.Kind == "taskCheckbox"))
                throw new RichDocumentException(
                    "Nested interactive content is forbidden inside links or buttons.");
            // SVG has its own separately validated content grammar. Native
            // geometry/text nodes are not HTML child elements.
            if (isElement && name == "svg") continue;
            if (node.Children is { Count: > 0 } children)
                ValidateContentModel(children,
                    phrasingOnly || inlineElement ||
                    node.Kind is "paragraph" or "heading" or
                        "strong" or "emphasis" or "strikethrough" or
                        "link" or "questionButton",
                    insideInteractive || richLink || markdownLink ||
                    node.Kind == "questionButton");
        }
    }

    private static RichParseResult Failed(string source, string reason) => new(
        new RichDocument(RichDocument.CurrentVersion, false,
            [new RichNode("literal", "0:literal", new RichSourceSpan(0, source.Length), Text: source)],
            [new RichDiagnostic(reason, new RichSourceSpan(0, source.Length))]),
        []);

    // Markdown semantics are used for the finite radio-question grammar only,
    // not for detecting rich or Agent tags.
    private static bool[] MarkdownLiteralMask(string source)
    {
        var mask = new bool[source.Length];
        var markdown = Markdown.Parse(source, new MarkdownPipelineBuilder()
            .UsePipeTables().UseEmphasisExtras().Build());
        void Visit(MarkdownObject node)
        {
            if (node is QuoteBlock or CodeBlock or CodeInline || IsComment(node, source))
            {
                var from = Math.Clamp(node.Span.Start, 0, source.Length);
                var until = Math.Clamp(node.Span.End + 1, from, source.Length);
                Array.Fill(mask, true, from, until - from);
                return;
            }
            if (node is ContainerBlock block)
                foreach (var child in block) Visit(child);
            else if (node is LeafBlock { Inline: { } inlines })
                Visit(inlines);
            else if (node is ContainerInline inline)
                foreach (var child in inline) Visit(child);
        }
        Visit(markdown);
        for (var at = 0; at < source.Length - 3; at++)
        {
            if (mask[at] || !source.AsSpan(at).StartsWith("<!--", StringComparison.Ordinal))
                continue;
            var close = source.IndexOf("-->", at + 4, StringComparison.Ordinal);
            var until = close < 0 ? source.Length : close + 3;
            Array.Fill(mask, true, at, until - at);
            at = until - 1;
        }
        return mask;
    }

    internal static bool[] MarkdownProtectionMask(string source) => MarkdownLiteralMask(source);

    private static bool IsComment(MarkdownObject node, string source) =>
        (node is HtmlInline or HtmlBlock) && node.Span.Start >= 0 &&
        node.Span.Start < source.Length &&
        source.AsSpan(node.Span.Start).StartsWith("<!--", StringComparison.Ordinal);

    // A matched command has authority regardless of presentation context.
    // Opaque SH/ATTACH bodies do not recursively expose nested commands.
    private void ScanCommands()
    {
        for (var at = 0; at < _source.Length;)
        {
            at = _source.IndexOf('<', at);
            if (at < 0) break;
            if (!TryTag(at, _source.Length, out var tag) || tag.Closing ||
                !RichTagRegistry.TryGet(tag.Name, out var definition) ||
                definition.Role is not (RichTagRole.Command or RichTagRole.Marker))
            {
                at++;
                continue;
            }
            if (tag.SelfClosing)
                throw new RichDocumentException($"{tag.Name} cannot be self-closing.");
            var after = tag.End;
            string? body = null;
            if (definition.Role == RichTagRole.Command)
            {
                var close = _source.IndexOf("</" + tag.Name + ">", tag.End,
                    StringComparison.Ordinal);
                if (close < 0) { at = tag.End; continue; }
                body = _source[tag.End..close];
                after = close + tag.Name.Length + 3;
            }
            ParseAttributes(tag, definition);
            if (_commands.Count == MaxNodes)
                throw new RichDocumentException("Command count exceeds limit.");
            var span = new RichSourceSpan(at, after);
            _commandSpans.Add(at, span);
            _commands.Add(new RichCommandCandidate(tag.Name, body, span));
            at = after;
        }
    }

    private string VisibleBody(int from, int end)
    {
        var visible = new System.Text.StringBuilder(end - from);
        var cursor = from;
        foreach (var command in _commands)
        {
            var span = command.SourceSpan;
            if (span.Start < cursor || span.End > end) continue;
            visible.Append(_source, cursor, span.Start - cursor);
            cursor = span.End;
        }
        visible.Append(_source, cursor, end - cursor);
        return visible.ToString();
    }

    private IReadOnlyList<RichNode> ReadChildren(int start, int end, string? expected,
        int depth, out int position, bool svgScope = false, bool inlineScope = false)
    {
        if (depth > MaxDepth) throw new RichDocumentException("Document nesting exceeds limit.");
        var result = new List<RichNode>();
        var cursor = start;
        var plainFrom = start;
        var inline = new List<RichNode>();
        void Flush(int until)
        {
            if (until > plainFrom || inline.Count > 0)
                AppendMarkdown(result, plainFrom, until, svgScope, inlineScope, inline);
            inline.Clear();
            plainFrom = until;
        }
        while (cursor < end)
        {
            if (_commandSpans.TryGetValue(cursor, out var command))
            {
                // Commands are invisible in display Markdown, but removing them
                // must not split the surrounding paragraph or emphasis scope.
                cursor = command.End;
                continue;
            }
            if (!svgScope && _source[cursor] == '{' && !IsEscaped(cursor) &&
                RichQuestionGrammar.TryParse(_source, cursor, end,
                    _radioLiteral, out var question, out var questionEnd))
            {
                Flush(cursor);
                Add(result, question);
                cursor = questionEnd;
                plainFrom = cursor;
                continue;
            }
            if (_source[cursor] != '<' || !TryTag(cursor, end, out var tag))
            {
                cursor++;
                continue;
            }
            if (tag.Closing)
            {
                if (expected == tag.Name)
                {
                    Flush(cursor);
                    position = tag.End;
                    return result;
                }
                cursor = tag.End; // A closing delimiter without an opener is ordinary text.
                continue;
            }
            if (!RichTagRegistry.TryGet(tag.Name, svgScope, out var definition))
            {
                // Only registered tags have rich syntax. Preserve unknown markup
                // as ordinary text while still discovering registered tags within it.
                if (RichTagRegistry.IsSvgTag(tag.Name))
                    throw new RichDocumentException($"SVG tag outside supported scope: {tag.Name}.");
                cursor = tag.End;
                continue;
            }
            // Commands were already discovered with no context exceptions.
            if (definition.Role is RichTagRole.Command or RichTagRole.Marker)
            {
                cursor = tag.End; // An unmatched opener is ordinary text.
                continue;
            }

            var from = cursor;
            var inlineElement = !svgScope && InlineElements.Contains(tag.Name);
            var closer = -1;
            if (!tag.SelfClosing && definition.Role != RichTagRole.Leaf)
            {
                closer = definition.SearchChildren
                    ? FindContainerClose(tag.Name, tag.End, end)
                    : FindOpaqueClosing(tag.Name, tag.End, end);
                if (closer < 0) { cursor = tag.End; continue; }
            }

            var attributes = ParseAttributes(tag, definition);
            if ((svgScope || tag.Name == "svg") &&
                (svgScope && !RichSvgValidation.AllowsChild(expected, tag.Name) ||
                 !RichSvgValidation.Valid(tag.Name, attributes)))
                throw new RichDocumentException($"Unsafe SVG attributes or nesting: {tag.Name}.");
            if (svgScope || tag.Name == "svg")
            {
                if (++_svgCount > 250 || depth > 12)
                    throw new RichDocumentException("SVG resource budget exceeded.");
                _svgChars += attributes.Sum(item => item.Value.Length);
                if (_svgChars > 24_000)
                    throw new RichDocumentException("SVG content budget exceeded.");
            }
            if (!inlineElement) Flush(from);
            cursor = tag.End;
            if (definition.Role is RichTagRole.OpaqueLiteral or RichTagRole.RawEditor)
            {
                if (tag.SelfClosing)
                    throw new RichDocumentException($"{tag.Name} requires a closing delimiter.");
                var after = closer + tag.Name.Length + 3;
                Add(result, new RichNode(definition.Role == RichTagRole.OpaqueLiteral ? "literal" : "raw",
                    $"{from}:raw", new RichSourceSpan(from, after),
                    Text: VisibleBody(tag.End, closer), Name: tag.Name, Attributes: attributes));
                cursor = after;
            }
            else if (definition.Role == RichTagRole.Leaf || tag.SelfClosing)
            {
                if (definition.Role == RichTagRole.Leaf && !tag.SelfClosing)
                    throw new RichDocumentException($"{tag.Name} must be self-closing.");
                var element = new RichNode("element", $"{from}:element", new RichSourceSpan(from, cursor),
                    Name: tag.Name, Attributes: attributes);
                if (inlineElement) inline.Add(element);
                else Add(result, element);
            }
            else if (!definition.SearchChildren)
            {
                var after = closer + tag.Name.Length + 3;
                var literal = new RichNode("literal", $"{tag.End}:literal",
                    new RichSourceSpan(tag.End, closer), Text: VisibleBody(tag.End, closer));
                var element = new RichNode("element", $"{from}:element",
                    new RichSourceSpan(from, after), Name: tag.Name,
                    Attributes: attributes, Children: [literal]);
                if (inlineElement) inline.Add(element);
                else Add(result, element);
                cursor = after;
            }
            else
            {
                var afterClose = closer + tag.Name.Length + 3;
                var children = ReadChildren(cursor, afterClose, tag.Name, depth + 1,
                    out var after, svgScope || tag.Name == "svg", inlineElement);
                var element = new RichNode("element", $"{from}:element",
                    new RichSourceSpan(from, after), Name: tag.Name,
                    Attributes: attributes, Children: children);
                if (inlineElement) inline.Add(element);
                else Add(result, element);
                cursor = after;
            }
            if (!inlineElement) plainFrom = cursor;
        }
        Flush(end);
        position = end;
        return result;
    }

    // Return the first matching close, with balanced nesting for UI containers.
    // Command bodies and raw elements do not contribute fake delimiters.
    private int FindContainerClose(string name, int from, int end)
    {
        var nesting = 1;
        for (var at = from; at < end;)
        {
            at = _source.IndexOf('<', at);
            if (at < 0 || at >= end) break;
            if (_commandSpans.TryGetValue(at, out var command))
            {
                at = command.End;
                continue;
            }
            if (!TryTag(at, end, out var tag)) { at++; continue; }
            if (!tag.Closing && !tag.SelfClosing &&
                RichTagRegistry.TryGet(tag.Name, out var definition) &&
                !definition.SearchChildren &&
                definition.Role is not (RichTagRole.Leaf or RichTagRole.Marker))
            {
                var opaqueClose = FindOpaqueClosing(tag.Name, tag.End, end);
                if (opaqueClose >= 0)
                {
                    at = opaqueClose + tag.Name.Length + 3;
                    continue;
                }
            }
            if (tag.Name == name)
            {
                if (tag.Closing && --nesting == 0) return at;
                if (!tag.Closing && !tag.SelfClosing) nesting++;
            }
            at = tag.End;
        }
        return -1;
    }

    private int FindOpaqueClosing(string name, int from, int end)
    {
        var closing = "</" + name + ">";
        for (var at = from; at < end;)
        {
            at = _source.IndexOf(closing, at, StringComparison.Ordinal);
            if (at < 0 || at >= end) return -1;
            // A closing-looking token inside an opaque command body is text.
            var insideCommand = _commands.Any(c =>
                c.SourceSpan.Start < at && c.SourceSpan.End > at);
            if (!insideCommand) return at;
            at += closing.Length;
        }
        return -1;
    }

    private void Add(List<RichNode> nodes, RichNode node)
    {
        if (++_count > MaxNodes) throw new RichDocumentException("Document node limit exceeded.");
        nodes.Add(node);
    }

    private void AppendMarkdown(List<RichNode> nodes, int start, int end, bool svgScope,
        bool inlineScope, IReadOnlyList<RichNode> inline)
    {
        if (end <= start) return;
        if (svgScope)
        {
            var raw = _source[start..end];
            _svgChars += raw.Length;
            if (_svgChars > 24_000)
                throw new RichDocumentException("SVG content budget exceeded.");
            if (!string.IsNullOrWhiteSpace(raw))
                Add(nodes, new RichNode("text", $"{start}:text", new RichSourceSpan(start, end), Text: raw));
            return;
        }
        // Inline rich nodes occupy original source spans while Markdig parses
        // surrounding text as one Markdown context. Their descendants were
        // already counted by the recursive parser; count only new AST nodes.
        var hidden = _commands.Select(c => c.SourceSpan)
            .Where(span => span.Start >= start && span.End <= end &&
                !inline.Any(node => span.Start >= node.SourceSpan.Start &&
                    span.End <= node.SourceSpan.End)).ToArray();
        var parsed = RichMarkdownNodes.Convert(_source[start..end], start, inlineScope, inline, hidden);
        var priorChildren = inline.Sum(n => n.Children?.Sum(CountNodes) ?? 0);
        var convertedCount = parsed.Sum(CountNodes);
        if (inline.Count > 0)
        {
            var remaining = new HashSet<RichNode>(inline, ReferenceEqualityComparer.Instance);
            void Visit(RichNode n)
            {
                remaining.Remove(n);
                if (n.Children is not null)
                    foreach (var child in n.Children) Visit(child);
            }
            foreach (var node in parsed) Visit(node);
            if (remaining.Count > 0)
                throw new RichDocumentException("Inline rich element did not survive Markdown lowering.");
        }
        _count += convertedCount - priorChildren;
        if (_count > MaxNodes) throw new RichDocumentException("Document node limit exceeded.");
        nodes.AddRange(parsed);
    }

    private static int CountNodes(RichNode node) =>
        1 + (node.Children?.Sum(CountNodes) ?? 0);

    private bool IsEscaped(int index)
    {
        var backslashes = 0;
        for (var i = index - 1; i >= 0 && _source[i] == '\\'; i++) backslashes++;
        return (backslashes & 1) == 1;
    }

    private sealed record Tag(string Name, bool Closing, bool SelfClosing, int Start, int End,
        string RawAttributes);

    private bool TryTag(int from, int end, out Tag tag)
    {
        tag = null!;
        var i = from + 1;
        var closing = i < end && _source[i] == '/';
        if (closing) i++;
        var startName = i;
        if (i >= end || !char.IsLetter(_source[i])) return false;
        while (i < end && (char.IsLetterOrDigit(_source[i]) || _source[i] is '-' or '_')) i++;
        var name = _source[startName..i];
        if (i >= end || (_source[i] != '>' && _source[i] != '/' && !char.IsWhiteSpace(_source[i])))
            return false;
        var attrStart = i;
        var quote = '\0';
        var braces = 0;
        for (; i < end && i - from < MaxTagLength; i++)
        {
            var c = _source[i];
            if (quote != '\0')
            {
                if (c == '\\' && i + 1 < end) { i++; continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '\'' or '"' or '`') { quote = c; continue; }
            if (c == '{') { braces++; continue; }
            if (c == '}' && braces > 0) { braces--; continue; }
            if (c != '>' || braces != 0) continue;
            var attributes = _source[attrStart..i];
            var selfClosing = attributes.TrimEnd().EndsWith('/');
            if (selfClosing) attributes = attributes.TrimEnd()[..^1];
            if (closing && (selfClosing || !string.IsNullOrWhiteSpace(attributes)))
            {
                if (RichTagRegistry.TryGet(name, out _) || RichTagRegistry.IsSvgTag(name))
                    throw new RichDocumentException("Invalid closing tag.");
                return false; // Malformed unknown closing markup is ordinary text.
            }
            tag = new Tag(name, closing, selfClosing, from, i + 1, attributes);
            return true;
        }
        return false; // Incomplete tag syntax remains ordinary text.
    }

    // Unknown *presentation* attributes stay inert data: React components read only
    // their own namespaced values, never spread these directly onto the DOM.
    // Event/HTML/DOM injection properties remain invalid even in that namespace.
    private static bool UnsafePresentationAttribute(string name) =>
        name.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
        name is "style" or "class" or "className" or "dangerouslySetInnerHTML" or
            "innerHTML" or "ref" or "srcDoc" or "suppressHydrationWarning";

    private static IReadOnlyDictionary<string, string> ParseAttributes(Tag tag, RichTagDefinition definition)
    {
        if (tag.Closing) return new Dictionary<string, string>();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var source = tag.RawAttributes.AsSpan().Trim();
        while (source.Length > 0)
        {
            if (result.Count >= MaxAttributes)
                throw new RichDocumentException("Too many tag attributes.");
            var i = 0;
            while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] is '-' or '_')) i++;
            if (i == 0) throw new RichDocumentException("Invalid tag attribute name.");
            var name = source[..i].ToString();
            if (result.ContainsKey(name) ||
                definition.AllowedAttributes is { } allowed && !allowed.Contains(name) ||
                definition.AllowedAttributes is null && UnsafePresentationAttribute(name))
                throw new RichDocumentException($"Unsupported or duplicate {tag.Name} attribute: {name}.");
            source = source[i..].TrimStart();
            string value;
            if (source.Length == 0 || source[0] != '=') value = "true";
            else
            {
                source = source[1..].TrimStart();
                if (source.Length == 0) throw new RichDocumentException("Empty attribute value.");
                if (source[0] is '\'' or '"')
                {
                    var quote = source[0];
                    var stop = source[1..].IndexOf(quote);
                    if (stop < 0) throw new RichDocumentException("Unclosed quoted attribute.");
                    value = source.Slice(1, stop).ToString();
                    source = source[(stop + 2)..];
                }
                else if (source[0] == '{')
                {
                    var stop = source.IndexOf('}');
                    if (stop < 0) throw new RichDocumentException("Unclosed attribute expression.");
                    value = source.Slice(1, stop - 1).ToString().Trim();
                    if (!decimal.TryParse(value, System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out _) &&
                        value != "true" && value != "false")
                        throw new RichDocumentException("Executable attribute expressions are not supported.");
                    source = source[(stop + 1)..];
                }
                else throw new RichDocumentException("Attribute must be quoted or a bounded literal.");
            }
            if (value.Length > 2_048) throw new RichDocumentException("Attribute value too long.");
            result.Add(name, value);
            source = source.TrimStart();
        }
        return result;
    }

    private sealed class RichDocumentException(string message) : Exception(message);
}
