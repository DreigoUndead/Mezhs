using System.Text.Json.Serialization;

namespace Mezhs.Agent.Models;

public enum AgentExecutionStatus
{
    Queued,
    Running,
    CancelRequested,
    Completed,
    Failed,
    Cancelled
}

public enum AgentExecutionKind
{
    Agent,
    Shell
}

internal static class AgentExecutionStatusExtensions
{
    public static bool IsTerminal(this AgentExecutionStatus status) =>
        status is AgentExecutionStatus.Completed
            or AgentExecutionStatus.Failed
            or AgentExecutionStatus.Cancelled;
}

public sealed class AgentChatRecord
{
    public required string ChatId { get; init; }
    public required string PolicyId { get; set; }
    public required string OriginSource { get; init; }
    public string? OriginReference { get; init; }
    public bool Paused { get; set; }
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
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

public sealed record AgentExecutionState(
    string ExecutionId,
    AgentExecutionStatus Status,
    DateTimeOffset? CompletedAt);

public sealed record AgentMessageRuntimeView(
    string MessageId,
    string Status,
    string? Activity,
    string? ActivityDetail,
    DateTimeOffset? ActivityAt);

public sealed record AgentExecutionRuntimeView(
    string ExecutionId,
    string Kind,
    string Status,
    bool IsTerminal,
    DateTimeOffset? CompletedAt,
    string? RestartedAsId);

public sealed record AgentChatRuntimeView(
    int MessageCount,
    string? LatestMessageId,
    string? LatestMessageStatus,
    AgentMessageRuntimeView? ActiveMessage,
    IReadOnlyList<AgentExecutionRuntimeView> Executions);

public sealed record AgentProtocolCommandView(
    string Name,
    string? Body,
    int? CommandIndex);

public sealed record AgentChatMessageView(
    string MessageId,
    string ChatId,
    string ConnectionId,
    string Role,
    string Origin,
    string Content,
    string DisplayContent,
    IReadOnlyList<string> FileIds,
    string? ParentMessageId,
    string? ReplayOfMessageId,
    string? ReplyMessageId,
    string Status,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Activity,
    string? ActivityDetail,
    string? Analysis,
    DateTimeOffset? ActivityAt,
    IReadOnlyList<AgentProtocolCommandView> Commands,
    bool CompletionClaimed);

public sealed class ExecutionRecord
{
    public required string ExecutionId { get; init; }
    public string? ParentExecutionId { get; init; }
    public required string CorrelationId { get; init; }
    public required AgentExecutionKind Kind { get; init; }
    public string? CommandName { get; init; }
    public string? TriggerMessageId { get; init; }
    public int? CommandIndex { get; init; }
    public string? ChatId { get; set; }
    public required string PolicyId { get; init; }
    public required string ConnectionId { get; init; }
    public required string Source { get; init; }
    public string? SourceReference { get; init; }
    public required AgentExecutionStatus Status { get; set; }
    public required string Request { get; init; }
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
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
    string? ChatId = null,
    IReadOnlyDictionary<string, string>? Environment = null);

public sealed record UpdateAgentChatRequest(bool Paused);

public sealed record AgentPolicyView(
    string Id,
    string ConnectionId,
    string ModelInstructions,
    string Snapshot);

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
    bool IsTerminal,
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

public static class AgentIds
{
    public static string New(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
}
