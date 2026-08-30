namespace Mezhs.Console;

public static class MezhsExecutionContext
{
    public const string ExecutionIdVariable = "MEZHS_EXECUTION_ID";
    public const string ParentExecutionIdVariable = "MEZHS_PARENT_EXECUTION_ID";
    public const string ChatIdVariable = "MEZHS_CHAT_ID";
    public const string CorrelationIdVariable = "MEZHS_CORRELATION_ID";
    public const string SourceVariable = "MEZHS_SOURCE";

    public static string? ExecutionId => Environment.GetEnvironmentVariable(ExecutionIdVariable);
    public static string? ParentExecutionId => Environment.GetEnvironmentVariable(ParentExecutionIdVariable);
    public static string? ChatId => Environment.GetEnvironmentVariable(ChatIdVariable);
    public static string? CorrelationId => Environment.GetEnvironmentVariable(CorrelationIdVariable);
    public static string? Source => Environment.GetEnvironmentVariable(SourceVariable);
    public static bool IsAvailable => !string.IsNullOrWhiteSpace(ExecutionId);
}
