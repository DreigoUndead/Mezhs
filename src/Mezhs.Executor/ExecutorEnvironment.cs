using Mezhs.Console;

namespace Mezhs.Executor;

public static class ExecutorEnvironment
{
    public const string StorageVariable = "MEZHS_EXECUTOR_STORAGE";
    public const string ExecutionIdVariable = MezhsExecutionContext.ExecutionIdVariable;
    public const string ParentExecutionIdVariable = MezhsExecutionContext.ParentExecutionIdVariable;
    public const string ChatIdVariable = MezhsExecutionContext.ChatIdVariable;
    public const string CorrelationIdVariable = MezhsExecutionContext.CorrelationIdVariable;
    public const string SourceVariable = MezhsExecutionContext.SourceVariable;
    public const string WorkspaceVariable = MezhsExecutionContext.WorkspaceVariable;
    public const string TriggerMessageIdVariable = MezhsExecutionContext.TriggerMessageIdVariable;
    public const string CommandIndexVariable = MezhsExecutionContext.CommandIndexVariable;

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

    public static IReadOnlyDictionary<string, string> SnapshotForChildExecution()
    {
        var result = SnapshotCurrent()
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        // These values identify one Agent command block. That identity is deliberately
        // non-transitive: nested/direct Execute calls are new work unless the orchestrator
        // explicitly supplies a fresh command identity.
        result.Remove(TriggerMessageIdVariable);
        result.Remove(CommandIndexVariable);
        return result;
    }
}
