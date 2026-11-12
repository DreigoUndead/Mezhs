namespace Mezhs.Agent.Commands;

public sealed record Command(string Name, string? Body);

public sealed record CommandBatch(
    IReadOnlyList<Command> Commands,
    string VisibleContent);

public sealed class CommandParseException(string message) : Exception(message);

public sealed class Parser
{
    public CommandBatch Parse(string content)
    {
        var lines = ReadLines(content);
        var commands = new List<Command>();
        var visible = new List<string>();
        var insideEscape = false;
        char fenceMarker = '\0';
        var fenceLength = 0;
        var insideComment = false;
        var inlineTicks = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i].Text.Trim();
            // Escaped presentation text is literal, including command-shaped examples.
            // Never promote escaped markup into executable Agent commands.
            if (insideEscape || text.Contains("<escape>", StringComparison.Ordinal) ||
                text.Contains("<escape ", StringComparison.Ordinal))
            {
                visible.Add(lines[i].Text);
                insideEscape = !text.Contains("</escape>", StringComparison.Ordinal);
                continue;
            }
            // A fenced/indented Markdown example is presentation, never an Agent command.
            if (fenceMarker != '\0')
            {
                visible.Add(lines[i].Text);
                if (IsClosingFence(lines[i].Text, fenceMarker, fenceLength))
                    fenceMarker = '\0';
                continue;
            }
            // Multiline comments and code spans in normal Markdown must also
            // remain presentation. This is the same tracker used for raw
            // WritingBlock closers, but has no effect on opaque command bodies.
            if (insideComment || inlineTicks != 0)
            {
                visible.Add(lines[i].Text);
                TrackInlineMarkdown(lines[i].Text, ref insideComment, ref inlineTicks);
                continue;
            }
            if (TryOpeningFence(lines[i].Text, out fenceMarker, out fenceLength))
            {
                visible.Add(lines[i].Text);
                continue;
            }
            if (lines[i].Text.StartsWith("    ", StringComparison.Ordinal) || lines[i].Text.StartsWith("\t", StringComparison.Ordinal))
            {
                visible.Add(lines[i].Text);
                continue;
            }

            // Raw presentation controls may contain literal <SH>/<DONE> examples.
            // Preserve their entire source without interpreting the enclosed text.
            if (TryRawPresentationOpening(text, out var presentationName))
            {
                var end = FindRawPresentationClosingTag(lines, i + 1, presentationName);
                var last = end < 0 ? lines.Count - 1 : end;
                for (var line = i; line <= last; line++)
                    visible.Add(lines[line].Text);
                i = last;
                continue;
            }
            TrackInlineMarkdown(lines[i].Text, ref insideComment, ref inlineTicks);
            if (insideComment || inlineTicks != 0)
            {
                visible.Add(lines[i].Text);
                continue;
            }
            if (!TryTag(text, out var name, out var closing))
            {
                visible.Add(lines[i].Text);
                continue;
            }
            if (closing)
                throw new CommandParseException($"Unexpected closing command </{name}>.");

            var closeIndex = FindClosingTag(lines, i + 1, name);
            if (closeIndex < 0)
            {
                commands.Add(new Command(name, null));
                continue;
            }

