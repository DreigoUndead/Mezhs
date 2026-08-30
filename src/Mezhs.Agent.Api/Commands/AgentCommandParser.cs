namespace Mezhs.Agent.Commands;

public sealed record AgentCommand(string Name, string? Body);

public sealed record AgentCommandBatch(
    IReadOnlyList<AgentCommand> Commands,
    bool CompletionClaimed);

public sealed class AgentCommandParseException(string message) : Exception(message);

public sealed class AgentCommandParser
{
    public AgentCommandBatch Parse(string content)
    {
        var lines = ReadLines(content);
        var commands = new List<AgentCommand>();
        var completionClaimed = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i].Text.Trim();
            if (text.Length == 0)
                continue;

            if (TryMarker(text, out var markerName))
            {
                if (string.Equals(markerName, "DONE", StringComparison.Ordinal))
                {
                    if (completionClaimed)
                        throw new AgentCommandParseException("<DONE> may appear only once in an assistant reply.");
                    completionClaimed = true;
                    continue;
                }

                if (completionClaimed)
                    throw new AgentCommandParseException("Executable commands cannot appear after <DONE>.");
                commands.Add(new AgentCommand(markerName, null));
                continue;
            }

            if (TryBlockStart(text, out var commandName))
            {
                if (string.Equals(commandName, "DONE", StringComparison.Ordinal))
                    throw new AgentCommandParseException("DONE is a marker command. Use <DONE> on a line by itself.");
                if (completionClaimed)
                    throw new AgentCommandParseException("Executable commands cannot appear after <DONE>.");

                var closeIndex = -1;
                for (var candidate = i + 1; candidate < lines.Count; candidate++)
                {
                    var candidateText = lines[candidate].Text.Trim();
                    if (IsBlockEnd(candidateText, commandName))
                    {
                        closeIndex = candidate;
                        break;
                    }

                    if (TryMarker(candidateText, out _) ||
                        TryBlockStart(candidateText, out _) ||
                        TryAnyBlockEnd(candidateText, out _))
                    {
                        throw new AgentCommandParseException(
                            $"Nested or mismatched agent command syntax inside <{commandName} ... {commandName}> is not allowed.");
                    }
                }

                if (closeIndex < 0)
                    throw new AgentCommandParseException($"Agent command <{commandName} is missing closing {commandName}>.");

                var bodyStart = lines[i].End;
                var bodyEnd = closeIndex == i + 1
                    ? bodyStart
                    : lines[closeIndex - 1].ContentEnd;
                var body = content.Substring(bodyStart, bodyEnd - bodyStart);
                commands.Add(new AgentCommand(commandName, body));
                i = closeIndex;
                continue;
            }

            if (TryAnyBlockEnd(text, out var unmatchedName))
                throw new AgentCommandParseException($"Unexpected closing agent command {unmatchedName}>.");

            if (LooksLikeMalformedCommand(text))
                throw new AgentCommandParseException($"Malformed agent command syntax: '{text}'.");
        }

        return new AgentCommandBatch(commands, completionClaimed);
    }

    private static bool TryMarker(string text, out string name)
    {
        name = string.Empty;
        if (text.Length < 3 || text[0] != '<' || text[^1] != '>')
            return false;
        return TryCommandName(text.AsSpan(1, text.Length - 2), out name);
    }

    private static bool TryBlockStart(string text, out string name)
    {
        name = string.Empty;
        if (text.Length < 2 || text[0] != '<' || text[^1] == '>')
            return false;
        return TryCommandName(text.AsSpan(1), out name);
    }

    private static bool IsBlockEnd(string text, string expectedName) =>
        TryAnyBlockEnd(text, out var name) &&
        string.Equals(name, expectedName, StringComparison.Ordinal);

    private static bool TryAnyBlockEnd(string text, out string name)
    {
        name = string.Empty;
        if (text.Length < 2 || text[0] == '<' || text[^1] != '>')
            return false;
        return TryCommandName(text.AsSpan(0, text.Length - 1), out name);
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

    private static bool LooksLikeMalformedCommand(string text) =>
        text.Length > 1 && text[0] == '<' && text[1] is >= 'A' and <= 'Z';

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
