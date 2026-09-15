using System.Globalization;
using Mezhs.Agent.Configuration;
using Mezhs.Agent.Models;
using Mezhs.Sqlite;

namespace Mezhs.Agent.Persistence;

internal sealed class AgentRecoveryStore(AgentOptions options)
{
    private readonly SqliteDatabase _database = new(options.Storage);

    public AgentRecoveryPlan Recover()
    {
        using var connection = _database.Open();
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

        var pendingCancellations = new List<AgentPendingCancellation>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT ExecutionId, ChatId
                FROM Executions
                WHERE Kind = $agentKind
                  AND ParentExecutionId IS NULL
                  AND Status = $cancelRequested;
                """;
            select.Parameters.AddWithValue("$agentKind", AgentExecutionKind.Agent.ToString());
            select.Parameters.AddWithValue("$cancelRequested", AgentExecutionStatus.CancelRequested.ToString());
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                pendingCancellations.Add(new AgentPendingCancellation(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1)));
            }
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
        return new AgentRecoveryPlan(recovered, pendingCancellations);
    }
}

internal sealed record AgentRecoveryPlan(
    IReadOnlyList<string> ExecutionIds,
    IReadOnlyList<AgentPendingCancellation> PendingCancellations);

internal sealed record AgentPendingCancellation(string ExecutionId, string? ChatId);
