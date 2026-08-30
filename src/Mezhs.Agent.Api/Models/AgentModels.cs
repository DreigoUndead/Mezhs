namespace Mezhs.Agent.Models;

public enum AgentExecutionStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
    Interrupted
}

public enum AgentExecutionKind
{
    Agent,
    Shell
}

public sealed class AgentChatRecord
{
    public required string ChatId { get; init; }
    public required string PolicyId { get; set; }
    public required string OriginSource { get; init; }
    public string? OriginReference { get; init; }
    public bool Paused { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

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

public sealed class ExecutionRecord
{
    public required string ExecutionId { get; init; }
    public string? ParentExecutionId { get; init; }
    public required string CorrelationId { get; init; }
    public required AgentExecutionKind Kind { get; init; }
    public string? ChatId { get; set; }
    public required string PolicyId { get; init; }
    public required string ConnectionId { get; init; }
    public required string Source { get; init; }
    public string? SourceReference { get; init; }
    public required AgentExecutionStatus Status { get; set; }
    public required string Request { get; init; }
    public string? Result { get; set; }
    public string? Error { get; set; }
    public int? ExitCode { get; set; }
    public required string PolicySnapshot { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed record CreateExecutionRequest(
    string PolicyId,
    string Input,
    string? ChatId = null);

public sealed record UpdateAgentChatRequest(bool Paused);

public sealed record AgentPolicyView(
    string Id,
    string ConnectionId,
    string ModelInstructions,
    string Snapshot);

public static class AgentIds
{
    public static string New(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
}
