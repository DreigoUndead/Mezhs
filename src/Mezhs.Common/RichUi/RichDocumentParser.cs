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
public sealed class RichDocumentParser(RichTagCatalog? catalog = null)
{
    private readonly RichTagCatalog _catalog = catalog ?? RichTagRegistry.Ui;
    private const int MaxLength = 180_000;
    private const int MaxNodes = 6_000;
    // Bounded structural work, including tokens that do not become UI nodes.
    private const int MaxTagTokens = 24_000;
    private const int MaxDepth = 32;
    private const int MaxTagLength = 4_096;
    private const int MaxAttributes = 16;
    private string _source = "";
    // These names must not bypass active-content validation as unknown markup.
    // Markdown code literals may still show them as non-executable examples.
    private static readonly IReadOnlySet<string> UnsafeRawTags =
        new HashSet<string>(["script", "style", "iframe", "object", "embed", "foreignObject", "image", "use", "animate", "animateMotion", "animateTransform", "set", "feImage"], StringComparer.OrdinalIgnoreCase);
    private bool[] _radioLiteral = [];
    private List<RichCommandCandidate> _commands = [];
    private int _count;
    internal RichSvgQuota SvgQuota { get; } = new();
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
        SvgQuota.Reset();
        _failure = null;
        try
        {
            _radioLiteral = MarkdownLiteralMask(_source);
            var roots = FindRootTags(0, source.Length);
            if (_failure is { } structuralFailure) return Failed(source, structuralFailure.Reason);
            var nodes = RenderChildren(0, source.Length, roots, 0);
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
            RichTagDefinition? registered = null;
            if (element && _catalog.TryGet(name, out var definition))
                registered = definition;
            var inline = registered?.Inline == true;
            var richLink = registered?.Interactive == true;
            var mdLink = node.Kind == "link";
            var notPhrasing = phrasingOnly &&
                !(RichMarkdownNodes.IsPhrasing(node.Kind) ||
                    inline || registered?.PhrasingAllowed == true);
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
            if (node.Kind is "unknown" || registered?.PreserveChildren == true)
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

    // Every markup boundary is paired by this one structural matcher. It does
    // not grant any tag authority: unknown, excluded and opaque wrappers still
    // form boundaries before semantic discovery decides what to render.
    // Parent links are private to parsing; no such links or handlers reach JSON.
    private sealed class LocatedTag(Tag opening, LocatedTag? parent)
    {
        public Tag Opening { get; } = opening;
        public LocatedTag? Parent { get; set; } = parent;
        public List<LocatedTag> SubTags { get; } = [];
        public RichTagContent? Content { get; set; }
        public int BodyEnd { get; set; } = opening.End;
        public int End { get; set; } = opening.End;
        public bool Complete { get; set; }
    }

    // One implementation of first-level structural matching is used both for
    // normal recursive rendering and for root escape command discovery. The
    // optional filter acts ONLY on returned roots, never on stack boundaries.
    private IReadOnlyList<LocatedTag> FindRootTags(int start, int end,
        Func<LocatedTag, bool>? tagsSearch = null)
    {
        var candidates = new List<LocatedTag>();
        var open = new Stack<LocatedTag>();
        var count = 0;
        for (var cursor = start; cursor < end && _failure is null;)
        {
            var at = _source.IndexOf('<', cursor);
            if (at < 0 || at >= end) break;
            if (!TryTag(at, end, out var tag))
            {
                cursor = at + 1;
                continue;
            }
            cursor = tag.End;
            if (++count > MaxTagTokens)
            {
                _failure = new ParseFailure("Tag token count exceeds limit.", Limit: true);
                break;
            }
            if (tag.Closing)
            {
                // A closer inside a different open wrapper is just text. This
                // also protects a parent closer inside an unknown child.
                if (open.Count > 0 && open.Peek().Opening.Name == tag.Name)
                {
                    var complete = open.Pop();
                    complete.BodyEnd = tag.Start;
                    complete.End = tag.End;
                    complete.Complete = true;
                    candidates.Add(complete);
                }
                continue;
            }
            _catalog.TryGetStructural(tag.Name, out var definition);
            var located = new LocatedTag(tag, open.Count > 0 ? open.Peek() : null);
            if (tag.SelfClosing || definition?.Role == RichTagRole.Marker)
            {
                located.Complete = true;
                candidates.Add(located);
                continue;
            }
            // Command and raw-editor bodies are opaque. A displayed delimiter
            // inside their contents must never close an ancestor. Leaf controls
            // with closing syntax are located for the existing diagnostic path.
            if (definition?.Role is RichTagRole.Command or RichTagRole.RawEditor or RichTagRole.Leaf)
            {
                var close = _source.IndexOf("</" + tag.Name + ">", tag.End,
                    StringComparison.Ordinal);
                if (close >= 0 && close < end)
                {
                    located.BodyEnd = close;
                    located.End = close + tag.Name.Length + 3;
                    located.Complete = true;
                    candidates.Add(located);
                    cursor = located.End;
                }
                else if (definition.Role == RichTagRole.Leaf)
                {
                    located.Complete = true; // Unsupported non-self-closing leaf
                    candidates.Add(located);
                }
                // Unmatched command/editor openers remain ordinary source.
                continue;
            }
            if (open.Count >= MaxDepth)
            {
                _failure = new ParseFailure("Document nesting exceeds limit.", Limit: true);
                break;
            }
            open.Push(located);
        }
        if (_failure is not null) return [];
        // Unmatched openers are literal, rather than implicitly owning child
        // commands. Promote their complete descendants to the nearest complete
        // ancestor, preserving the pre-existing incomplete-opener behavior.
        var roots = new List<LocatedTag>();
        foreach (var located in candidates)
        {
            var parent = located.Parent;
            while (parent is not null && !parent.Complete) parent = parent.Parent;
            located.Parent = parent;
            if (parent is null) roots.Add(located);
            else parent.SubTags.Add(located);
        }
        static void Order(List<LocatedTag> tags)
        {
            tags.Sort((left, right) => left.Opening.Start.CompareTo(right.Opening.Start));
            foreach (var tag in tags) Order(tag.SubTags);
        }
        Order(roots);
        return tagsSearch is null ? roots : roots.Where(tagsSearch).ToArray();
    }

    private IReadOnlyList<RichNode> RenderChildren(int start, int end,
        IReadOnlyList<LocatedTag> tags, int depth,
        RichTagDefinition? parentDefinition = null, bool inlineScope = false)
    {
        if (depth > MaxDepth)
        {
            _failure = new ParseFailure("Document nesting exceeds limit.", Limit: true);
            return [];
        }
        var result = new List<RichNode>();
        var cursor = start;
        var plainFrom = start;
        var inline = new List<RichNode>();
        bool Flush(int until)
        {
            if (until > plainFrom || inline.Count > 0)
                AppendMarkdown(result, plainFrom, until, parentDefinition, inlineScope, inline,
                    insideRichContainer: depth > 0);
            if (_failure is not null) return false;
            inline.Clear();
            plainFrom = until;
            return true;
        }
        // Radio grammar is intentionally the only non-tag discovery in ordinary
        // source. It cannot change structural parentage or command authority.
        bool PlainUntil(int until)
        {
            while (cursor < until)
            {
                if (parentDefinition?.LiteralChildren != true && _source[cursor] == '{' && !IsEscaped(cursor) &&
                    RichQuestionGrammar.TryParse(_source, cursor, end,
                        _radioLiteral, out var question, out var questionEnd))
                {
                    if (!Flush(cursor)) return false;
                    Add(result, question);
                    cursor = questionEnd;
                    plainFrom = cursor;
                    if (_failure is not null) return false;
                }
                else cursor++;
            }
            return true;
        }
        foreach (var located in tags)
        {
            // The finite radio-question grammar owns its complete span, even
            // when that span includes otherwise structural presentation tags.
            // Never process or collect a tag already consumed by that grammar.
            if (located.Opening.Start < cursor) continue;
            var tag = located.Opening;
            if (!PlainUntil(tag.Start)) return [];
            if (tag.Start < cursor) continue;
            if (_failure is not null) return [];
            // Root-only host commands are inert inside *all* containers,
            // irrespective of the parent's ordinary UI child policy.
            if (located.Parent is not null &&
                _catalog.TryGetStructural(tag.Name, out var structural) &&
                !structural.AllowAsChild)
            {
                cursor = located.End;
                continue;
            }
            if (!_catalog.TryGet(tag.Name, parentDefinition, out var definition))
            {
                if (parentDefinition?.SupportedChildren is { Count: > 0 })
                {
                    _failure = new ParseFailure($"Unsupported child: {tag.Name}.");
                    return [];
                }
                if (!Flush(tag.Start)) return [];
                AddUnknown(result, located, "Unsupported control", depth);
                cursor = located.End;
                plainFrom = cursor;
                continue;
            }
            var from = tag.Start;
            var inlineElement = parentDefinition?.LiteralChildren != true && definition.Inline;
            if (definition.Role == RichTagRole.Leaf && !tag.SelfClosing)
            {
                if (parentDefinition?.SupportedChildren is { Count: > 0 })
                {
                    _failure = new ParseFailure("Invalid leaf in strict content.");
                    return [];
                }
                if (!Flush(from)) return [];
                AddUnknown(result, located, "Leaf requires self-closing syntax", depth,
                    opaque: located.End > tag.End);
                cursor = located.End;
                plainFrom = cursor;
                continue;
            }
            if (!tag.SelfClosing && definition.Role == RichTagRole.RawEditor &&
                located.End == tag.End)
            {
                // Kept as a defensive boundary if a future tag role is added.
                cursor = tag.End;
                continue;
            }
            var validAttributes = TryParseAttributes(tag, definition,
                out var attributes, out var reason);
            if (validAttributes && definition.Validate is { } validator &&
                !validator(definition, attributes))
            {
                validAttributes = false;
                reason = $"Unsafe attributes or nesting: {tag.Name}.";
            }
            if (!validAttributes)
            {
                if (parentDefinition?.SupportedChildren is { Count: > 0 } || !definition.AllowAsChild)
                {
                    _failure = new ParseFailure(reason);
                    return [];
                }
                if (!Flush(from)) return [];
                AddUnknown(result, located, reason, depth,
                    opaque: definition.SupportedChildren is { Count: 0 });
                cursor = located.End;
                plainFrom = cursor;
                continue;
            }
            // One dispatch point for every eligible registered tag. The
            // registry, not presentation roles, decides whether work occurs.
            var commandStart = _commands.Count;
            DispatchTag(located, definition, attributes,
                isDocumentRoot: located.Parent is null, depth: depth);
            if (_failure is not null) return [];
            if (!definition.AllowAsChild)
            {
                cursor = located.End; // Registered host-only tag, never UI
                continue;
            }
            if (!inlineElement && !Flush(from)) return [];
            cursor = tag.End;
            if (definition.Role is RichTagRole.OpaqueLiteral or RichTagRole.RawEditor)
            {
                if (tag.SelfClosing)
                {
                    _failure = new ParseFailure($"{tag.Name} requires a closing delimiter.");
                    return [];
                }
                var text = LiteralExcludingCommands(tag.End, located.BodyEnd, commandStart);
                Add(result, new RichNode(definition.Role == RichTagRole.OpaqueLiteral ? "literal" : "raw",
                    $"{from}:raw", new RichSourceSpan(from, located.End),
                    Text: text, Name: tag.Name, Attributes: attributes));
                cursor = located.End;
            }
            else if (definition.Role == RichTagRole.Leaf || tag.SelfClosing)
            {
                var element = new RichNode("element", $"{from}:element",
                    new RichSourceSpan(from, cursor), Name: tag.Name, Attributes: attributes);
                if (inlineElement) inline.Add(element);
                else Add(result, element);
            }
            else if (definition.SupportedChildren is { Count: 0 })
            {
                var literal = new RichNode("literal", $"{tag.End}:literal",
                    new RichSourceSpan(tag.End, located.BodyEnd),
                    Text: _source[tag.End..located.BodyEnd]);
                var element = new RichNode("element", $"{from}:element",
                    new RichSourceSpan(from, located.End), Name: tag.Name,
                    Attributes: attributes, Children: [literal]);
                if (inlineElement) inline.Add(element);
                else Add(result, element);
                cursor = located.End;
            }
            else
            {
                var children = RenderChildren(tag.End, located.BodyEnd,
                    located.SubTags, depth + 1, definition, inlineElement);
                if (_failure is { } childFailure)
                {
                    if (parentDefinition?.SupportedChildren is { Count: > 0 } || childFailure.Limit) return [];
                    _failure = null;
                    if (inlineElement && !Flush(from)) return [];
                    AddUnknown(result, located, childFailure.Reason, depth, opaque: true);
                    cursor = located.End;
                    plainFrom = cursor;
                    continue;
                }
                var element = new RichNode("element", $"{from}:element",
                    new RichSourceSpan(from, located.End), Name: tag.Name,
                    Attributes: attributes, Children: children);
                if (inlineElement) inline.Add(element);
                else Add(result, element);
                cursor = located.End;
            }
            if (!inlineElement) plainFrom = cursor;
        }
        if (!PlainUntil(end) || !Flush(end)) return [];
        return result;
    }

    // A single registry-driven dispatcher builds validated, source-addressed
    // handler content. It runs in rendering source order; handlers cannot execute
    // host actions and are not serialized to the frontend.
    private void DispatchTag(LocatedTag located, RichTagDefinition definition,
        IReadOnlyDictionary<string, string> attributes, bool isDocumentRoot,
        RichTagContent? parent = null, int depth = 0)
    {
        var tag = located.Opening;
        var content = new RichTagContent(definition, attributes,
            _source[tag.End..located.BodyEnd],
            new RichSourceSpan(tag.Start, located.End),
            new RichSourceSpan(tag.End, located.BodyEnd),
            parent ?? located.Parent?.Content, isDocumentRoot, tag.SelfClosing, depth);
        located.Content = content;
        definition.ProcessContent?.Invoke(this, content);
    }

    // Handler callbacks collect only candidates. Admission and actual host
    // execution remain exclusively the Agent Interpreter/Registry's work.
    internal void RejectTag(string reason, bool limit = false) =>
        _failure = new ParseFailure(reason, Limit: limit);

    internal void AddCommandCandidate(string name, string? body, RichSourceSpan span)
    {
        if (_commands.Count >= MaxNodes)
        {
            _failure = new ParseFailure("Command count exceeds limit.", Limit: true);
            return;
        }
        _commands.Add(new RichCommandCandidate(name, body, span));
    }

    // Reuse the existing structural finder for handlers that explicitly ask
    // for registered direct root-only children. No command names are embedded.
    internal void DispatchDirectHandlers(RichTagContent parent)
    {
        var direct = FindRootTags(parent.BodySpan.Start, parent.BodySpan.End,
            located => _catalog.TryGet(located.Opening.Name, out var definition) &&
                !definition.AllowAsChild && definition.ProcessContent is not null);
        foreach (var child in direct)
        {
            if (_failure is not null) return;
            _catalog.TryGet(child.Opening.Name, out var definition);
            if (!TryParseAttributes(child.Opening, definition, out var parameters, out var reason))
            {
                _failure = new ParseFailure(reason);
                return;
            }
            DispatchTag(child, definition, parameters, isDocumentRoot: false, parent: parent,
                depth: parent.Depth + 1);
        }
    }

    // Project muted/raw text from the same collected source spans. Opaque
    // children never carry a second command-discovery scanner.
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

    private void AddUnknown(List<RichNode> nodes, LocatedTag located,
        string reason, int depth, bool opaque = false)
    {
        var tag = located.Opening;
        IReadOnlyList<RichNode>? children = null;
        if (!tag.SelfClosing && located.End > tag.End)
        {
            if (opaque)
                children = [new RichNode("literal", $"{tag.End}:literal",
                    new RichSourceSpan(tag.End, located.BodyEnd),
                    Text: _source[tag.End..located.BodyEnd])];
            else
                children = RenderChildren(tag.End, located.BodyEnd, located.SubTags, depth + 1);
        }
        if (_failure is not null) return;
        Add(nodes, new RichNode("unknown", $"{tag.Start}:unknown",
            new RichSourceSpan(tag.Start, located.End), Name: tag.Name,
            Attributes: new Dictionary<string, string> { ["reason"] = reason },
            Children: children));
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

    private void AppendMarkdown(List<RichNode> nodes, int start, int end,
        RichTagDefinition? parentDefinition, bool inlineScope,
        IReadOnlyList<RichNode> inline, bool insideRichContainer)
    {
        if (end <= start) return;
        if (parentDefinition?.LiteralChildren == true)
        {
            var raw = _source[start..end];
            parentDefinition.ProcessLiteral?.Invoke(this, raw);
            if (_failure is not null) return;
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
