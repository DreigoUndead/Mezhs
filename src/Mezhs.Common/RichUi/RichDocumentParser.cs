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
    // Only structural layout scopes may contain Agent command nodes.
    // Display-only tags (text, badge, title, caption, Link, etc.) never grant authority.
    private static readonly IReadOnlySet<string> ExecutableContainers =
        new HashSet<string>(["box", "row", "col", "grid", "grid-item"], StringComparer.Ordinal);
    // Keep the syntax used for literal masking aligned with typed Markdown lowering.
    // AdvancedExtensions includes GenericAttributes, which swallows literal
    // "{item.property}" model text as Markdown metadata. It is intentionally off.
    private readonly MarkdownPipeline _markdown = new MarkdownPipelineBuilder().UsePipeTables().UseEmphasisExtras().Build();
    private string _source = "";
    private bool[] _literal = [];
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
            // Markdown establishes code/escaped/comment semantics. Rich tags
            // are scanned only outside protected source ranges.
            _literal = MarkdownLiteralMask(source);
            var nodes = ReadChildren(0, source.Length, null, 0, out _);
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

    private static RichParseResult Failed(string source, string reason) => new(
        new RichDocument(RichDocument.CurrentVersion, false,
            [new RichNode("literal", "0:literal", new RichSourceSpan(0, source.Length), Text: source)],
            [new RichDiagnostic(reason, new RichSourceSpan(0, source.Length))]),
        []);

    private bool[] MarkdownLiteralMask(string source)
    {
        var mask = new bool[source.Length];
        var document = Markdown.Parse(source, _markdown);
        void Visit(MarkdownObject node)
        {
            if (node is CodeBlock || node is CodeInline || IsComment(node, source))
            {
                var from = Math.Clamp(node.Span.Start, 0, source.Length);
                var to = Math.Clamp(node.Span.End + 1, from, source.Length);
                for (var i = from; i < to; i++) mask[i] = true;
                return;
            }
            if (node is ContainerBlock block)
                foreach (var child in block) Visit(child);
            else if (node is LeafBlock leaf && leaf.Inline is not null)
                Visit(leaf.Inline);
            else if (node is ContainerInline inline)
                foreach (var child in inline) Visit(child);
        }
        Visit(document);
        // HTML comments not always become isolated AST nodes inside HTML
        // blocks. Recognize them as opaque only outside Markdown code.
        for (var i = 0; i < source.Length - 3; i++)
        {
            if (mask[i] || !source.AsSpan(i).StartsWith("<!--", StringComparison.Ordinal)) continue;
            var end = source.IndexOf("-->", i + 4, StringComparison.Ordinal);
            var until = end < 0 ? source.Length : end + 3;
            for (var j = i; j < until; j++) mask[j] = true;
            i = until - 1;
        }
        // Markdig masks completed code spans. Conservatively mask a dangling
        // backtick run through EOF: unmatched Markdown literals must not grant
        // command authority to later command-shaped text. One linear pass.
        var unmatchedAt = -1;
        var unmatchedWidth = 0;
        for (var i = 0; i < source.Length;)
        {
            if (mask[i] || source[i] != (char)96) { i++; continue; }
            var until = i;
            while (until < source.Length && !mask[until] && source[until] == (char)96)
                until++;
            var width = until - i;
            if (unmatchedAt == -1) { unmatchedAt = i; unmatchedWidth = width; }
            else if (width == unmatchedWidth)
            {
                for (var j = unmatchedAt; j < until; j++) mask[j] = true;
                unmatchedAt = -1;
                unmatchedWidth = 0;
            }
            i = until;
        }
        if (unmatchedAt >= 0)
            for (var i = unmatchedAt; i < source.Length; i++) mask[i] = true;
        return mask;
    }

    private static bool IsComment(MarkdownObject node, string source)
    {
        if (node is not HtmlInline && node is not HtmlBlock) return false;
        var start = node.Span.Start;
        return start >= 0 && start < source.Length &&
            source.AsSpan(start).StartsWith("<!--", StringComparison.Ordinal);
    }

    private IReadOnlyList<RichNode> ReadChildren(int start, int end, string? expected,
        int depth, out int position, bool svgScope = false, bool commandsAllowed = true)
    {
        if (depth > MaxDepth) throw new RichDocumentException("Document nesting exceeds limit.");
        var result = new List<RichNode>();
        var cursor = start;
        var plainFrom = start;
        while (cursor < end)
        {
            if (!svgScope && commandsAllowed && _source[cursor] == '{' && !_literal[cursor] && !IsEscaped(cursor) &&
                RichQuestionGrammar.TryParse(_source, cursor, end, _literal, out var question, out var questionEnd))
            {
                AppendMarkdown(result, plainFrom, cursor, svgScope);
                Add(result, question);
                cursor = questionEnd;
                plainFrom = cursor;
                continue;
            }
            if (_source[cursor] != '<' || (!svgScope && _literal[cursor]) || IsEscaped(cursor) ||
                !TryTag(cursor, end, out var tag))
            {
                cursor++;
                continue;
            }
            AppendMarkdown(result, plainFrom, cursor, svgScope);
            if (tag.Closing)
            {
                if (expected != tag.Name)
                    throw new RichDocumentException($"Unexpected closing tag: {tag.Name}.");
                position = tag.End;
                return result;
            }
            if (!RichTagRegistry.TryGet(tag.Name, svgScope, out var definition))
                throw new RichDocumentException($"Unsupported tag: {tag.Name}.");
            if ((definition.Role is RichTagRole.Command or RichTagRole.Marker) &&
                (!commandsAllowed || svgScope))
                throw new RichDocumentException("Agent protocol commands are forbidden inside display-only elements.");
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

            var from = cursor;
            cursor = tag.End;
            if (definition.Role is RichTagRole.OpaqueLiteral or RichTagRole.RawEditor or RichTagRole.Command)
            {
                if (tag.SelfClosing)
                    throw new RichDocumentException($"{tag.Name} requires a closing delimiter.");
                if (definition.Role != RichTagRole.OpaqueLiteral && !Standalone(from, tag.End))
                    throw new RichDocumentException($"{tag.Name} must begin on its own line.");
                var closer = FindOpaqueClosing(tag.Name, tag.End, end,
                    definition.Role == RichTagRole.OpaqueLiteral, tag.Name == "WritingBlock");
                if (closer < 0)
                    throw new RichDocumentException($"Unclosed {tag.Name} block.");
                var closeEnd = closer + tag.Name.Length + 3;
                var bodyStart = tag.End;
                var bodyEnd = closer;
                if (definition.Role != RichTagRole.OpaqueLiteral)
                {
                    bodyStart = SkipLineBreak(bodyStart);
                    bodyEnd = TrimOneLineBreak(bodyEnd);
                }
                if (bodyEnd < bodyStart) bodyEnd = bodyStart;
                var body = _source[bodyStart..bodyEnd];
                if (definition.Role == RichTagRole.Command)
                    _commands.Add(new RichCommandCandidate(tag.Name, body, new RichSourceSpan(from, closeEnd)));
                else
                    Add(result, new RichNode(definition.Role == RichTagRole.OpaqueLiteral ? "literal" : "raw",
                        $"{from}:raw", new RichSourceSpan(from, closeEnd), Text: body, Name: tag.Name,
                        Attributes: attributes));
                cursor = closeEnd;
            }
            else if (definition.Role == RichTagRole.Marker)
            {
                if (tag.SelfClosing || attributes.Count != 0 || !Standalone(from, tag.End))
                    throw new RichDocumentException("DONE is a bodyless standalone marker.");
                _commands.Add(new RichCommandCandidate(tag.Name, null, new RichSourceSpan(from, cursor)));
            }
            else if (definition.Role == RichTagRole.Leaf || tag.SelfClosing)
            {
                if (definition.Role == RichTagRole.Leaf && !tag.SelfClosing)
                    throw new RichDocumentException($"{tag.Name} must be self-closing.");
                Add(result, new RichNode("element", $"{from}:element", new RichSourceSpan(from, cursor),
                    Name: tag.Name, Attributes: attributes));
            }
            else
            {
                // Markdown scopes must be reparsed to protect indented/list code
                // examples from Agent command discovery. SVG, however, has its
                // own validated element grammar and no command authority at all:
                // Markdig would mistake four-space SVG geometry for code blocks.
                if (!svgScope && tag.Name != "svg")
                {
                    var scoped = MarkdownLiteralMask(_source[cursor..end]);
                    for (var i = 0; i < scoped.Length; i++)
                        if (scoped[i]) _literal[cursor + i] = true;
                }
                var children = ReadChildren(cursor, end, tag.Name, depth + 1, out var after,
                    svgScope || tag.Name == "svg",
                    commandsAllowed && ExecutableContainers.Contains(tag.Name));
                Add(result, new RichNode("element", $"{from}:element", new RichSourceSpan(from, after),
                    Name: tag.Name, Attributes: attributes, Children: children));
                cursor = after;
            }
            plainFrom = cursor;
        }
        if (expected is not null)
            throw new RichDocumentException($"Unclosed {expected} container.");
        AppendMarkdown(result, plainFrom, end, svgScope);
        position = end;
        return result;
    }

    private void Add(List<RichNode> nodes, RichNode node)
    {
        if (++_count > MaxNodes) throw new RichDocumentException("Document node limit exceeded.");
        nodes.Add(node);
    }

    private void AppendMarkdown(List<RichNode> nodes, int start, int end, bool svgScope)
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
        foreach (var node in RichMarkdownNodes.Convert(_source[start..end], start))
        {
            _count += CountNodes(node);
            if (_count > MaxNodes) throw new RichDocumentException("Document node limit exceeded.");
            nodes.Add(node);
        }
    }

    private static int CountNodes(RichNode node) =>
        1 + (node.Children?.Sum(CountNodes) ?? 0);

    private bool IsEscaped(int index)
    {
        var backslashes = 0;
        for (var i = index - 1; i >= 0 && _source[i] == '\\'; i--) backslashes++;
        return (backslashes & 1) == 1;
    }

    private bool Standalone(int start, int end)
    {
        var lineStart = _source.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        var lineEnd = _source.IndexOf('\n', end);
        if (lineEnd < 0) lineEnd = _source.Length;
        // Delimiters may have at most three leading spaces; tabs are never valid.
        // Markdown code indentation cannot terminate a raw editor.
        var prefix = _source.AsSpan(lineStart, start - lineStart);
        return prefix.Length <= 3 && prefix.IndexOfAnyExcept((char)32) < 0 &&
               string.IsNullOrWhiteSpace(_source[end..lineEnd]);
    }

    private int SkipLineBreak(int start)
    {
        if (start < _source.Length && _source[start] == '\r') start++;
        if (start < _source.Length && _source[start] == '\n') start++;
        return start;
    }

    private int TrimOneLineBreak(int end)
    {
        if (end > 0 && _source[end - 1] == '\n') end--;
        if (end > 0 && _source[end - 1] == '\r') end--;
        return end;
    }

    private int FindOpaqueClosing(string name, int from, int end, bool inline, bool markdownAware)
    {
        var closer = "</" + name + ">";
        // WritingBlock is an editor of literal Markdown; delimiter-like examples
        // inside its Markdown code remain part of the draft, not a closing tag.
        bool[]? rawMask = markdownAware ? MarkdownLiteralMask(_source[from..end]) : null;
        var pos = from;
        while ((pos = _source.IndexOf(closer, pos, StringComparison.Ordinal)) >= 0 && pos < end)
        {
            // An opaque body is not recursively scanned. Commands and raw
            // editors require a standalone closer; escape may close inline.
            if ((rawMask is null || !rawMask[pos - from]) &&
                (inline || Standalone(pos, pos + closer.Length)))
                return pos;
            pos += closer.Length;
        }
        return -1;
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
                throw new RichDocumentException("Invalid closing tag.");
            tag = new Tag(name, closing, selfClosing, from, i + 1, attributes);
            return true;
        }
        throw new RichDocumentException("Unclosed or oversized tag opening.");
    }

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
            if (!definition.AllowedAttributes.Contains(name) || result.ContainsKey(name))
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
