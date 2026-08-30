using Mezhs.Agent.Models;
using Mezhs.Agent.Persistence;

namespace Mezhs.Agent.Policy;

public sealed class PolicyEvaluationService(AgentStore store)
{
    public PolicyEvaluationContext Create(ExecutionRecord execution)
    {
        var evidence = store.GetExecutions(execution.ChatId)
            .Where(record => string.Equals(
                record.CorrelationId,
                execution.CorrelationId,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(record => record.CreatedAt)
            .ToArray();
        return new PolicyEvaluationContext(execution, evidence);
    }
}
