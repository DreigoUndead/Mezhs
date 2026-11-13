using System.Text;
using System.Text.RegularExpressions;

namespace Mezhs.Common.RichUi;

// A finite compatibility grammar. It turns a radio question into inert option
// data and precomputed send strings. Model-authored JavaScript is never run.
internal static class RichQuestionGrammar
{
    private const int MaxQuestion = 8_192;
    private const int MaxOptions = 12;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly Regex Declaration = new(
        @"^\{@body\s+const\s*\[\s*(?<state>[A-Za-z_$][\w$]*)\s*,\s*(?<setter>[A-Za-z_$][\w$]*)\s*\]\s*=\s*DIL\.useState\(\s*""(?<initial>[A-Za-z0-9_-]{1,64})""\s*\)\s*\}$",
        RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex RadioOpening = new(
        "^<radio\\s+value=\"(?<value>[A-Za-z0-9_-]{1,64})\"\\s*>$",
        RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex AllowedLiteralTag = new(
        @"^</?text(?:\s+[a-zA-Z][a-zA-Z0-9]*\s*=\s*(?:""[a-zA-Z0-9_-]{1,64}""|'[a-zA-Z0-9_-]{1,64}'))*\s*>$|^<icon(?:\s+[a-zA-Z][a-zA-Z0-9]*\s*(?:=\s*(?:""[a-zA-Z0-9_-]{1,64}""|'[a-zA-Z0-9_-]{1,64}'))?)*\s*/>$",
        RegexOptions.CultureInvariant, RegexTimeout);

    public static bool TryParse(string source, int from, int end, bool[] literal,
        out RichNode node, out int after)
    {
        node = null!;
        after = from;
        if (from < 0 || from >= end || literal[from] ||
            !source.AsSpan(from).StartsWith("{@body", StringComparison.Ordinal)) return false;
        if (!StandalonePrefix(source, from)) return false;
        var limit = Math.Min(end, from + MaxQuestion);
        var lineEnd = source.IndexOf('\n', from, Math.Min(480, limit - from));
        if (lineEnd < 0) return false;
        var decl = Declaration.Match(source[from..lineEnd].TrimEnd('\r', ' ', '\t'));
        if (!decl.Success) return false;
        var state = decl.Groups["state"].Value;
        var setter = decl.Groups["setter"].Value;
        var initial = decl.Groups["initial"].Value;
        if (state == setter || state is "constructor" or "prototype" or "__proto__" or "globalThis") return false;
        var at = SkipSpace(source, lineEnd + 1, limit);
        // The previously supported one-box radio question is a finite
        // presentation wrapper around the same restricted question grammar.
        // Validate its attributes with the canonical parser, not a second
        // attribute grammar or model-authored expression evaluator.
        IReadOnlyDictionary<string, string>? boxAttributes = null;
        if (Prefix(source, at, "<box") && at + 4 < limit &&
            char.IsWhiteSpace(source[at + 4]))
        {
            var boxEnd = HeaderEnd(source, at, limit, 512);
            if (boxEnd < 0) return false;
            var wrapperSource = source[at..boxEnd] + "</box>";
            var verified = new RichDocumentParser().Parse(wrapperSource);
            var parsedBox = verified.Document.Nodes.Count == 1
                ? verified.Document.Nodes[0] : null;
            if (!verified.Document.Valid || verified.Commands.Count != 0 ||
                parsedBox?.Kind != "element" || parsedBox.Name != "box" ||
                parsedBox.Attributes is null) return false;
            boxAttributes = parsedBox.Attributes;
            at = SkipSpace(source, boxEnd, limit);
        }
        if (!Prefix(source, at, "<radio-group") || literal[at]) return false;
        var groupEnd = HeaderEnd(source, at, limit, 512);
        if (groupEnd < 0) return false;
        var groupHeader = source[at..groupEnd];
        var pattern = "^<radio-group(?:\\s+direction=\"(?:col|row)\")?\\s+value=\\{" +
            Regex.Escape(state) + "\\}\\s+onChange=\\{" + Regex.Escape(setter) + "\\}\\s*>$";
        if (!Regex.IsMatch(groupHeader, pattern, RegexOptions.CultureInvariant, RegexTimeout)) return false;
        at = groupEnd;
        var options = new List<RichNode>();
        var values = new HashSet<string>(StringComparer.Ordinal);
        while (options.Count < MaxOptions)
        {
            at = SkipSpace(source, at, limit);
            if (Prefix(source, at, "</radio-group>")) break;
            if (!Prefix(source, at, "<radio") || literal[at]) return false;
            var openEnd = HeaderEnd(source, at, limit, 256);
            if (openEnd < 0) return false;
            var match = RadioOpening.Match(source[at..openEnd]);
            if (!match.Success) return false;
            var value = match.Groups["value"].Value;
            if (!values.Add(value)) return false;
            var close = source.IndexOf("</radio>", openEnd, StringComparison.Ordinal);
            if (close < 0 || close >= limit || close - openEnd > 600) return false;
            if (!Content(source, openEnd, close, out var display)) return false;
            options.Add(new RichNode("questionOption", $"{at}:questionOption",
                new RichSourceSpan(at, close + 8),
                Attributes: new Dictionary<string, string> { ["value"] = value }, Children: display));
            at = close + 8;
        }
        if (options.Count < 2 || !values.Contains(initial)) return false;
        if (!Prefix(source, at, "</radio-group>")) return false;
        at = SkipSpace(source, at + "</radio-group>".Length, limit);
        if (!Prefix(source, at, "<button") || literal[at]) return false;
        var buttonStart = at;
        var buttonEnd = HeaderEnd(source, at, limit, 3_200);
        if (buttonEnd < 0) return false;
        var buttonHeader = source[at..buttonEnd];
        var button = Regex.Match(buttonHeader,
            @"^<button(?<block>\s+block)?\s+onClick=\{(?<action>[\s\S]*)\}\s*>$",
            RegexOptions.CultureInvariant, RegexTimeout);
        if (!button.Success) return false;
        if (!Messages(button.Groups["action"].Value, state,
            options.Select(o => o.Attributes!["value"]).ToArray(), out var messages)) return false;
        var buttonClose = source.IndexOf("</button>", buttonEnd, StringComparison.Ordinal);
        if (buttonClose < 0 || buttonClose >= limit || buttonClose - buttonEnd > 400) return false;
        if (!Content(source, buttonEnd, buttonClose, out var buttonDisplay)) return false;
        for (var i = 0; i < options.Count; i++)
        {
            var option = options[i];
            var attrs = new Dictionary<string, string>(option.Attributes!, StringComparer.Ordinal)
            {
                ["message"] = messages[i]
            };
            options[i] = option with { Attributes = attrs };
        }
        options.Add(new RichNode("questionButton", $"{buttonStart}:questionButton",
            new RichSourceSpan(buttonStart, buttonClose + 9),
            Attributes: new Dictionary<string, string> { ["block"] = button.Groups["block"].Success ? "true" : "false" },
            Children: buttonDisplay));
        after = buttonClose + 9;
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["initial"] = initial
        };
        if (boxAttributes is not null)
        {
            var closeAt = SkipSpace(source, after, limit);
            if (!Prefix(source, closeAt, "</box>") || literal[closeAt]) return false;
            after = closeAt + "</box>".Length;
            attributes["box"] = "true";
            foreach (var (key, value) in boxAttributes)
                attributes["box." + key] = value;
        }
        node = new RichNode("question", $"{from}:question", new RichSourceSpan(from, after),
            Attributes: attributes, Children: options);
        return true;
    }

    private static bool Content(string source, int from, int end, out IReadOnlyList<RichNode> nodes)
    {
        nodes = [];
        var value = source[from..end];
        if (string.IsNullOrWhiteSpace(value)) return false;
        // Only the two known presentation tags may appear within labels.
        for (var at = 0; at < value.Length; at++)
        {
            if (value[at] != '<') continue;
            var close = value.IndexOf('>', at + 1);
            if (close < 0 || close - at > 256 ||
                !AllowedLiteralTag.IsMatch(value[at..(close + 1)])) return false;
            at = close;
        }
        // Radio bodies are indented relative to <radio>, but four leading
        // spaces mean an indented Markdown code block. Remove only the common
        // visual indent before canonical parsing, retaining original source
        // offsets for every surviving UTF-16 code unit.
        var (normalized, positions) = NormalizeLabel(value, from);
        var parsed = new RichDocumentParser().Parse(normalized);
        if (!parsed.Document.Valid || parsed.Commands.Count != 0) return false;
        bool Allowed(RichNode n) =>
            (n.Kind != "element" || n.Name is "text" or "icon") &&
            n.Kind is not ("question" or "raw") &&
            (n.Children is null || n.Children.All(Allowed));
        if (!parsed.Document.Nodes.All(Allowed)) return false;
        nodes = parsed.Document.Nodes.Select(n => Shift(n, positions)).ToArray();
        return true;
    }

    private static (string Text, int[] Positions) NormalizeLabel(string value, int offset)
    {
        var minIndent = int.MaxValue;
        for (var start = 0; start < value.Length;)
        {
            var next = value.IndexOf('\n', start);
            var finish = next < 0 ? value.Length : next;
            if (!string.IsNullOrWhiteSpace(value[start..finish]))
            {
                var spaces = 0;
                while (start + spaces < finish && value[start + spaces] == ' ') spaces++;
                minIndent = Math.Min(minIndent, spaces);
            }
            start = next < 0 ? value.Length : next + 1;
        }
        if (minIndent == int.MaxValue) minIndent = 0;
        var text = new StringBuilder(value.Length);
        var positions = new List<int>(value.Length + 1);
        for (var start = 0; start < value.Length;)
        {
            var next = value.IndexOf('\n', start);
            var finish = next < 0 ? value.Length : next + 1;
            var skip = 0;
            while (start + skip < finish && skip < minIndent && value[start + skip] == ' ') skip++;
            for (var i = start + skip; i < finish; i++)
            {
                text.Append(value[i]);
                positions.Add(offset + i);
            }
            start = finish;
        }
        positions.Add(offset + value.Length);
        return (text.ToString(), positions.ToArray());
    }

    private static RichNode Shift(RichNode n, int[] positions)
    {
        var start = positions[Math.Clamp(n.SourceSpan.Start, 0, positions.Length - 1)];
        var end = n.SourceSpan.End > n.SourceSpan.Start
            ? positions[Math.Clamp(n.SourceSpan.End - 1, 0, positions.Length - 2)] + 1
            : start;
        return n with
        {
            Id = $"{start}:{n.Kind}",
            SourceSpan = new RichSourceSpan(start, end),
            Children = n.Children?.Select(c => Shift(c, positions)).ToArray()
        };
    }

    // Accept only the original single-state literal template or bounded ternary. No model JS runs.
    // The final option is its default; all other branches are strict equality tests.
    private static bool Messages(string action, string state, string[] values, out string[] messages)
    {
        messages = [];
        // A single interpolation of the declared state is finite data, not JS.
        // Reject escapes and any other interpolation or dynamic expression.
        var template = Regex.Match(action,
            @"^\s*\(\s*\)\s*=>\s*GenUI\.issueNewTurn\(\s*\x60(?<prefix>[^\x60\\$\r\n]*)\$\{(?<state>[A-Za-z_$][\w$]*)\}(?<suffix>[^\x60\\$\r\n]*)\x60\s*\)\s*$",
            RegexOptions.CultureInvariant, RegexTimeout);
        if (template.Success && template.Groups["state"].Value == state)
        {
            var prefix = template.Groups["prefix"].Value;
            var suffix = template.Groups["suffix"].Value;
            var interpolated = values.Select(value => prefix + value + suffix).ToArray();
            if (!interpolated.All(message => message.Length is >= 1 and <= 2_000))
                return false;
            messages = interpolated;
            return true;
        }
        var cursor = new Cursor(action);
        if (!cursor.Take("()=>GenUI.issueNewTurn(")) return false;
        var result = new List<string>();
        for (var i = 0; i < values.Length - 1; i++)
        {
            if (!cursor.Take(state) || !cursor.Take("===") ||
                !cursor.String(out var selected) || selected != values[i] ||
                !cursor.Take("?") || !cursor.String(out var message) ||
                message.Length is < 1 or > 2_000 || !cursor.Take(":")) return false;
            result.Add(message);
        }
        if (!cursor.String(out var fallback) || fallback.Length is < 1 or > 2_000 ||
            !cursor.Take(")") || !cursor.End) return false;
        result.Add(fallback);
        messages = result.ToArray();
        return true;
    }

    private sealed class Cursor(string source)
    {
        private int _at;
        private void Space() { while (_at < source.Length && char.IsWhiteSpace(source[_at])) _at++; }
        public bool End { get { Space(); return _at == source.Length; } }
        public bool Take(string expected)
        {
            Space();
            if (!source.AsSpan(_at).StartsWith(expected, StringComparison.Ordinal)) return false;
            _at += expected.Length;
            return true;
        }
        public bool String(out string value)
        {
            value = "";
            Space();
            if (_at >= source.Length || source[_at] is not ('\'' or '"')) return false;
            var quote = source[_at++];
            var b = new StringBuilder();
            while (_at < source.Length && b.Length <= 2_000)
            {
                var ch = source[_at++];
                if (ch == quote) { value = b.ToString(); return true; }
                if (ch == '\\')
                {
                    if (_at >= source.Length) return false;
                    ch = source[_at++] switch
                    {
                        'n' => '\n', 'r' => '\r', 't' => '\t', '\\' => '\\',
                        '\'' => '\'', '"' => '"', _ => '\0'
                    };
                    if (ch == '\0') return false;
                }
                else if (ch is '\n' or '\r') return false;
                b.Append(ch);
            }
            return false;
        }
    }

    private static bool StandalonePrefix(string source, int start) =>
        string.IsNullOrWhiteSpace(source[(source.LastIndexOf('\n', Math.Max(0, start - 1)) + 1)..start]);

    private static bool Prefix(string source, int at, string prefix) =>
        at >= 0 && at < source.Length && source.AsSpan(at).StartsWith(prefix, StringComparison.Ordinal);

    private static int SkipSpace(string source, int at, int limit)
    {
        while (at < limit && char.IsWhiteSpace(source[at])) at++;
        return at;
    }

    private static int HeaderEnd(string source, int from, int limit, int max)
    {
        char quote = '\0';
        var braces = 0;
        for (var i = from; i < limit && i - from < max; i++)
        {
            var ch = source[i];
            if (quote != '\0')
            {
                if (ch == '\\' && i + 1 < limit) { i++; continue; }
                if (ch == quote) quote = '\0';
                continue;
            }
            if (ch is '"' or '\'') { quote = ch; continue; }
            if (ch == '{') braces++;
            else if (ch == '}') { if (--braces < 0) return -1; }
            else if (ch == '>' && braces == 0) return i + 1;
            else if (ch is '\n' or '\r') return -1;
        }
        return -1;
    }
}
