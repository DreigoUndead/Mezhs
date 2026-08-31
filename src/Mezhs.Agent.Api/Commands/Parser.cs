namespace Mezhs.Agent.Commands;

public sealed record Command(string Name, string? Body);

public sealed record CommandBatch(IReadOnlyList<Command> Commands);

public sealed class CommandParseException(string message) : Exception(message);

public sealed class Parser
{
    public CommandBatch Parse(string content)
    {
        var lines = ReadLines(content);
        var commands = new List<Command>();

        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i].Text.Trim();
            if (!TryTag(text, out var name, out var closing))
                continue;
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

        return new CommandBatch(commands);
    }

    private static int FindClosingTag(IReadOnlyList<LineSlice> lines, int start, string name)
    {
        for (var i = start; i < lines.Count; i++)
        {
            var text = lines[i].Text.Trim();
            if (!TryTag(text, out var candidate, out var closing))
                continue;
            if (closing && string.Equals(candidate, name, StringComparison.Ordinal))
                return i;
            return -1;
        }
        return -1;
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
