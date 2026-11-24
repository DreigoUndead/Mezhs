using System.Text;
using Mezhs.Common.RichUi;

namespace Mezhs.Agent.Commands;

public sealed record Command(string Name, string? Body);

public sealed record CommandBatch(
    IReadOnlyList<Command> Commands,
    string VisibleContent);

public sealed class CommandParseException(string message) : Exception(message);

// One authoritative syntax tree. Agent policy and Registry remain the only
// owners of command authority; this adapter never discovers text commands.
public sealed class Parser
{
    public CommandBatch Parse(string content)
    {
        var parsed = new RichDocumentParser(Registry.RichTags).Parse(content);
        if (!parsed.Document.Valid)
            throw new CommandParseException(parsed.Document.Diagnostics.FirstOrDefault()?.Code ??
                "Invalid rich document.");

        var commands = new List<Command>(parsed.Commands.Count);
        var visible = new StringBuilder(content.Length);
        var cursor = 0;
        foreach (var candidate in parsed.Commands)
        {
            if (!Registry.TryGet(candidate.Name, out var definition) ||
                (definition.Form == CommandForm.Block) != (candidate.Body is not null))
                throw new CommandParseException($"Unsupported Agent command: {candidate.Name}.");

            var start = candidate.SourceSpan.Start;
            var end = candidate.SourceSpan.End;
            if (start < cursor || end < start || end > content.Length)
                throw new CommandParseException("Invalid command source range.");
            // Consume only the exact matched tag span; surrounding prose and
            // line breaks remain visible.
            visible.Append(content, cursor, start - cursor);
            cursor = end;
            commands.Add(new Command(candidate.Name, candidate.Body));
        }
        visible.Append(content, cursor, content.Length - cursor);
        return new CommandBatch(commands, visible.ToString());
    }
}
