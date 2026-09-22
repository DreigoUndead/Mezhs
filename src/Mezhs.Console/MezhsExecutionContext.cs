namespace Mezhs.Console;

public static class MezhsExecutionContext
{
    public const string ExecutionIdVariable = "MEZHS_EXECUTION_ID";
    public const string ParentExecutionIdVariable = "MEZHS_PARENT_EXECUTION_ID";
    public const string ChatIdVariable = "MEZHS_CHAT_ID";
    public const string CorrelationIdVariable = "MEZHS_CORRELATION_ID";
    public const string SourceVariable = "MEZHS_SOURCE";
    public const string WorkspaceVariable = "MEZHS_WORKSPACE";
    public const string TriggerMessageIdVariable = "MEZHS_TRIGGER_MESSAGE_ID";
    public const string CommandIndexVariable = "MEZHS_COMMAND_INDEX";

    public static string? ExecutionId => Environment.GetEnvironmentVariable(ExecutionIdVariable);
    public static string? ParentExecutionId => Environment.GetEnvironmentVariable(ParentExecutionIdVariable);
    public static string? ChatId => Environment.GetEnvironmentVariable(ChatIdVariable);
    public static string? CorrelationId => Environment.GetEnvironmentVariable(CorrelationIdVariable);
    public static string? Source => Environment.GetEnvironmentVariable(SourceVariable);
    public static string? Workspace => Environment.GetEnvironmentVariable(WorkspaceVariable);
    public static string? TriggerMessageId => Environment.GetEnvironmentVariable(TriggerMessageIdVariable);
    public static string? CommandIndex => Environment.GetEnvironmentVariable(CommandIndexVariable);
    public static bool IsAvailable => !string.IsNullOrWhiteSpace(ExecutionId);
}
