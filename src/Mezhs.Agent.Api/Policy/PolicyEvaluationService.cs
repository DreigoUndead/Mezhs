using System.Globalization;
using Mezhs.Agent.Commands;
using Mezhs.Agent.Models;
using Mezhs.Agent.Persistence;
using Mezhs.Executor;

namespace Mezhs.Agent.Policy;

public sealed class PolicyEvaluationService(
    AgentStore store,
    ExecutorService executor)
{
    public PolicyDecision ValidateTurn(
        PolicyContext policy,
        ExecutionRecord execution,
        int turnIndex) =>
        policy.ValidateTurn(new PolicyTurnContext(Create(execution), turnIndex));

    public PolicyCompletionDecision EvaluateCompletion(
        PolicyContext policy,
        ExecutionRecord execution,
        bool completionClaimed) =>
        policy.EvaluateCompletion(
            new PolicyCompletionContext(Create(execution), completionClaimed));

    public PolicyDecision ValidateAction(
        PolicyContext policy,
        ExecutionRecord execution,
        PolicyAction action) =>
        policy.ValidateAction(new PolicyActionContext(Create(execution), action));

    private PolicyEvaluationContext Create(ExecutionRecord execution)
    {
        var agentEvidence = store.GetExecutions(execution.ChatId)
            .Where(record => record.Kind == AgentExecutionKind.Agent && string.Equals(
                record.CorrelationId,
                execution.CorrelationId,
                StringComparison.OrdinalIgnoreCase))
            .Select(ToEvidence);
        var shellEvidence = string.IsNullOrWhiteSpace(execution.ChatId)
            ? []
            : executor.List(execution.ChatId, 1000)
                .Where(record => string.Equals(
                    record.CorrelationId,
                    execution.CorrelationId,
                    StringComparison.OrdinalIgnoreCase))
                .Select(record => ToEvidence(record, execution.CorrelationId));
        var evidence = agentEvidence
            .Concat(shellEvidence)
            .OrderBy(record => record.CreatedAt)
            .ThenBy(record => record.ExecutionId, StringComparer.Ordinal)
            .ToArray();
        return new PolicyEvaluationContext(ToEvidence(execution), evidence);
    }

    private static ExecutionEvidence ToEvidence(ExecutionRecord record) => new(
        record.ExecutionId,
        record.ParentExecutionId,
        record.CorrelationId,
        record.Kind,
        record.CommandName,
        record.Status,
        record.Request,
        record.Result,
        record.Error,
        record.ExitCode,
        record.CreatedAt,
        record.StartedAt,
        record.CompletedAt);

    private static ExecutionEvidence ToEvidence(Execution record, string correlationId) => new(
        record.Id.ToString(CultureInfo.InvariantCulture),
        record.ParentExecutionId,
        record.CorrelationId ?? correlationId,
        AgentExecutionKind.Shell,
        Registry.Get(CommandBehavior.Shell).Name,
        ToAgentStatus(record.Status),
        record.Command,
        record.Result,
        record.Error,
        record.ExitCode,
        record.CreatedAt,
        record.StartedAt,
        record.CompletedAt);

    private static AgentExecutionStatus ToAgentStatus(ExecutionStatus status) => status switch
    {
        ExecutionStatus.Created => AgentExecutionStatus.Queued,
        ExecutionStatus.Running => AgentExecutionStatus.Running,
        ExecutionStatus.KillRequested => AgentExecutionStatus.CancelRequested,
        ExecutionStatus.Completed => AgentExecutionStatus.Completed,
        ExecutionStatus.Killed => AgentExecutionStatus.Cancelled,
        ExecutionStatus.Failed or ExecutionStatus.TimedOut or ExecutionStatus.Dead => AgentExecutionStatus.Failed,
        _ => AgentExecutionStatus.Failed
    };
}
