using Mezhs.Console;

namespace Mezhs.Executor;

public enum ExecutionStatus
{
    Created,
    Running,
    KillRequested,
    Completed,
    Failed,
    Killed,
    TimedOut,
    Dead
}

public sealed class Execution : ReturnObjectBase
{
    public int Id { get; set; }
    public ExecutionStatus Status { get; set; }
    public string Command { get; set; } = string.Empty;
    public string Directory { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; }
    public int? OwnerProcessId { get; set; }
    public int? ProcessId { get; set; }
    public int? ExitCode { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int? RestartedFromId { get; set; }
    public int? RestartedAsId { get; set; }
    public string? ChatId { get; set; }
    public string? ParentExecutionId { get; set; }
    public string? CorrelationId { get; set; }
    public string? Source { get; set; }
    public string? Workspace { get; set; }
    public string? TriggerMessageId { get; set; }
    public int? CommandIndex { get; set; }

    public bool IsTerminal => Status is ExecutionStatus.Completed
        or ExecutionStatus.Failed
        or ExecutionStatus.Killed
        or ExecutionStatus.TimedOut
        or ExecutionStatus.Dead;
}
