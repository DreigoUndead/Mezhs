using Mezhs.Agent.Models;
using Mezhs.Agent.Persistence;

namespace Mezhs.Agent.Services;

public sealed class AgentService(
    AgentStore store,
    PolicyRegistry policies,
    AgentWorker worker)
{
    public ExecutionRecord Start(CreateExecutionRequest request)
    {
        var policyId = request.PolicyId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(policyId))
            throw new RequestValidationException("policyId is required.");
        if (string.IsNullOrWhiteSpace(request.Input))
            throw new RequestValidationException("input is required.");

        var policy = policies.Get(policyId);
        var connectionId = string.IsNullOrWhiteSpace(request.ConnectionId)
            ? policy.ConnectionId
            : request.ConnectionId.Trim();
        var chatId = string.IsNullOrWhiteSpace(request.ChatId)
            ? null
            : request.ChatId.Trim();

        if (chatId is not null)
            store.ValidateAgentChatPolicy(chatId, policyId);

        var execution = store.CreateRootExecution(
            policyId,
            connectionId,
            chatId,
            source: "manual",
            sourceReference: null,
            request.Input.Trim(),
            policies.Snapshot(policyId));
        worker.Enqueue(execution.ExecutionId);
        return execution;
    }
}
