using Mezhs.Agent.Configuration;
using Mezhs.Agent.Models;
using Mezhs.Agent.Persistence;
using Mezhs.Services;

namespace Mezhs.Agent.Services;

public sealed class AgentService(
    AgentStore store,
    PolicyRegistry policies,
    AgentWorker worker,
    IntegrationRegistry integrations,
    ChatStore chats)
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
        var connectionId = ResolveConnectionId(request.ConnectionId, chatId, policy.ConnectionId);
        var integration = integrations.Get(connectionId);
        // null means inherit an existing chat connection/model state. On a fresh policy-default
        // target, the policy's model/effort default is materialized into durable execution state.
        // Empty means explicit provider Default.
        var model = request.Model is null
            ? chatId is null &&
              string.Equals(connectionId, policy.ConnectionId, StringComparison.OrdinalIgnoreCase)
                ? policy.DefaultModel
                : null
            : request.Model.Trim();
        if (!string.IsNullOrWhiteSpace(model) && integration.Models is null)
            throw new RequestValidationException(
                $"Connection '{connectionId}' does not support model selection.");
        var requestedEnvironment = request.Environment is null
            ? null
            : NormalizeEnvironment(request.Environment, policy.Settings.Environment.Allow);
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

        ExecutionRecord execution;
        lock (chats.DeletionSync)
        {
            if (chatId is not null)
            {
                var canonical = chats.GetChat(chatId)
                    ?? throw new ResourceNotFoundException($"Chat '{chatId}' was not found.");
                chatId = canonical.ChatId;
                store.ValidateAgentChatPolicy(chatId, policyId);
                store.ValidateAgentChatRunnable(chatId);
            }
            execution = store.TryCreateRootExecution(
                policyId, connectionId, chatId,
                source: "manual", sourceReference: null, request.Input,
                environment, policies.Snapshot(policyId), model);
        }

        worker.SignalWork();
        return execution;
    }

    public async Task<IReadOnlyList<string>> DeleteChatsAsync(
        IEnumerable<string> chatIds,
        CancellationToken cancellationToken = default)
    {
        var entries = chatIds.ToArray();
        if (entries.Length == 0)
            throw new RequestValidationException("At least one chatId is required.");
        if (entries.Any(string.IsNullOrWhiteSpace))
            throw new RequestValidationException("chatIds cannot contain empty chat IDs.");
        var requested = entries.Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        string[] ids;
        lock (chats.DeletionSync)
        {
            ids = requested.Select(id => chats.GetChat(id)?.ChatId
                ?? throw new ResourceNotFoundException($"Chat '{id}' was not found.")).ToArray();
            worker.BeginChatDeletion(ids);
        }

        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stop.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                // Agent reasoning joins, but Executor shells keep running independently.
                await worker.WaitForChatStopAsync(ids, stop.Token);
                while (true)
                {
                    bool active;
                    lock (chats.DeletionSync)
                        active = ids.Any(id => chats.GetRuntimeState(id).ActiveMessage is not null);
                    if (!active) break;
                    await Task.Delay(50, stop.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RequestValidationException(
                    "Agent or chat processing did not stop in time. No chat data was deleted.");
            }

            lock (chats.DeletionSync)
            {
                if (ids.Any(id => chats.GetRuntimeState(id).ActiveMessage is not null))
                    throw new RequestValidationException(
                        "Chat processing resumed before deletion. No chat data was deleted.");
                store.RemoveChatData(ids); // Agent SQLite transaction first.
                chats.DeleteChats(ids);      // Core SQLite transaction second.
                return ids;
            }
        }
        finally
        {
            worker.EndChatDeletion(ids);
        }
    }

    public AgentChatRecord SetPaused(string chatId, bool paused)
    {
        var chat = store.SetAgentChatPaused(chatId, paused);
        if (!paused)
        {
            worker.SignalWork();
            return chat;
        }

        foreach (var execution in store.GetExecutions(chatId)
                     .Where(record =>
                         record.Kind == AgentExecutionKind.Agent &&
                         record.Status is AgentExecutionStatus.Queued or AgentExecutionStatus.Running))
        {
            worker.Cancel(execution.ExecutionId);
        }
        return store.GetAgentChat(chatId)!;
    }

    private string ResolveConnectionId(
        string? requestedConnectionId,
        string? chatId,
        string policyDefaultConnectionId)
    {
        if (!string.IsNullOrWhiteSpace(requestedConnectionId))
            return requestedConnectionId.Trim();

        if (chatId is not null)
        {
            var previous = store.GetExecutions(chatId)
                .Where(record => record.Kind == AgentExecutionKind.Agent && record.ParentExecutionId is null)
                .OrderByDescending(record => record.CreatedAt)
                .ThenByDescending(record => record.ExecutionId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (previous is not null)
                return previous.ConnectionId;
        }

        return policyDefaultConnectionId;
    }

    private static IReadOnlyDictionary<string, string> NormalizeEnvironment(
        IReadOnlyDictionary<string, string> values,
        IReadOnlyList<string> allowedNames)
    {
        var allowed = allowedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawName, value) in values)
        {
            var name = rawName?.Trim() ?? string.Empty;
            if (name.Length == 0 || name.Contains('=') || name.Contains('\0'))
                throw new RequestValidationException($"Invalid environment variable name '{rawName}'.");
            if (name.StartsWith("MEZHS_", StringComparison.OrdinalIgnoreCase))
                throw new RequestValidationException($"Environment variable '{name}' is reserved by MEŽS.");
            if (!allowed.Contains(name))
                throw new RequestValidationException($"Policy does not allow environment variable '{name}'.");
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
