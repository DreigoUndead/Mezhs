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
        record.Requester,
        record.Status,
        record.Request,
        record.Result,
        record.Error,
        record.ExitCode,
        record.PolicySnapshot,
        record.CreatedAt,
        record.StartedAt,
        record.CompletedAt);
}
