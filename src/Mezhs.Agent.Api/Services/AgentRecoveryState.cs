using System.Globalization;
using Mezhs.Agent.Models;
using Mezhs.Sqlite;

namespace Mezhs.Agent.Services;

public sealed class AgentRecoveryState
{
    private readonly HashSet<string> _executionIds;
    private readonly object _sync = new();

    private AgentRecoveryState(IEnumerable<string> executionIds) =>
        _executionIds = new HashSet<string>(executionIds, StringComparer.OrdinalIgnoreCase);

    public static AgentRecoveryState Prepare(string storagePath)
    {
        var path = Path.GetFullPath(storagePath);
        if (!File.Exists(path))
            return new AgentRecoveryState([]);

        var database = new SqliteDatabase(path);
        using var connection = database.Open();
        if (!SqliteDatabase.TableExists(connection, "Executions"))
            return new AgentRecoveryState([]);

        using var transaction = connection.BeginTransaction();
        var recovered = new List<string>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT ExecutionId
                FROM Executions
                WHERE Kind = $agentKind
                  AND ParentExecutionId IS NULL
                  AND (
                      Status = $running
                      OR (Status = $queued AND StartedAt IS NOT NULL));
                """;
            select.Parameters.AddWithValue("$agentKind", AgentExecutionKind.Agent.ToString());
            select.Parameters.AddWithValue("$running", AgentExecutionStatus.Running.ToString());
            select.Parameters.AddWithValue("$queued", AgentExecutionStatus.Queued.ToString());
            using var reader = select.ExecuteReader();
            while (reader.Read())
                recovered.Add(reader.GetString(0));
        }

        using (var requeue = connection.CreateCommand())
        {
            requeue.Transaction = transaction;
            requeue.CommandText = """
                UPDATE Executions
                SET Status = $queued,
                    Error = NULL,
                    CompletedAt = NULL
                WHERE Kind = $agentKind
                  AND ParentExecutionId IS NULL
                  AND Status = $running;
                """;
            requeue.Parameters.AddWithValue("$agentKind", AgentExecutionKind.Agent.ToString());
            requeue.Parameters.AddWithValue("$running", AgentExecutionStatus.Running.ToString());
            requeue.Parameters.AddWithValue("$queued", AgentExecutionStatus.Queued.ToString());
            requeue.ExecuteNonQuery();
        }

        using (var cancel = connection.CreateCommand())
        {
            cancel.Transaction = transaction;
            cancel.CommandText = """
                UPDATE Executions
                SET Status = $cancelled,
                    Error = $error,
                    CompletedAt = $completedAt
                WHERE Kind = $agentKind
                  AND ParentExecutionId IS NULL
                  AND Status = $cancelRequested;
                """;
            cancel.Parameters.AddWithValue("$agentKind", AgentExecutionKind.Agent.ToString());
            cancel.Parameters.AddWithValue("$cancelRequested", AgentExecutionStatus.CancelRequested.ToString());
            cancel.Parameters.AddWithValue("$cancelled", AgentExecutionStatus.Cancelled.ToString());
            cancel.Parameters.AddWithValue("$error", "Cancellation was pending when MEŽS Agent restarted.");
            cancel.Parameters.AddWithValue("$completedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            cancel.ExecuteNonQuery();
        }

        transaction.Commit();
        return new AgentRecoveryState(recovered);
    }

    public bool TryTake(string executionId)
    {
        lock (_sync)
            return _executionIds.Remove(executionId);
    }
}
