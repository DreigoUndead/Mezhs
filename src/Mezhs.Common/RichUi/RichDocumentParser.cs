using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.Globalization;
using System.Text;
using System.Text.Json;

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
    // These names must not bypass active-content validation as unknown markup.
    // Markdown code literals may still show them as non-executable examples.
    private static readonly IReadOnlySet<string> UnsafeRawTags =
        new HashSet<string>(["script", "style", "iframe", "object", "embed", "foreignObject", "image", "use", "animate", "animateMotion", "animateTransform", "set", "feImage"], StringComparer.OrdinalIgnoreCase);
    private bool[] _radioLiteral = [];
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
        try
        {
            _radioLiteral = MarkdownLiteralMask(_source);
            var nodes = ReadChildren(0, source.Length, null, 0, out _);
            // Validate the final typed tree before releasing any command
            // candidates. Rich inline wrappers render as HTML phrasing
            // elements; they must not contain blocks or nested interactive
            // controls, even when Markdown lowering introduces those nodes.
            nodes = NormalizeContentModel(nodes);
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

    // UI content-model errors are displayed as inert diagnostics, not fatal.
    private IReadOnlyList<RichNode> NormalizeContentModel(IReadOnlyList<RichNode> nodes,
        bool phrasingOnly = false, bool insideInteractive = false)
    {
        var result = new List<RichNode>(nodes.Count);
        foreach (var node in nodes)
        {
            var element = node.Kind == "element";
            var name = node.Name ?? "";
            var inline = element && InlineElements.Contains(name);
            var richLink = element && name == "Link";
            var mdLink = node.Kind == "link";
            var notPhrasing = phrasingOnly &&
                !(node.Kind is "text" or "literal" or "inlineCode" or
                    "strong" or "emphasis" or "strikethrough" or
                    "lineBreak" or "link" or "image" or "taskCheckbox" ||
                    inline || element && name == "svg");
            var nestedInteractive = insideInteractive &&
                (richLink || mdLink || node.Kind == "taskCheckbox");
            if (notPhrasing || nestedInteractive)
            {
                var span = node.SourceSpan;
                result.Add(new RichNode("unknown", node.Id + ":unknown", span,
                    Name: name.Length > 0 ? name : node.Kind,
                    Attributes: new Dictionary<string, string> {
                        ["reason"] = notPhrasing ? "Invalid inline content" : "Nested interactive content" },
                    Children: [new RichNode("literal", node.Id + ":literal", span,
                        Text: _source[span.Start..span.End])]));
                continue;
            }
            if (node.Kind is "unknown" || element && name == "svg")
            {
                result.Add(node);
                continue;
            }
            if (node.Children is { Count: > 0 } children)
                result.Add(node with { Children = NormalizeContentModel(children,
                    phrasingOnly || inline ||
                    node.Kind is "paragraph" or "heading" or
                        "strong" or "emphasis" or "strikethrough" or "link" or "questionButton",
                    insideInteractive || richLink || mdLink || node.Kind == "questionButton") });
            else result.Add(node);
        }
        return result;
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

    private IReadOnlyList<RichNode> ReadChildren(int start, int end, string? expected,
        int depth, out int position, bool svgScope = false, bool inlineScope = false,
        bool escapeLiteral = false)
    {
        if (depth > MaxDepth) throw new RichDocumentLimitException("Document nesting exceeds limit.");
        var result = new List<RichNode>();
        var cursor = start;
        var plainFrom = start;
        var inline = new List<RichNode>();
        void Flush(int until)
        {
            // Root escape scans commands using this same grammar, but its other
            // contents remain literal rather than Markdown/presentation nodes.
            if (escapeLiteral) { plainFrom = until; return; }
            if (until > plainFrom || inline.Count > 0)
                AppendMarkdown(result, plainFrom, until, svgScope, inlineScope, inline,
                    insideRichContainer: expected is not null);
            inline.Clear();
            plainFrom = until;
        }
        while (cursor < end)
        {
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
            if (escapeLiteral &&
                (!RichTagRegistry.TryGet(tag.Name, out var escapedDefinition) ||
                 escapedDefinition.AllowAsChild ||
                 escapedDefinition.Role is not (RichTagRole.Command or RichTagRole.Marker)))
            {
                // All non-command markup within escape stays inert. In particular,
                // an entire paired UI element cannot expose its descendants.
                if (tag.SelfClosing) { cursor = tag.End; continue; }
                var close = RichTagRegistry.TryGet(tag.Name, out var ignored) && !ignored.SearchChildren
                    ? FindOpaqueClosing(tag.Name, tag.End, end)
                    : FindContainerClose(tag.Name, tag.End, end);
                cursor = close < 0 ? end : close + tag.Name.Length + 3;
                continue;
            }
            if (!RichTagRegistry.TryGet(tag.Name, svgScope, out var definition))
            {
                // Unknown tags are structural, inert presentation controls.
                // Their boundary prevents root-only commands from leaking out.
                // Inside SVG the entire SVG becomes Unknown rather than placing
                // an HTML diagnostic element into native SVG geometry.
                if (svgScope)
                    throw new RichDocumentException($"Unsupported SVG child: {tag.Name}.");
                var unknownEnd = tag.SelfClosing ? tag.End :
                    FindContainerClose(tag.Name, tag.End, end);
                if (unknownEnd < 0) { cursor = tag.End; continue; }
                if (!tag.SelfClosing) unknownEnd += tag.Name.Length + 3;
                Flush(cursor);
                AddUnknown(result, tag, unknownEnd, "Unsupported control", depth);
                cursor = unknownEnd;
                plainFrom = cursor;
                continue;
            }
            // One traversal enforces the registry eligibility policy. A
            // forbidden opaque child stays literal without exposing its body.
            if (expected is not null && !definition.AllowAsChild)
            {
                if (!tag.SelfClosing && !definition.SearchChildren &&
                    definition.Role is not (RichTagRole.Leaf or RichTagRole.Marker))
                {
                    var close = FindOpaqueClosing(tag.Name, tag.End, end);
                    cursor = close < 0 ? tag.End : close + tag.Name.Length + 3;
                }
                else cursor = tag.End;
                continue;
            }
            // Commands use the same tokenizer and structural traversal as UI;
            // they produce host-only spans, not display nodes.
            if (definition.Role is RichTagRole.Command or RichTagRole.Marker)
            {
                if (tag.SelfClosing)
                    throw new RichDocumentException($"{tag.Name} cannot be self-closing.");
                var after = tag.End;
                string? body = null;
                if (definition.Role == RichTagRole.Command)
                {
                    var close = FindOpaqueClosing(tag.Name, tag.End, end);
                    if (close < 0) { cursor = tag.End; continue; }
                    body = _source[tag.End..close];
                    after = close + tag.Name.Length + 3;
                }
                ParseAttributes(tag, definition);
                if (_commands.Count == MaxNodes)
                    throw new RichDocumentLimitException("Command count exceeds limit.");
                _commands.Add(new RichCommandCandidate(tag.Name, body,
                    new RichSourceSpan(cursor, after)));
                cursor = after;
                continue;
            }

            var from = cursor;
            var inlineElement = !svgScope && InlineElements.Contains(tag.Name);
            if (definition.Role == RichTagRole.Leaf && !tag.SelfClosing)
            {
                if (svgScope) throw new RichDocumentException("Invalid SVG leaf.");
                var leafClose = FindOpaqueClosing(tag.Name, tag.End, end);
                var leafEnd = leafClose < 0 ? tag.End : leafClose + tag.Name.Length + 3;
                Flush(from);
                AddUnknown(result, tag, leafEnd, "Leaf requires self-closing syntax", depth,
                    opaque: leafClose >= 0);
                cursor = leafEnd;
                plainFrom = cursor;
                continue;
            }
            var closer = -1;
            if (!tag.SelfClosing && definition.Role != RichTagRole.Leaf)
            {
                closer = definition.SearchChildren
                    ? FindContainerClose(tag.Name, tag.End, end)
                    : FindOpaqueClosing(tag.Name, tag.End, end);
                if (closer < 0) { cursor = tag.End; continue; }
            }

            IReadOnlyDictionary<string, string> attributes;
            try
            {
                attributes = ParseAttributes(tag, definition);
                if ((svgScope || tag.Name == "svg") &&
                    (svgScope && !RichSvgValidation.AllowsChild(expected, tag.Name) ||
                     !RichSvgValidation.Valid(tag.Name, attributes)))
                    throw new RichDocumentException($"Unsafe SVG attributes or nesting: {tag.Name}.");
            }
            catch (RichDocumentException e) when (e is not RichDocumentLimitException)
            {
                if (svgScope) throw; // Parent SVG falls back as one inert unit.
                Flush(from);
                var unknownEnd = tag.SelfClosing ? tag.End : closer + tag.Name.Length + 3;
                AddUnknown(result, tag, unknownEnd, e.Message, depth,
                    opaque: !definition.SearchChildren);
                cursor = unknownEnd;
                plainFrom = cursor;
                continue;
            }
            if (svgScope || tag.Name == "svg")
            {
                if (++_svgCount > 250 || depth > 12)
                    throw new RichDocumentLimitException("SVG resource budget exceeded.");
                _svgChars += attributes.Sum(item => item.Value.Length);
                if (_svgChars > 24_000)
                    throw new RichDocumentLimitException("SVG content budget exceeded.");
            }
            if (tag.Name == "escape" && expected is null && !svgScope)
            {
                // An escape containing only one complete command is itself the
                // command span. Like a plain root SH, it must not split Markdown
                // paragraphs or leave an empty escape wrapper in VisibleContent.
                if (TryReadSplitEscapedCommand(tag, closer, end, out var split, out var splitEnd) ||
                    TryReadCompleteEscapedCommand(tag, closer, out split))
                {
                    if (_commands.Count == MaxNodes)
                        throw new RichDocumentLimitException("Command count exceeds limit.");
                    _commands.Add(split);
                    cursor = splitEnd > 0 ? splitEnd : closer + tag.Name.Length + 3;
                    continue;
                }
            }
            if (!inlineElement) Flush(from);
            cursor = tag.End;
            if (definition.Role is RichTagRole.OpaqueLiteral or RichTagRole.RawEditor)
            {
                if (tag.SelfClosing)
                    throw new RichDocumentException($"{tag.Name} requires a closing delimiter.");
                var after = closer + tag.Name.Length + 3;
                if (tag.Name == "escape" && expected is null && !svgScope)
                {
                    // The ordinary command branch remains the sole source of
                    // command candidates. Scan escape's contents with that same
                    // traversal while suppressing all other markup.
                    var before = _commands.Count;
                    ReadChildren(tag.End, closer, null, depth + 1, out _, escapeLiteral: true);
                    var visible = new StringBuilder();
                    var keptFrom = tag.End;
                    foreach (var command in _commands.Skip(before))
                    {
                        visible.Append(_source, keptFrom, command.SourceSpan.Start - keptFrom);
                        keptFrom = command.SourceSpan.End;
                    }
                    visible.Append(_source, keptFrom, closer - keptFrom);
                    Add(result, new RichNode("literal", $"{from}:raw",
                        new RichSourceSpan(from, after), Text: visible.ToString(),
                        Name: tag.Name, Attributes: attributes));
                }
                else
                    Add(result, new RichNode(definition.Role == RichTagRole.OpaqueLiteral ? "literal" : "raw",
                        $"{from}:raw", new RichSourceSpan(from, after),
                        Text: _source[tag.End..closer], Name: tag.Name, Attributes: attributes));
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
                    new RichSourceSpan(tag.End, closer), Text: _source[tag.End..closer]);
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
                IReadOnlyList<RichNode> children;
                int after;
                try
                {
                    children = ReadChildren(cursor, afterClose, tag.Name, depth + 1,
                        out after, svgScope || tag.Name == "svg", inlineElement);
                }
                catch (RichDocumentException e) when (!svgScope && e is not RichDocumentLimitException)
                {
                    // A malformed UI subtree is one inert diagnostic block.
                    // Never let its invalid children turn into root commands.
                    if (inlineElement) Flush(from);
                    AddUnknown(result, tag, afterClose, e.Message, depth, opaque: true);
                    cursor = afterClose;
                    plainFrom = cursor;
                    continue;
                }
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

    private void AddUnknown(List<RichNode> nodes, Tag tag, int end, string reason,
        int depth, bool opaque = false)
    {
        IReadOnlyList<RichNode>? children = null;
        if (!tag.SelfClosing && end > tag.End)
        {
            var bodyEnd = end - tag.Name.Length - 3;
            if (opaque)
                children = [new RichNode("literal", $"{tag.End}:literal",
                    new RichSourceSpan(tag.End, bodyEnd), Text: _source[tag.End..bodyEnd])];
            else
                children = ReadChildren(tag.End, end, tag.Name, depth + 1, out _);
        }
        Add(nodes, new RichNode("unknown", $"{tag.Start}:unknown",
            new RichSourceSpan(tag.Start, end), Name: tag.Name,
            Attributes: new Dictionary<string, string> { ["reason"] = reason },
            Children: children));
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

    // A single complete protocol tag can stand inside a root escape without
    // changing the Markdown flow around that wrapper.
    private bool TryReadCompleteEscapedCommand(Tag wrapper, int wrapperClose,
        out RichCommandCandidate command)
    {
        command = null!;
        if (!TryTag(wrapper.End, wrapperClose, out var inner) || inner.Closing ||
            inner.SelfClosing || !RichTagRegistry.TryGet(inner.Name, out var definition) ||
            definition.AllowAsChild ||
            definition.Role is not (RichTagRole.Command or RichTagRole.Marker))
            return false;

        var after = inner.End;
        string? body = null;
        if (definition.Role == RichTagRole.Command)
        {
            var close = FindOpaqueClosing(inner.Name, inner.End, wrapperClose);
            if (close < 0) return false;
            body = _source[inner.End..close];
            after = close + inner.Name.Length + 3;
        }
        if (after != wrapperClose) return false;
        ParseAttributes(inner, definition);
        command = new RichCommandCandidate(inner.Name, body,
            new RichSourceSpan(wrapper.Start, wrapperClose + wrapper.Name.Length + 3));
        return true;
    }

    // Handles the precise split form <escape><SH></escape>body<escape></SH></escape>.
    // Only registered root-only block commands qualify; all other content remains
    // opaque. Find the first command close, as for ordinary opaque SH/ATTACH.
    private bool TryReadSplitEscapedCommand(Tag wrapper, int wrapperClose, int end,
        out RichCommandCandidate command, out int after)
    {
        command = null!;
        after = 0;
        var at = wrapper.End;
        while (at < wrapperClose && char.IsWhiteSpace(_source[at])) at++;
        if (!TryTag(at, wrapperClose, out var opener) || opener.Closing || opener.SelfClosing ||
            !RichTagRegistry.TryGet(opener.Name, out var definition) ||
            definition.AllowAsChild || definition.Role != RichTagRole.Command ||
            !string.IsNullOrWhiteSpace(_source[opener.End..wrapperClose]))
            return false;

        var bodyStart = wrapperClose + wrapper.Name.Length + 3;
        var close = FindOpaqueClosing(opener.Name, bodyStart, end);
        if (close < 0) return false;
        var wrapperStart = _source.LastIndexOf("<escape", close,
            close - bodyStart + 1, StringComparison.Ordinal);
        if (wrapperStart < bodyStart || !TryTag(wrapperStart, end, out var closingWrapper) ||
            closingWrapper.Name != "escape" || closingWrapper.Closing ||
            closingWrapper.SelfClosing || closingWrapper.End > close)
            return false;
        var wrapperEnd = FindOpaqueClosing("escape", closingWrapper.End, end);
        var closeLength = opener.Name.Length + 3;
        if (wrapperEnd < 0 || close + closeLength > wrapperEnd ||
            !string.IsNullOrWhiteSpace(_source[closingWrapper.End..close]) ||
            !string.IsNullOrWhiteSpace(_source[(close + closeLength)..wrapperEnd]))
            return false;

        ParseAttributes(opener, definition);
        ParseAttributes(closingWrapper, RichTagRegistry.TryGet("escape", out var escapeDef)
            ? escapeDef : throw new RichDocumentException("Missing escape definition."));
        after = wrapperEnd + closingWrapper.Name.Length + 3;
        command = new RichCommandCandidate(opener.Name, _source[bodyStart..wrapperStart],
            new RichSourceSpan(wrapper.Start, after));
        return true;
    }

    private int FindOpaqueClosing(string name, int from, int end)
    {
        var close = _source.IndexOf("</" + name + ">", from, StringComparison.Ordinal);
        return close >= 0 && close < end ? close : -1;
    }

    private void Add(List<RichNode> nodes, RichNode node)
    {
        if (++_count > MaxNodes) throw new RichDocumentLimitException("Document node limit exceeded.");
        nodes.Add(node);
    }

    private void AppendMarkdown(List<RichNode> nodes, int start, int end, bool svgScope,
        bool inlineScope, IReadOnlyList<RichNode> inline, bool insideRichContainer)
    {
        if (end <= start) return;
        if (svgScope)
        {
            var raw = _source[start..end];
            _svgChars += raw.Length;
            if (_svgChars > 24_000)
                throw new RichDocumentLimitException("SVG content budget exceeded.");
            if (!string.IsNullOrWhiteSpace(raw))
                Add(nodes, new RichNode("text", $"{start}:text", new RichSourceSpan(start, end), Text: raw));
            return;
        }
        // Inline rich nodes occupy original source spans while Markdig parses
        // surrounding text as one Markdown context. Their descendants were
        // already counted by the recursive parser; count only new AST nodes.
        var hidden = _commands.Select(c => c.SourceSpan)
            .Where(span => span.Start >= start && span.End <= end).ToArray();
        var parsed = RichMarkdownNodes.Convert(_source[start..end], start,
            inlineScope, inline, hidden, insideRichContainer);
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
        if (_count > MaxNodes) throw new RichDocumentLimitException("Document node limit exceeded.");
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
                return false; // Malformed closing markup is inert text.
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
                    var stop = FindExpressionEnd(source);
                    if (stop < 0) throw new RichDocumentException("Unclosed attribute expression.");
                    var expression = source.Slice(1, stop - 1).ToString().Trim();
                    if (decimal.TryParse(expression, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var number))
                        value = number.ToString(CultureInfo.InvariantCulture);
                    else if (expression is "true" or "false")
                        value = expression;
                    else
                    {
                        // Data-only object/array literals become the same json:
                        // attribute representation already used by the UI.
                        // Identifiers, function calls and JS operators never run.
                        try
                        {
                            var literal = new SafeLiteralReader(expression).Read();
                            if (literal is not Dictionary<string, object?>)
                                throw new FormatException("Expected an object literal.");
                            value = "json:" + Uri.EscapeDataString(JsonSerializer.Serialize(literal));
                        }
                        catch (Exception e) when (e is FormatException or JsonException or OverflowException)
                        {
                            throw new RichDocumentException("Unsupported non-literal attribute expression.");
                        }
                    }
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

    // Find the outer brace with correct quote/escape handling. TryTag already
    // tokenizes nested braces; attribute parsing must consume the same span.
    private static int FindExpressionEnd(ReadOnlySpan<char> source)
    {
        var depth = 0;
        char quote = '\0';
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (quote != '\0')
            {
                if (c == '\\') { i++; continue; }
                if (c == quote) quote = '\0';
            }
            else if (c is '\'' or '"' or '`') quote = c;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return -1;
    }

    // Strict bounded JSON-like data literals, not a JavaScript evaluator.
    // Unquoted *keys* are supported for the common JSX object syntax, but
    // string values must use JSON double quotes. No code, methods or spreads.
    private sealed class SafeLiteralReader(string source)
    {
        private int _at;
        private int _items;

        public object? Read()
        {
            var value = Value(0);
            Space();
            if (_at != source.Length) throw new FormatException("Unexpected literal tail.");
            return value;
        }

        private object? Value(int depth)
        {
            if (depth > 8 || ++_items > 128)
                throw new FormatException("Literal resource budget exceeded.");
            Space();
            if (_at >= source.Length) throw new FormatException("Missing literal.");
            if (source[_at] == '{') return Object(depth + 1);
            if (source[_at] == '[') return Array(depth + 1);
            if (source[_at] == '"') return Quoted();
            var from = _at;
            while (_at < source.Length && !char.IsWhiteSpace(source[_at]) &&
                   source[_at] is not (',' or '}' or ']')) _at++;
            var token = source[from.._at];
            if (token == "true") return true;
            if (token == "false") return false;
            if (token == "null") return null;
            if (decimal.TryParse(token, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var n))
                return n;
            throw new FormatException("Only data literals are supported.");
        }

        private Dictionary<string, object?> Object(int depth)
        {
            _at++; // {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            Space();
            if (Take('}')) return result;
            while (true)
            {
                Space();
                var key = _at < source.Length && source[_at] == '"'
                    ? Quoted() : Identifier();
                Space();
                Expect(':');
                if (!result.TryAdd(key, Value(depth)))
                    throw new FormatException("Duplicate literal key.");
                Space();
                if (Take('}')) return result;
                Expect(',');
            }
        }

        private List<object?> Array(int depth)
        {
            _at++; // [
            var result = new List<object?>();
            Space();
            if (Take(']')) return result;
            while (true)
            {
                result.Add(Value(depth));
                Space();
                if (Take(']')) return result;
                Expect(',');
            }
        }

        private string Identifier()
        {
            var from = _at;
            if (_at >= source.Length || !(char.IsLetter(source[_at]) || source[_at] == '_'))
                throw new FormatException("Invalid literal property.");
            while (_at < source.Length &&
                   (char.IsLetterOrDigit(source[_at]) || source[_at] is '_' or '-')) _at++;
            return source[from.._at];
        }

        private string Quoted()
        {
            var from = _at++;
            var escaped = false;
            while (_at < source.Length)
            {
                var c = source[_at++];
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"')
                    return JsonSerializer.Deserialize<string>(source[from.._at]) ??
                        throw new FormatException("Invalid string literal.");
            }
            throw new FormatException("Unclosed literal string.");
        }

        private void Space()
        {
            while (_at < source.Length && char.IsWhiteSpace(source[_at])) _at++;
        }

        private bool Take(char expected)
        {
            if (_at >= source.Length || source[_at] != expected) return false;
            _at++;
            return true;
        }

        private void Expect(char expected)
        {
            Space();
            if (!Take(expected)) throw new FormatException("Invalid literal separator.");
        }
    }

    private class RichDocumentException(string message) : Exception(message);
    private sealed class RichDocumentLimitException(string message) : RichDocumentException(message);
}
