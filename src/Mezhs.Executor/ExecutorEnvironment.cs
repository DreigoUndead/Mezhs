namespace Mezhs.Executor;

public static class ExecutorEnvironment
{
    public const string StorageVariable = "MEZHS_EXECUTOR_STORAGE";
    public const string ExecutionIdVariable = "MEZHS_EXECUTION_ID";
    public const string ParentExecutionIdVariable = "MEZHS_PARENT_EXECUTION_ID";
    public const string ChatIdVariable = "MEZHS_CHAT_ID";
    public const string CorrelationIdVariable = "MEZHS_CORRELATION_ID";
    public const string SourceVariable = "MEZHS_SOURCE";
    public const string WorkspaceVariable = "MEZHS_WORKSPACE";
    public const string TriggerMessageIdVariable = "MEZHS_TRIGGER_MESSAGE_ID";
    public const string CommandIndexVariable = "MEZHS_COMMAND_INDEX";

    public static string ResolveStoragePath(string? explicitPath = null)
    {
        var path = explicitPath;
        if (string.IsNullOrWhiteSpace(path))
            path = Environment.GetEnvironmentVariable(StorageVariable);
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(Environment.CurrentDirectory, "data", "executor.sqlite");
        return Path.GetFullPath(path);
    }

    public static IReadOnlyDictionary<string, string> SnapshotCurrent()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry item in Environment.GetEnvironmentVariables())
        {
            if (item.Key is string key && item.Value is string value)
                result[key] = value;
        }
        return result;
    }
}
