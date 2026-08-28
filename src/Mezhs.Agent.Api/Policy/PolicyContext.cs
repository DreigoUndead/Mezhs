using Mezhs.Agent.Models;

namespace Mezhs.Agent.Policy;

public sealed class PolicyContext
{
    private readonly IReadOnlyList<Func<PolicyTurnContext, string?>> _turnValidators;
    private readonly Func<PolicyCompletionContext, bool> _completionClaim;
    private readonly IReadOnlyList<Func<PolicyCompletionContext, string?>> _completionValidators;
    private readonly IReadOnlyList<Func<PolicyActionContext, PolicyActionRuleResult>> _actionRules;

    internal PolicyContext(
        string id,
        PolicySettings settings,
        string modelInstructions,
        string snapshot,
        IReadOnlyList<Func<PolicyTurnContext, string?>> turnValidators,
        Func<PolicyCompletionContext, bool> completionClaim,
        IReadOnlyList<Func<PolicyCompletionContext, string?>> completionValidators,
        IReadOnlyList<Func<PolicyActionContext, PolicyActionRuleResult>> actionRules)
    {
        Id = id;
        Settings = settings;
        ModelInstructions = modelInstructions;
        Snapshot = snapshot;
        _turnValidators = turnValidators;
        _completionClaim = completionClaim;
        _completionValidators = completionValidators;
        _actionRules = actionRules;
    }

    public string Id { get; }
    public PolicySettings Settings { get; }
    public string ConnectionId => Settings.ConnectionId;
    public string ModelInstructions { get; }
    public string Snapshot { get; }

    public PolicyDecision ValidateTurn(PolicyTurnContext context)
    {
        foreach (var validate in _turnValidators)
        {
            if (validate(context) is { } error)
                return PolicyDecision.Deny(error);
        }
        return PolicyDecision.Allow();
    }

    public PolicyCompletionDecision EvaluateCompletion(PolicyCompletionContext context)
    {
        if (!_completionClaim(context))
            return PolicyCompletionDecision.Incomplete();

        foreach (var validate in _completionValidators)
        {
            if (validate(context) is { } error)
                return PolicyCompletionDecision.Reject(error);
        }
        return PolicyCompletionDecision.Accept();
    }

    public PolicyDecision ValidateAction(PolicyActionContext context)
    {
        var allowed = false;
        foreach (var evaluate in _actionRules)
        {
            var result = evaluate(context);
            if (result.Decision == PolicyActionRuleDecision.Deny)
                return PolicyDecision.Deny(
                    result.Error ?? $"Policy denied {context.Action.Kind} action.");
            if (result.Decision == PolicyActionRuleDecision.Allow)
                allowed = true;
        }

        return allowed
            ? PolicyDecision.Allow()
            : PolicyDecision.Deny(
                $"Policy does not explicitly allow {context.Action.Kind} actions.");
    }
}

public sealed record PolicyEvaluationContext(
    ExecutionRecord Execution,
    IReadOnlyList<ExecutionRecord> Evidence);

public sealed record PolicyTurnContext(
    PolicyEvaluationContext Execution,
    int TurnIndex);

public sealed record PolicyCompletionContext(
    PolicyEvaluationContext Execution,
    string AssistantReply);

public sealed record PolicyAction(
    string Kind,
    string Request);

public sealed record PolicyActionContext(
    PolicyEvaluationContext Execution,
    PolicyAction Action);

public sealed record PolicyDecision(bool Allowed, string? Error)
{
    public static PolicyDecision Allow() => new(true, null);
    public static PolicyDecision Deny(string error) => new(false, error);
}

public enum PolicyCompletionState
{
    Incomplete,
    Accepted,
    Rejected
}

public sealed record PolicyCompletionDecision(
    PolicyCompletionState State,
    string? Error)
{
    public static PolicyCompletionDecision Incomplete() =>
        new(PolicyCompletionState.Incomplete, null);

    public static PolicyCompletionDecision Accept() =>
        new(PolicyCompletionState.Accepted, null);

    public static PolicyCompletionDecision Reject(string error) =>
        new(PolicyCompletionState.Rejected, error);
}

internal enum PolicyActionRuleDecision
{
    None,
    Allow,
    Deny
}

internal sealed record PolicyActionRuleResult(
    PolicyActionRuleDecision Decision,
    string? Error = null);
