using System.Net.Http.Json;
using Mezhs.Api.Client;

namespace Mezhs.Agent.Api.Client;

public sealed class AgentApiClient : MezhsApiClient
{
    public AgentApiClient(HttpClient client) : base(client)
    {
        if (client.BaseAddress is { IsLoopback: false })
            throw new InvalidOperationException("MEŽS Agent API client must target a loopback address.");
    }

    public async Task<AgentRuntimeView> GetRuntimeAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync("/v1/runtime", cancellationToken);
        return await ReadAsync<AgentRuntimeView>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<AgentPolicyView>> GetPoliciesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync("/v1/policies", cancellationToken);
        return await ReadAsync<AgentPolicyView[]>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<AgentChatView>> GetChatsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync("/v1/agent-chats", cancellationToken);
        return await ReadAsync<AgentChatView[]>(response, cancellationToken);
    }

    public async Task<AgentChatView> GetChatAsync(
        string chatId,
        CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(
            $"/v1/agent-chats/{Uri.EscapeDataString(chatId)}",
            cancellationToken);
        return await ReadAsync<AgentChatView>(response, cancellationToken);
    }

    public async Task<AgentChatView> SetPausedAsync(
        string chatId,
        bool paused,
        CancellationToken cancellationToken = default)
    {
        using var response = await Client.PatchAsJsonAsync(
            $"/v1/agent-chats/{Uri.EscapeDataString(chatId)}",
            new { paused },
            Json,
            cancellationToken);
        return await ReadAsync<AgentChatView>(response, cancellationToken);
    }

    public async Task<AgentExecutionView> StartExecutionAsync(
        CreateAgentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await Client.PostAsJsonAsync(
            "/v1/executions",
            request,
            Json,
            cancellationToken);
        return await ReadAsync<AgentExecutionView>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<AgentExecutionView>> GetExecutionsAsync(
        string? chatId = null,
        CancellationToken cancellationToken = default)
    {
        var path = string.IsNullOrWhiteSpace(chatId)
            ? "/v1/executions"
            : $"/v1/executions?chatId={Uri.EscapeDataString(chatId)}";
        using var response = await Client.GetAsync(path, cancellationToken);
        return await ReadAsync<AgentExecutionView[]>(response, cancellationToken);
    }

    public async Task<AgentExecutionView> GetExecutionAsync(
        string executionId,
        CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(
            $"/v1/executions/{Uri.EscapeDataString(executionId)}",
            cancellationToken);
        return await ReadAsync<AgentExecutionView>(response, cancellationToken);
    }

    public Task<AgentExecutionView> CancelExecutionAsync(
        string executionId,
        CancellationToken cancellationToken = default) =>
        ExecuteActionAsync(executionId, "cancel", cancellationToken);

    public Task<AgentExecutionView> KillExecutionAsync(
        string executionId,
        CancellationToken cancellationToken = default) =>
        ExecuteActionAsync(executionId, "kill", cancellationToken);

    public Task<AgentExecutionView> RestartExecutionAsync(
        string executionId,
        CancellationToken cancellationToken = default) =>
        ExecuteActionAsync(executionId, "restart", cancellationToken);

    private async Task<AgentExecutionView> ExecuteActionAsync(
        string executionId,
        string action,
        CancellationToken cancellationToken)
    {
        using var response = await Client.PostAsync(
            $"/v1/executions/{Uri.EscapeDataString(executionId)}/{action}",
            null,
            cancellationToken);
        return await ReadAsync<AgentExecutionView>(response, cancellationToken);
    }
}

public sealed record AgentRuntimeView(string Status);

public sealed record AgentPolicyView(
    string Id,
    string ConnectionId,
    string ModelInstructions,
    string Snapshot);

public sealed record AgentChatView(
    string ChatId,
    string PolicyId,
    string OriginSource,
    string? OriginReference,
    bool Paused,
    string? Title,
    string? ConnectionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateAgentExecutionRequest(
    string PolicyId,
    string Input,
    string? ChatId = null,
    IReadOnlyDictionary<string, string>? Environment = null);

public sealed record AgentExecutionView(
    string ExecutionId,
    string? ParentExecutionId,
    string CorrelationId,
    string Kind,
    string? CommandName,
    string? TriggerMessageId,
    int? CommandIndex,
    string? ChatId,
    string PolicyId,
    string ConnectionId,
    string Source,
    string? SourceReference,
    string Status,
    string Request,
    string? Result,
    string? Error,
    int? ExitCode,
    string PolicySnapshot,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? RestartedFromId = null,
    string? RestartedAsId = null);
