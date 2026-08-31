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
        var chatId = string.IsNullOrWhiteSpace(request.ChatId) ? null : request.ChatId.Trim();
        var requestedEnvironment = request.Environment is null ? null : NormalizeEnvironment(request.Environment);
        IReadOnlyDictionary<string, string> environment = requestedEnvironment ?? EmptyEnvironment();

        if (chatId is not null)
        {
            store.ValidateAgentChatPolicy(chatId, policyId);
            store.ValidateAgentChatRunnable(chatId);
            if (store.GetAgentChat(chatId) is { } existing)
            {
                if (requestedEnvironment is not null && !EnvironmentEquals(existing.Environment, requestedEnvironment))
                    throw new RequestValidationException(
                        $"Agent chat '{chatId}' already has a different environment. Environment is fixed when the chat is first claimed.");
                environment = existing.Environment;
            }
        }

        var execution = store.CreateRootExecution(
            policyId,
            policy.ConnectionId,
            chatId,
            source: "manual",
            sourceReference: null,
            request.Input.Trim(),
            environment,
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

    private static IReadOnlyDictionary<string, string> NormalizeEnvironment(
        IReadOnlyDictionary<string, string> values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawName, value) in values)
        {
            var name = rawName?.Trim() ?? string.Empty;
            if (name.Length == 0 || name.Contains('=') || name.Contains('\0'))
                throw new RequestValidationException($"Invalid environment variable name '{rawName}'.");
            if (value?.Contains('\0') == true)
                throw new RequestValidationException($"Environment variable '{name}' contains an invalid null character.");
            if (!result.TryAdd(name, value ?? string.Empty))
                throw new RequestValidationException($"Environment variable '{name}' is duplicated.");
        }
        return result;
    }

    private static bool EnvironmentEquals(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out var value) && string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static IReadOnlyDictionary<string, string> EmptyEnvironment() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
