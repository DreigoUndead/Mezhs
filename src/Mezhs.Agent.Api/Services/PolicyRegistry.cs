using Mezhs.Agent.Configuration;
using Mezhs.Agent.Models;
using Mezhs.Agent.Policy;

namespace Mezhs.Agent.Services;

public sealed class PolicyRegistry(AgentOptions options)
{
    public PolicyContext Get(string policyId)
    {
        policyId = policyId?.Trim() ?? string.Empty;
        if (!options.Policies.TryGetValue(policyId, out var policy))
            throw new ResourceNotFoundException($"Agent policy '{policyId}' was not found.");
        return policy;
    }

    public AgentPolicyView GetView(string policyId)
    {
        var policy = Get(policyId);
        return new AgentPolicyView(
            policy.Id,
            policy.ConnectionId,
            policy.ModelInstructions,
            policy.Snapshot);
    }

    public IReadOnlyList<AgentPolicyView> GetViews() =>
        options.Policies.Keys
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Select(GetView)
            .ToArray();

    public string Snapshot(string policyId) => Get(policyId).Snapshot;
}
