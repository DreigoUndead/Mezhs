using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mezhs.Agent.Api.Client;

public sealed class AgentApiClient(HttpClient client)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<AgentRuntimeView> GetRuntimeAsync(CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync("/v1/runtime", cancellationToken);
        return await ReadAsync<AgentRuntimeView>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<AgentPolicyView>> GetPoliciesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync("/v1/policies", cancellationToken);
        return await ReadAsync<AgentPolicyView[]>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<AgentChatView>> GetChatsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync("/v1/agent-chats", cancellationToken);
        return await ReadAsync<AgentChatView[]>(response, cancellationToken);
    }

    public async Task<AgentChatView> GetChatAsync(
        string chatId,
        CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(
            $"/v1/agent-chats/{Uri.EscapeDataString(chatId)}",
            cancellationToken);
        return await ReadAsync<AgentChatView>(response, cancellationToken);
    }

    public async Task<AgentChatView> SetPausedAsync(
        string chatId,
        bool paused,
        CancellationToken cancellationToken = default)
    {
        using var response = await client.PatchAsJsonAsync(
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
        using var response = await client.PostAsJsonAsync(
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
        using var response = await client.GetAsync(path, cancellationToken);
        return await ReadAsync<AgentExecutionView[]>(response, cancellationToken);
    }

    public async Task<AgentExecutionView> GetExecutionAsync(
        string executionId,
        CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(
            $"/v1/executions/{Uri.EscapeDataString(executionId)}",
            cancellationToken);
        return await ReadAsync<AgentExecutionView>(response, cancellationToken);
    }

    public async Task<AgentExecutionView> CancelExecutionAsync(
        string executionId,
        CancellationToken cancellationToken = default)
    {
        using var response = await client.PostAsync(
            $"/v1/executions/{Uri.EscapeDataString(executionId)}/cancel",
            null,
            cancellationToken);
        return await ReadAsync<AgentExecutionView>(response, cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"MEŽS Agent API returned HTTP {(int)response.StatusCode}: {body}");
        }
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
            ?? throw new InvalidOperationException("MEŽS Agent API returned an empty response.");
    }
}

public sealed record AgentRuntimeView(string Status, string MezhsApi, bool MezhsApiHealthy);

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
    IReadOnlyDictionary<string, string> Environment,
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
    string? ChatId,
    string PolicyId,
    string ConnectionId,
    string Source,
    string? SourceReference,
    string Status,
    string Request,
    IReadOnlyDictionary<string, string> Environment,
    string? Result,
    string? Error,
    int? ExitCode,
    string PolicySnapshot,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);
