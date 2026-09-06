using Mezhs.Agent.Models;
using Mezhs.Agent.Persistence;
using Mezhs.Agent.Services;

namespace Mezhs.Agent.Policy;

public sealed class PolicyEvaluationService(
    AgentStore store,
    AgentMetrics metrics)
{
    public PolicyDecision ValidateTurn(
        PolicyContext policy,
        ExecutionRecord execution,
        int turnIndex)
    {
        var decision = policy.ValidateTurn(new PolicyTurnContext(Create(execution), turnIndex));
        if (!decision.Allowed)
            metrics.RecordPolicyDenial();
        return decision;
    }

    public PolicyCompletionDecision EvaluateCompletion(
        PolicyContext policy,
        ExecutionRecord execution,
        bool completionClaimed)
    {
        var decision = policy.EvaluateCompletion(
            new PolicyCompletionContext(Create(execution), completionClaimed));
        if (decision.State == PolicyCompletionState.Rejected)
            metrics.RecordPolicyDenial();
        return decision;
    }

    public PolicyDecision ValidateAction(
        PolicyContext policy,
        ExecutionRecord execution,
        PolicyAction action)
    {
        var decision = policy.ValidateAction(new PolicyActionContext(Create(execution), action));
        if (!decision.Allowed)
            metrics.RecordPolicyDenial();
        return decision;
    }

    private PolicyEvaluationContext Create(ExecutionRecord execution)
    {
        var evidence = store.GetExecutions(execution.ChatId)
            .Where(record => string.Equals(
                record.CorrelationId,
                execution.CorrelationId,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(record => record.CreatedAt)
            .Select(ToEvidence)
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
}
