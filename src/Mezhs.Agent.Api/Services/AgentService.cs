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
        var chatId = string.IsNullOrWhiteSpace(request.ChatId)
            ? null
            : request.ChatId.Trim();

        if (chatId is not null)
        {
            store.ValidateAgentChatPolicy(chatId, policyId);
            store.ValidateAgentChatRunnable(chatId);
        }

        var execution = store.CreateRootExecution(
            policyId,
            policy.ConnectionId,
            chatId,
            source: "manual",
            sourceReference: null,
            request.Input.Trim(),
            policies.Snapshot(policyId));
        worker.Enqueue(execution.ExecutionId);
        return execution;
    }

    public AgentChatRecord SetPaused(string chatId, bool paused)
    {
        var chat = store.SetAgentChatPaused(chatId, paused);
        if (!paused)
            return chat;

        foreach (var execution in store.GetExecutions(chatId)
                     .Where(record =>
                         record.Kind == AgentExecutionKind.Agent &&
                         record.Status is AgentExecutionStatus.Queued or AgentExecutionStatus.Running))
        {
            worker.Cancel(execution.ExecutionId);
        }
        return store.GetAgentChat(chatId)!;
    }
}
