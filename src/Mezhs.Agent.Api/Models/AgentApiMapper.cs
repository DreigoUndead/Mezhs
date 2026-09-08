using Mezhs.Agent.Commands;
using Mezhs.Api.Contracts;

namespace Mezhs.Agent.Models;

public static class AgentApiMapper
{
    public static AgentExecutionView ToView(ExecutionRecord record) => new(
        record.ExecutionId,
        record.ParentExecutionId,
        record.CorrelationId,
        record.Kind,
        record.CommandName,
        record.TriggerMessageId,
        record.CommandIndex,
        record.ChatId,
        record.PolicyId,
        record.ConnectionId,
        record.Source,
        record.SourceReference,
        record.Status,
        record.Request,
        record.Result,
        record.Error,
        record.ExitCode,
        record.PolicySnapshot,
        record.CreatedAt,
        record.StartedAt,
        record.CompletedAt);

    public static AgentChatMessageView ToView(
        ApiChatHistoryMessage message,
        Parser parser)
    {
        var displayContent = message.Content;
        var protocolCommands = new List<AgentProtocolCommandView>();
        var completionClaimed = false;

        if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var batch = parser.Parse(message.Content);
                displayContent = batch.VisibleContent;
                var executableIndex = 0;
                foreach (var command in batch.Commands)
                {
                    if (Registry.TryGet(command.Name, out var definition) &&
                        definition.Behavior == CommandBehavior.Complete)
                    {
                        completionClaimed = true;
                        continue;
                    }

                    int? commandIndex = null;
                    if (Registry.TryGet(command.Name, out definition))
                        commandIndex = executableIndex++;
                    protocolCommands.Add(new AgentProtocolCommandView(
                        command.Name,
                        command.Body,
                        commandIndex));
                }
            }
            catch (CommandParseException)
            {
                // Stored malformed replies remain readable. Runtime validation owns command errors.
            }
        }

        return new AgentChatMessageView(
            message.MessageId,
            message.ChatId,
            message.ConnectionId,
            message.Role,
            message.Origin,
            message.Content,
            displayContent,
            message.FileIds,
            message.ParentMessageId,
            message.ReplayOfMessageId,
            message.ReplyMessageId,
            message.Status,
            message.Error,
            message.CreatedAt,
            message.StartedAt,
            message.CompletedAt,
            protocolCommands,
            completionClaimed);
    }
}