            var bodyStart = lines[i].End;
            var bodyEnd = closeIndex == i + 1
                ? bodyStart
                : lines[closeIndex - 1].ContentEnd;
            commands.Add(new Command(name, content.Substring(bodyStart, bodyEnd - bodyStart)));
            i = closeIndex;
        }

        return new CommandBatch(
            commands,
            string.Join('\n', visible).Trim());
    }

    private static int FindClosingTag(IReadOnlyList<LineSlice> lines, int start, string name)
    {
        var closingTag = $"</{name}>";
        for (var i = start; i < lines.Count; i++)
        {
            if (string.Equals(lines[i].Text.Trim(), closingTag, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    // Match the UI's raw-block closing contract: CodeBlock uses the first
    // standalone delimiter; WritingBlock ignores delimiters in Markdown
    // fences, inline code spans, and HTML comments. Never release a command
    // from content that the renderer still considers literal source.
    private static int FindRawPresentationClosingTag(
        IReadOnlyList<LineSlice> lines, int start, string name)
    {
        char fenceMarker = '\0';
        var fenceLength = 0;
        var insideComment = false;
        var inlineTicks = 0;

        for (var i = start; i < lines.Count; i++)
        {
            var line = lines[i].Text;
            if (name == "WritingBlock")
            {
                if (fenceMarker != '\0')
                {
                    if (IsClosingFence(line, fenceMarker, fenceLength))
                        fenceMarker = '\0';
                    continue;
                }
                if (!insideComment && inlineTicks == 0 &&
                    TryOpeningFence(line, out var openingMarker, out var openingLength))
                {
                    fenceMarker = openingMarker;
                    fenceLength = openingLength;
                    continue;
                }
                if (!insideComment && inlineTicks == 0 &&
                    IsStandaloneRawClosingTag(line, name))
                    return i;
                TrackInlineMarkdown(line, ref insideComment, ref inlineTicks);
            }
            else if (IsStandaloneRawClosingTag(line, name))
            {
                return i;
            }
        }
        return -1;
    }

    private static bool IsStandaloneRawClosingTag(string line, string name)
    {
        var indent = 0;
        while (indent < line.Length && indent < 4 && line[indent] == ' ')
            indent++;
        if (indent > 3) return false;
        // Do not trim leading tabs or other indentation: the UI's regex
        // permits only zero to three spaces before this raw delimiter.
        var value = line.AsSpan(indent);
        while (value.Length > 0 && value[^1] is ' ' or '\t')
            value = value[..^1];
        var prefix = $"</{name}";
        if (!value.StartsWith(prefix.AsSpan(), StringComparison.Ordinal))
            return false;
        value = value[prefix.Length..];
        while (value.Length > 0 && value[0] is ' ' or '\t')
            value = value[1..];
        return value.SequenceEqual(">".AsSpan());
    }

    // The WritingBlock body is Markdown source. An unmatched backtick or
    // HTML comment conservatively keeps subsequent lines inert.
    private static void TrackInlineMarkdown(
        string line, ref bool insideComment, ref int inlineTicks)
    {
        for (var i = 0; i < line.Length;)
        {
            if (insideComment)
            {
                var end = line.IndexOf("-->", i, StringComparison.Ordinal);
                if (end < 0) return;
                insideComment = false;
                i = end + 3;
                continue;
            }
            if (line[i] == '\x60')
            {
                var end = i + 1;
                while (end < line.Length && line[end] == '\x60') end++;
                var length = end - i;
                if (inlineTicks == 0) inlineTicks = length;
                else if (inlineTicks == length) inlineTicks = 0;
                i = end;
                continue;
            }
            if (inlineTicks == 0 &&
                line.AsSpan(i).StartsWith("<!--", StringComparison.Ordinal))
            {
                insideComment = true;
                i += 4;
                continue;
            }
            i++;
        }
    }

    private static bool TryRawPresentationOpening(string text, out string name)
    {
        name = string.Empty;
        foreach (var candidate in new[] { "CodeBlock", "WritingBlock" })
        {
            if (!text.StartsWith("<" + candidate, StringComparison.Ordinal)) continue;
            var remainder = text.AsSpan(candidate.Length + 1);
            if (remainder.Length == 0 || (remainder[0] != '>' && !char.IsWhiteSpace(remainder[0])) ||
                text.EndsWith("/>", StringComparison.Ordinal)) return false;

            // An apparent </CodeBlock> inside a quoted attribute is not an
            // inline closing delimiter. Find the first unquoted '>' so that
            // a legitimate standalone raw opener still shields its body.
            var quote = '\0';
            for (var i = candidate.Length + 1; i < text.Length; i++)
            {
                var ch = text[i];
                if (quote != '\0')
                {
                    if (ch == quote) quote = '\0';
                }
                else if (ch is '"' or '\'')
                {
                    quote = ch;
                }
                else if (ch == '>')
                {
                    // Only a complete same-line close is self-contained.
                    // Arbitrary trailing/malformed opener text must remain
                    // fail-closed rather than exposing command-shaped lines.
                    var tail = text.AsSpan(i + 1).Trim();
                    if (tail.EndsWith(("</" + candidate + ">").AsSpan(),
                        StringComparison.Ordinal)) return false;
                    break;
                }
            }
            // Even malformed opening metadata is inert. Do not let an invalid
            // presentation tag expose its literal shell examples to the Agent.
            name = candidate;
            return true;
        }
        return false;
    }

    private static bool TryOpeningFence(string line, out char marker, out int count)
    {
        marker = '\0';
        count = 0;
        var indent = 0;
        while (indent < line.Length && indent < 4 && line[indent] == ' ') indent++;
        if (indent > 3 || indent >= line.Length || line[indent] is not ('\x60' or '~')) return false;
        var candidate = line[indent];
        while (indent + count < line.Length && line[indent + count] == candidate) count++;
        if (count < 3) return false;
        marker = candidate;
        return true;
    }

    private static bool IsClosingFence(string line, char marker, int requiredLength)
    {
        var indent = 0;
        while (indent < line.Length && indent < 4 && line[indent] == ' ') indent++;
        if (indent > 3) return false;
        var count = 0;
        while (indent + count < line.Length && line[indent + count] == marker) count++;
        return count >= requiredLength && line.AsSpan(indent + count).Trim().IsEmpty;
    }

    private static bool TryTag(string text, out string name, out bool closing)
    {
        name = string.Empty;
        closing = false;
        if (text.Length < 3 || text[0] != '<' || text[^1] != '>')
            return false;

        var value = text.AsSpan(1, text.Length - 2);
        if (value.Length > 0 && value[0] == '/')
        {
            closing = true;
            value = value[1..];
        }
        // Display markup uses lowercase tag names and may include attributes.
        // Only command-shaped uppercase tags participate in the Agent protocol.
        if (value.Length == 0 || value[0] is < 'A' or > 'Z')
            return false;
        // PascalCase UI controls are not Agent commands. Strict ALL_CAPS
        // commands (including unknown ones) retain their existing rejection path.
        if (value.Length > 1 && value[1] is >= 'a' and <= 'z') return false;
        if (!TryCommandName(value, out name))
            throw new CommandParseException($"Malformed command tag '{text}'.");
        return true;
    }

    private static bool TryCommandName(ReadOnlySpan<char> value, out string name)
    {
        name = string.Empty;
        if (value.Length == 0 || value[0] is < 'A' or > 'Z')
            return false;
        foreach (var character in value)
        {
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')
                continue;
            return false;
        }
        name = value.ToString();
        return true;
    }

    private static IReadOnlyList<LineSlice> ReadLines(string content)
    {
        var result = new List<LineSlice>();
        var position = 0;
        while (position < content.Length)
        {
            var start = position;
            while (position < content.Length && content[position] is not ('\r' or '\n'))
                position++;
            var contentEnd = position;
            if (position < content.Length && content[position] == '\r')
                position++;
            if (position < content.Length && content[position] == '\n')
                position++;
            result.Add(new LineSlice(
                content.Substring(start, contentEnd - start),
                contentEnd,
                position));
        }
        return result;
    }

    private sealed record LineSlice(string Text, int ContentEnd, int End);
}