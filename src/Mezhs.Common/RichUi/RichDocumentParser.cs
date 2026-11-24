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
    // Expected validation failures are returned through the parser rather than
    // thrown. A limit failure cannot be recovered as a UI Unknown node.
    private sealed record ParseFailure(string Reason, bool Limit = false);
    private ParseFailure? _failure;

    public RichParseResult Parse(string source)
    {
        if (source.Length > MaxLength)
            return Failed(source, "Message exceeds the document size limit.");
        _source = source;
        _commands = [];
        _count = 0;
        _svgCount = 0;
        _svgChars = 0;
        _failure = null;
        try
        {
            _radioLiteral = MarkdownLiteralMask(_source);
            var nodes = ReadChildren(0, source.Length, null, 0, out _);
            if (_failure is { } failure) return Failed(source, failure.Reason);
            // Validate the final typed tree before releasing any command
            // candidates. Rich inline wrappers render as HTML phrasing
            // elements; they must not contain blocks or nested interactive
            // controls, even when Markdown lowering introduces those nodes.
            nodes = NormalizeContentModel(nodes);
            var done = _commands.FindIndex(c => c.Name == "DONE");
            if (done >= 0 && (done != _commands.Count - 1 ||
                _commands.Count(c => c.Name == "DONE") != 1))
                return Failed(source, "DONE must be the final Agent command and occur once.");
            return new RichParseResult(
                new RichDocument(RichDocument.CurrentVersion, true, nodes, []),
                _commands.ToArray());
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

    // The regular rich-document traversal owns node creation. Opaque content is
    // inspected separately by registered tag processors without rendering it as UI.
    private IReadOnlyList<RichNode> ReadChildren(int start, int end, string? expected,
        int depth, out int position, bool svgScope = false, bool inlineScope = false)
    {
        if (depth > MaxDepth)
        {
            _failure = new ParseFailure("Document nesting exceeds limit.", Limit: true);
            position = start;
            return [];
        }
        var result = new List<RichNode>();
        var cursor = start;
        var plainFrom = start;
        var inline = new List<RichNode>();
        bool Flush(int until)
        {
            if (until > plainFrom || inline.Count > 0)
                AppendMarkdown(result, plainFrom, until, svgScope, inlineScope, inline,
                    insideRichContainer: expected is not null);
            if (_failure is not null) return false;
            inline.Clear();
            plainFrom = until;
            return true;
        }
        while (cursor < end)
        {
            if (_failure is not null) { position = cursor; return []; }
            if (!svgScope && _source[cursor] == '{' && !IsEscaped(cursor) &&
                RichQuestionGrammar.TryParse(_source, cursor, end,
                    _radioLiteral, out var question, out var questionEnd))
            {
                if (!Flush(cursor)) { position = cursor; return []; }
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
                    if (!Flush(cursor)) { position = cursor; return []; }
                    position = tag.End;
                    return result;
                }
                cursor = tag.End; // A closing delimiter without an opener is ordinary text.
                continue;
            }
            if (!RichTagRegistry.TryGet(tag.Name, svgScope, out var definition))
            {
                // Unknown tags are structural, inert presentation controls.
                // Their boundary prevents root-only commands from leaking out.
                // Inside SVG the entire SVG becomes Unknown rather than placing
                // an HTML diagnostic element into native SVG geometry.
                if (svgScope)
                {
                    _failure = new ParseFailure($"Unsupported SVG child: {tag.Name}.");
                    position = cursor;
                    return [];
                }
                var unknownEnd = tag.SelfClosing ? tag.End :
                    FindContainerClose(tag.Name, tag.End, end);
                if (unknownEnd < 0) { cursor = tag.End; continue; }
                if (!tag.SelfClosing) unknownEnd += tag.Name.Length + 3;
                if (!Flush(cursor)) { position = cursor; return []; }
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
            // Ordinary command discovery and registered tag handlers share one
            // collector. Neither path executes host actions.
            if (definition.Role is RichTagRole.Command or RichTagRole.Marker)
            {
                CollectCommand(tag, definition, end, out cursor);
                if (_failure is not null) { position = cursor; return []; }
                continue;
            }

            var from = cursor;
            var inlineElement = !svgScope && InlineElements.Contains(tag.Name);
            if (definition.Role == RichTagRole.Leaf && !tag.SelfClosing)
            {
                if (svgScope)
                {
                    _failure = new ParseFailure("Invalid SVG leaf.");
                    position = cursor;
                    return [];
                }
                var leafClose = FindOpaqueClosing(tag.Name, tag.End, end);
                var leafEnd = leafClose < 0 ? tag.End : leafClose + tag.Name.Length + 3;
                if (!Flush(from)) { position = cursor; return []; }
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

            var validAttributes = TryParseAttributes(tag, definition, out var attributes, out var reason);
            if (validAttributes && (svgScope || tag.Name == "svg") &&
                (svgScope && !RichSvgValidation.AllowsChild(expected, tag.Name) ||
                 !RichSvgValidation.Valid(tag.Name, attributes)))
            {
                validAttributes = false;
                reason = $"Unsafe SVG attributes or nesting: {tag.Name}.";
            }
            if (!validAttributes)
            {
                if (svgScope)
                {
                    // The outer SVG is replaced by one inert Unknown node.
                    _failure = new ParseFailure(reason);
                    position = cursor;
                    return [];
                }
                if (!Flush(from)) { position = cursor; return []; }
                var unknownEnd = tag.SelfClosing ? tag.End : closer + tag.Name.Length + 3;
                AddUnknown(result, tag, unknownEnd, reason, depth,
                    opaque: !definition.SearchChildren);
                cursor = unknownEnd;
                plainFrom = cursor;
                continue;
            }
            if (svgScope || tag.Name == "svg")
            {
                if (++_svgCount > 250 || depth > 12)
                {
                    _failure = new ParseFailure("SVG resource budget exceeded.", Limit: true);
                    position = cursor;
                    return [];
                }
                _svgChars += attributes.Sum(item => item.Value.Length);
                if (_svgChars > 24_000)
                {
                    _failure = new ParseFailure("SVG content budget exceeded.", Limit: true);
                    position = cursor;
                    return [];
                }
            }
            if (!inlineElement && !Flush(from)) { position = cursor; return []; }
            cursor = tag.End;
            if (definition.Role is RichTagRole.OpaqueLiteral or RichTagRole.RawEditor)
            {
                if (tag.SelfClosing)
                {
                    _failure = new ParseFailure($"{tag.Name} requires a closing delimiter.");
                    position = cursor;
                    return [];
                }
                var after = closer + tag.Name.Length + 3;
                var commandStart = _commands.Count;
                if (definition.ProcessContent is { } process)
                    process(this, tag.End, closer, depth + 1);
                else
                    ScanOpaqueContent(tag.End, closer, depth + 1);
                if (_failure is not null) { position = cursor; return []; }
                var text = LiteralExcludingCommands(tag.End, closer, commandStart);
                Add(result, new RichNode(definition.Role == RichTagRole.OpaqueLiteral ? "literal" : "raw",
                    $"{from}:raw", new RichSourceSpan(from, after),
                    Text: text, Name: tag.Name, Attributes: attributes));
                cursor = after;
            }
            else if (definition.Role == RichTagRole.Leaf || tag.SelfClosing)
            {
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
                var children = ReadChildren(cursor, afterClose, tag.Name, depth + 1,
                    out var after, svgScope || tag.Name == "svg", inlineElement);
                if (_failure is { } childFailure)
                {
                    if (svgScope || childFailure.Limit)
                    {
                        position = cursor;
                        return [];
                    }
                    // The same boundary as the old nested validation catch:
                    // malformed UI becomes inert, whereas resource limits fail
                    // the entire document with no released command candidates.
                    _failure = null;
                    if (inlineElement && !Flush(from)) { position = cursor; return []; }
                    AddUnknown(result, tag, afterClose, childFailure.Reason, depth, opaque: true);
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
        if (!Flush(end)) { position = end; return []; }
        position = end;
        return result;
    }

    // All executable command tags use one validation and source-span collector.
    // An unmatched block opener remains text; invalid complete commands fail closed.
    private void CollectCommand(Tag tag, RichTagDefinition definition, int end, out int after)
    {
        after = tag.End;
        if (tag.SelfClosing)
        {
            _failure = new ParseFailure($"{tag.Name} cannot be self-closing.");
            return;
        }
        string? body = null;
        if (definition.Role == RichTagRole.Command)
        {
            var close = FindOpaqueClosing(tag.Name, tag.End, end);
            if (close < 0) return;
            body = _source[tag.End..close];
            after = close + tag.Name.Length + 3;
        }
        if (!TryParseAttributes(tag, definition, out _, out var error))
        {
            _failure = new ParseFailure(error);
            return;
        }
        if (_commands.Count == MaxNodes)
        {
            _failure = new ParseFailure("Command count exceeds limit.", Limit: true);
            return;
        }
        _commands.Add(new RichCommandCandidate(tag.Name, body,
            new RichSourceSpan(tag.Start, after)));
    }

    // The 'escape' tag registers this as its content processor. Its direct
    // executable children are collected; everything else stays literal.
    internal void ProcessEscapedContent(int start, int end, int depth) =>
        ScanOpaqueContent(start, end, depth, allowDirectCommands: true);

    // A generic walk of otherwise opaque source. It uses the same tag tokenizer,
    // paired-container matching and command collector as ReadChildren, but makes
    // no UI nodes. Nested registered processors are invoked irrespective of the
    // wrapper that contains them.
    private void ScanOpaqueContent(int start, int end, int depth,
        bool allowDirectCommands = false)
    {
        if (depth > MaxDepth)
        {
            _failure = new ParseFailure("Document nesting exceeds limit.", Limit: true);
            return;
        }
        for (var cursor = start; cursor < end && _failure is null;)
        {
            var next = _source.IndexOf('<', cursor);
            if (next < 0 || next >= end) break;
            cursor = next;
            if (!TryTag(cursor, end, out var tag)) { cursor++; continue; }
            if (tag.Closing) { cursor = tag.End; continue; }
            var known = RichTagRegistry.TryGet(tag.Name, out var definition);
            if (known && definition.Role is (RichTagRole.Command or RichTagRole.Marker))
            {
                if (allowDirectCommands && !definition.AllowAsChild)
                    CollectCommand(tag, definition, end, out cursor);
                else if (!tag.SelfClosing && definition.Role == RichTagRole.Command)
                {
                    var close = FindOpaqueClosing(tag.Name, tag.End, end);
                    cursor = close < 0 ? tag.End : close + tag.Name.Length + 3;
                }
                else cursor = tag.End;
                continue;
            }
            if (tag.SelfClosing) { cursor = tag.End; continue; }
            var closeAt = known && !definition!.SearchChildren
                ? FindOpaqueClosing(tag.Name, tag.End, end)
                : FindContainerClose(tag.Name, tag.End, end);
            if (closeAt < 0) { cursor = tag.End; continue; }
            if (known && definition!.ProcessContent is { } process &&
                TryParseAttributes(tag, definition, out _, out _))
                process(this, tag.End, closeAt, depth + 1);
            else
                ScanOpaqueContent(tag.End, closeAt, depth + 1);
            cursor = closeAt + tag.Name.Length + 3;
        }
    }

    // Project opaque text using command spans from the canonical traversal.
    // Presentation never decides which commands may execute.
    private string LiteralExcludingCommands(int start, int end, int commandStart)
    {
        if (commandStart == _commands.Count) return _source[start..end];
        var visible = new StringBuilder();
        var at = start;
        for (var i = commandStart; i < _commands.Count; i++)
        {
            var span = _commands[i].SourceSpan;
            visible.Append(_source, at, span.Start - at);
            at = span.End;
        }
        visible.Append(_source, at, end - at);
        return visible.ToString();
    }

    private void AddUnknown(List<RichNode> nodes, Tag tag, int end, string reason,
        int depth, bool opaque = false)
    {
        IReadOnlyList<RichNode>? children = null;
        if (!tag.SelfClosing && end > tag.End)
        {
            var bodyEnd = end - tag.Name.Length - 3;
            if (opaque)
            {
                var commandStart = _commands.Count;
                ScanOpaqueContent(tag.End, bodyEnd, depth + 1);
                if (_failure is not null) return;
                children = [new RichNode("literal", $"{tag.End}:literal",
                    new RichSourceSpan(tag.End, bodyEnd),
                    Text: LiteralExcludingCommands(tag.End, bodyEnd, commandStart))];
            }
            else
                children = ReadChildren(tag.End, end, tag.Name, depth + 1, out _);
        }
        if (_failure is not null) return;
        Add(nodes, new RichNode("unknown", $"{tag.Start}:unknown",
            new RichSourceSpan(tag.Start, end), Name: tag.Name,
            Attributes: new Dictionary<string, string> { ["reason"] = reason },
            Children: children));
    }

    // Balanced container matching shares the canonical tokenizer with parsing.
    // Processed literal bodies protect their own nested containers from closing
    // an unrelated parent; opaque command/editor bodies are never inspected.
    private int FindContainerClose(string name, int from, int end, int depth = 0)
    {
        if (depth > MaxDepth)
        {
            _failure = new ParseFailure("Document nesting exceeds limit.", Limit: true);
            return -1;
        }
        var processedTarget = RichTagRegistry.TryGet(name, out var target) &&
            target.ProcessContent is not null;
        var nesting = 1;
        for (var at = from; at < end;)
        {
            at = _source.IndexOf('<', at);
            if (at < 0 || at >= end) break;
            if (!TryTag(at, end, out var tag)) { at++; continue; }
            if (!tag.Closing && !tag.SelfClosing)
            {
                var known = RichTagRegistry.TryGet(tag.Name, out var definition);
                var opaque = known && !definition.SearchChildren &&
                    definition.Role is not (RichTagRole.Leaf or RichTagRole.Marker);
                var shield = tag.Name != name &&
                    (processedTarget || opaque || known && definition.ProcessContent is not null);
                if (shield)
                {
                    var nestedClose = opaque
                        ? FindOpaqueClosing(tag.Name, tag.End, end)
                        : FindContainerClose(tag.Name, tag.End, end, depth + 1);
                    if (_failure is not null) return -1;
                    if (nestedClose >= 0)
                    {
                        at = nestedClose + tag.Name.Length + 3;
                        continue;
                    }
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
        var close = _source.IndexOf("</" + name + ">", from, StringComparison.Ordinal);
        return close >= 0 && close < end ? close : -1;
    }

    private void Add(List<RichNode> nodes, RichNode node)
    {
        if (++_count > MaxNodes)
        {
            _failure = new ParseFailure("Document node limit exceeded.", Limit: true);
            return;
        }
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
            {
                _failure = new ParseFailure("SVG content budget exceeded.", Limit: true);
                return;
            }
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
            {
                _failure = new ParseFailure("Inline rich element did not survive Markdown lowering.");
                return;
            }
        }
        _count += convertedCount - priorChildren;
        if (_count > MaxNodes)
        {
            _failure = new ParseFailure("Document node limit exceeded.", Limit: true);
            return;
        }
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

    private static bool TryParseAttributes(Tag tag, RichTagDefinition definition,
        out IReadOnlyDictionary<string, string> attributes, out string reason)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        attributes = result;
        reason = "";
        if (tag.Closing) return true;
        var source = tag.RawAttributes.AsSpan().Trim();
        while (source.Length > 0)
        {
            if (result.Count >= MaxAttributes)
            {
                reason = "Too many tag attributes.";
                return false;
            }
            var i = 0;
            while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] is '-' or '_')) i++;
            if (i == 0)
            {
                reason = "Invalid tag attribute name.";
                return false;
            }
            var name = source[..i].ToString();
            if (result.ContainsKey(name) ||
                definition.AllowedAttributes is { } allowed && !allowed.Contains(name) ||
                definition.AllowedAttributes is null && UnsafePresentationAttribute(name))
            {
                reason = $"Unsupported or duplicate {tag.Name} attribute: {name}.";
                return false;
            }
            source = source[i..].TrimStart();
            string value;
            if (source.Length == 0 || source[0] != '=') value = "true";
            else
            {
                source = source[1..].TrimStart();
                if (source.Length == 0)
                {
                    reason = "Empty attribute value.";
                    return false;
                }
                if (source[0] is '\'' or '"')
                {
                    var quote = source[0];
                    var stop = source[1..].IndexOf(quote);
                    if (stop < 0)
                    {
                        reason = "Unclosed quoted attribute.";
                        return false;
                    }
                    value = source.Slice(1, stop).ToString();
                    source = source[(stop + 2)..];
                }
                else if (source[0] == '{')
                {
                    var stop = FindExpressionEnd(source);
                    if (stop < 0)
                    {
                        reason = "Unclosed attribute expression.";
                        return false;
                    }
                    var expression = source.Slice(1, stop - 1).ToString().Trim();
                    if (decimal.TryParse(expression, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var number))
                        value = number.ToString(CultureInfo.InvariantCulture);
                    else if (expression is "true" or "false")
                        value = expression;
                    else
                    {
                        // Literal validation is data-only; malformed syntax is
                        // an ordinary status result, not a thrown exception.
                        if (!new SafeLiteralReader(expression).TryRead(out var literal) ||
                            literal is not Dictionary<string, object?>)
                        {
                            reason = "Unsupported non-literal attribute expression.";
                            return false;
                        }
                        value = "json:" + Uri.EscapeDataString(JsonSerializer.Serialize(literal));
                    }
                    source = source[(stop + 1)..];
                }
                else
                {
                    reason = "Attribute must be quoted or a bounded literal.";
                    return false;
                }
            }
            if (value.Length > 2_048)
            {
                reason = "Attribute value too long.";
                return false;
            }
            result.Add(name, value);
            source = source.TrimStart();
        }
        return true;
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

        public bool TryRead(out object? value)
        {
            if (!TryValue(0, out value)) return false;
            Space();
            return _at == source.Length;
        }

        private bool TryValue(int depth, out object? value)
        {
            value = null;
            if (depth > 8 || ++_items > 128) return false;
            Space();
            if (_at >= source.Length) return false;
            if (source[_at] == '{')
            {
                if (!TryObject(depth + 1, out var obj)) return false;
                value = obj;
                return true;
            }
            if (source[_at] == '[')
            {
                if (!TryArray(depth + 1, out var array)) return false;
                value = array;
                return true;
            }
            if (source[_at] == '"')
            {
                if (!TryQuoted(out var quoted)) return false;
                value = quoted;
                return true;
            }
            var from = _at;
            while (_at < source.Length && !char.IsWhiteSpace(source[_at]) &&
                   source[_at] is not (',' or '}' or ']')) _at++;
            var token = source[from.._at];
            if (token == "true") { value = true; return true; }
            if (token == "false") { value = false; return true; }
            if (token == "null") return true;
            if (decimal.TryParse(token, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var n))
            {
                value = n;
                return true;
            }
            return false;
        }

        private bool TryObject(int depth, out Dictionary<string, object?> result)
        {
            _at++; // {
            result = new Dictionary<string, object?>(StringComparer.Ordinal);
            Space();
            if (Take('}')) return true;
            while (true)
            {
                Space();
                string key;
                if (_at < source.Length && source[_at] == '"')
                {
                    if (!TryQuoted(out key)) return false;
                }
                else if (!TryIdentifier(out key)) return false;
                Space();
                if (!Expect(':') || !TryValue(depth, out var value) || !result.TryAdd(key, value))
                    return false;
                Space();
                if (Take('}')) return true;
                if (!Expect(',')) return false;
            }
        }

        private bool TryArray(int depth, out List<object?> result)
        {
            _at++; // [
            result = [];
            Space();
            if (Take(']')) return true;
            while (true)
            {
                if (!TryValue(depth, out var value)) return false;
                result.Add(value);
                Space();
                if (Take(']')) return true;
                if (!Expect(',')) return false;
            }
        }

        private bool TryIdentifier(out string key)
        {
            key = "";
            var from = _at;
            if (_at >= source.Length || !(char.IsLetter(source[_at]) || source[_at] == '_'))
                return false;
            while (_at < source.Length &&
                   (char.IsLetterOrDigit(source[_at]) || source[_at] is '_' or '-')) _at++;
            key = source[from.._at];
            return true;
        }

        private bool TryQuoted(out string value)
        {
            value = "";
            if (!Take('"')) return false;
            var builder = new StringBuilder();
            while (_at < source.Length)
            {
                var c = source[_at++];
                if (c == '"')
                {
                    value = builder.ToString();
                    return true;
                }
                if (c < ' ') return false;
                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }
                if (_at >= source.Length) return false;
                var escape = source[_at++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (!TryUnicodeEscape(out var unicode)) return false;
                        if (char.IsHighSurrogate(unicode) && _at + 6 <= source.Length &&
                            source[_at] == '\\' && source[_at + 1] == 'u')
                        {
                            var afterHigh = _at;
                            _at += 2;
                            if (TryUnicodeEscape(out var low) && char.IsLowSurrogate(low))
                            {
                                builder.Append(unicode);
                                builder.Append(low);
                                break;
                            }
                            _at = afterHigh;
                        }
                        // Match System.Text.Json: isolated escaped UTF-16 surrogates
                        // are invalid rather than silently replaced in a literal.
                        if (char.IsSurrogate(unicode)) return false;
                        builder.Append(unicode);
                        break;
                    default: return false;
                }
            }
            return false;
        }

        private bool TryUnicodeEscape(out char value)
        {
            value = default;
            if (_at + 4 > source.Length ||
                !ushort.TryParse(source.AsSpan(_at, 4), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var number)) return false;
            _at += 4;
            value = (char)number;
            return true;
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

        private bool Expect(char expected)
        {
            Space();
            return Take(expected);
        }
    }
}
