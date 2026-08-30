using System.Globalization;
using Mezhs.Agent.Configuration;
using Mezhs.Agent.Models;
using Microsoft.Data.Sqlite;

namespace Mezhs.Agent.Persistence;

public sealed class AgentStore(AgentOptions options)
{
    private readonly string _path = options.Storage;
    private readonly object _writeLock = new();

    public void Initialize()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;

            CREATE TABLE IF NOT EXISTS AgentChats (
                ChatId TEXT PRIMARY KEY,
                PolicyId TEXT NOT NULL,
                OriginSource TEXT NOT NULL,
                OriginReference TEXT NULL,
                Paused INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Executions (
                ExecutionId TEXT PRIMARY KEY,
                ParentExecutionId TEXT NULL,
                CorrelationId TEXT NOT NULL,
                Kind TEXT NOT NULL,
                ChatId TEXT NULL,
                PolicyId TEXT NOT NULL,
                ConnectionId TEXT NOT NULL,
                Source TEXT NOT NULL,
                SourceReference TEXT NULL,
                Status TEXT NOT NULL,
                Request TEXT NOT NULL,
                Result TEXT NULL,
                Error TEXT NULL,
                ExitCode INTEGER NULL,
                PolicySnapshot TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                StartedAt TEXT NULL,
                CompletedAt TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_Executions_ChatId_CreatedAt
                ON Executions(ChatId, CreatedAt);
            CREATE INDEX IF NOT EXISTS IX_Executions_CorrelationId_CreatedAt
                ON Executions(CorrelationId, CreatedAt);
            CREATE INDEX IF NOT EXISTS IX_Executions_Status
                ON Executions(Status);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "AgentChats", "Paused", "INTEGER NOT NULL DEFAULT 0");

        lock (_writeLock)
        {
            using var recovery = connection.CreateCommand();
            recovery.CommandText = """
                UPDATE Executions
                SET Status = $interrupted,
                    Error = CASE
                        WHEN Error IS NULL OR Error = '' THEN $error
                        ELSE Error
                    END,
                    CompletedAt = $completedAt
                WHERE Status = $queued OR Status = $running;
                """;
            recovery.Parameters.AddWithValue("$interrupted", AgentExecutionStatus.Interrupted.ToString());
            recovery.Parameters.AddWithValue("$queued", AgentExecutionStatus.Queued.ToString());
            recovery.Parameters.AddWithValue("$running", AgentExecutionStatus.Running.ToString());
            recovery.Parameters.AddWithValue("$error", "MEŽS Agent restarted before this execution completed.");
            recovery.Parameters.AddWithValue("$completedAt", Format(DateTimeOffset.UtcNow));
            recovery.ExecuteNonQuery();
        }
    }

    public ExecutionRecord CreateRootExecution(
        string policyId,
        string connectionId,
        string? chatId,
        string source,
        string? sourceReference,
        string request,
        string policySnapshot)
    {
        var executionId = AgentIds.New("exec");
        var record = new ExecutionRecord
        {
            ExecutionId = executionId,
            CorrelationId = executionId,
            Kind = AgentExecutionKind.Agent,
            ChatId = chatId,
            PolicyId = policyId,
            ConnectionId = connectionId,
            Source = source,
            SourceReference = sourceReference,
            Status = AgentExecutionStatus.Queued,
            Request = request,
            PolicySnapshot = policySnapshot
        };
        InsertExecution(record);
        return record;
    }

    public ExecutionRecord CreateChildExecution(
        ExecutionRecord parent,
        AgentExecutionKind kind,
        string request)
    {
        var record = new ExecutionRecord
        {
            ExecutionId = AgentIds.New("exec"),
            ParentExecutionId = parent.ExecutionId,
            CorrelationId = parent.CorrelationId,
            Kind = kind,
            ChatId = parent.ChatId,
            PolicyId = parent.PolicyId,
            ConnectionId = parent.ConnectionId,
            Source = parent.Source,
            SourceReference = parent.SourceReference,
            Status = AgentExecutionStatus.Queued,
            Request = request,
            PolicySnapshot = parent.PolicySnapshot
        };
        InsertExecution(record);
        return record;
    }

    public ExecutionRecord? GetExecution(string executionId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Executions WHERE ExecutionId = $executionId;";
        command.Parameters.AddWithValue("$executionId", executionId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadExecution(reader) : null;
    }

    public IReadOnlyList<ExecutionRecord> GetExecutions(string? chatId = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(chatId)
            ? "SELECT * FROM Executions ORDER BY CreatedAt DESC;"
            : "SELECT * FROM Executions WHERE ChatId = $chatId ORDER BY CreatedAt DESC;";
        if (!string.IsNullOrWhiteSpace(chatId))
            command.Parameters.AddWithValue("$chatId", chatId);
        using var reader = command.ExecuteReader();
        var records = new List<ExecutionRecord>();
        while (reader.Read())
            records.Add(ReadExecution(reader));
        return records;
    }

    public AgentChatRecord? GetAgentChat(string chatId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM AgentChats WHERE ChatId = $chatId;";
        command.Parameters.AddWithValue("$chatId", chatId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAgentChat(reader) : null;
    }

    public IReadOnlyList<AgentChatRecord> GetAgentChats()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM AgentChats ORDER BY UpdatedAt DESC;";
        using var reader = command.ExecuteReader();
        var records = new List<AgentChatRecord>();
        while (reader.Read())
            records.Add(ReadAgentChat(reader));
        return records;
    }

    public void ValidateAgentChatPolicy(string chatId, string policyId)
    {
        if (GetAgentChat(chatId) is { } existing)
            EnsurePolicyMatches(chatId, existing.PolicyId, policyId);
    }

    public void ValidateAgentChatRunnable(string chatId)
    {
        if (GetAgentChat(chatId) is { Paused: true })
            throw new RequestValidationException(
                $"Agent chat '{chatId}' is paused. Resume it before starting another execution.");
    }

    public AgentChatRecord SetAgentChatPaused(string chatId, bool paused)
    {
        lock (_writeLock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE AgentChats
                SET Paused = $paused,
                    UpdatedAt = $updatedAt
                WHERE ChatId = $chatId;
                """;
            command.Parameters.AddWithValue("$paused", paused ? 1 : 0);
            command.Parameters.AddWithValue("$updatedAt", Format(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$chatId", chatId);
            if (command.ExecuteNonQuery() != 1)
                throw new ResourceNotFoundException($"Agent chat '{chatId}' was not found.");
        }
        return GetAgentChat(chatId)!;
    }

    public void AttachChat(string executionId, string chatId)
    {
        UpdateActive(
            executionId,
            """
            UPDATE Executions
            SET ChatId = $chatId
            WHERE ExecutionId = $executionId
              AND Status IN ($queued, $running);
            """,
            command => command.Parameters.AddWithValue("$chatId", chatId));
    }

    public void ClaimAgentChat(
        string chatId,
        string policyId,
        string originSource,
        string? originReference)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_writeLock)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO AgentChats (
                        ChatId, PolicyId, OriginSource, OriginReference, Paused, CreatedAt, UpdatedAt)
                    VALUES (
                        $chatId, $policyId, $originSource, $originReference, 0, $createdAt, $updatedAt)
                    ON CONFLICT(ChatId) DO NOTHING;
                    """;
                insert.Parameters.AddWithValue("$chatId", chatId);
                insert.Parameters.AddWithValue("$policyId", policyId);
                insert.Parameters.AddWithValue("$originSource", originSource);
                insert.Parameters.AddWithValue("$originReference", Db(originReference));
                insert.Parameters.AddWithValue("$createdAt", Format(now));
                insert.Parameters.AddWithValue("$updatedAt", Format(now));
                insert.ExecuteNonQuery();
            }

            string existingPolicyId;
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT PolicyId FROM AgentChats WHERE ChatId = $chatId;";
                select.Parameters.AddWithValue("$chatId", chatId);
                existingPolicyId = select.ExecuteScalar() as string
                    ?? throw new InvalidOperationException($"Agent chat '{chatId}' could not be claimed.");
            }
            EnsurePolicyMatches(chatId, existingPolicyId, policyId);

            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE AgentChats
                    SET UpdatedAt = $updatedAt
                    WHERE ChatId = $chatId;
                    """;
                update.Parameters.AddWithValue("$updatedAt", Format(now));
                update.Parameters.AddWithValue("$chatId", chatId);
                update.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public bool TryMarkRunning(string executionId)
    {
        lock (_writeLock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE Executions
                SET Status = $running,
                    StartedAt = $startedAt
                WHERE ExecutionId = $executionId
                  AND Status = $queued;
                """;
            command.Parameters.AddWithValue("$running", AgentExecutionStatus.Running.ToString());
            command.Parameters.AddWithValue("$queued", AgentExecutionStatus.Queued.ToString());
            command.Parameters.AddWithValue("$startedAt", Format(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$executionId", executionId);
            return command.ExecuteNonQuery() == 1;
        }
    }

    public void Complete(string executionId, string? result)
    {
        Finish(
            executionId,
            AgentExecutionStatus.Completed,
            result,
            error: null);
    }

    public void CompleteShell(
        string executionId,
        int exitCode,
        string? result)
    {
        var status = exitCode == 0
            ? AgentExecutionStatus.Completed
            : AgentExecutionStatus.Failed;
        var error = exitCode == 0 ? null : $"Shell exited with code {exitCode}.";

        lock (_writeLock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE Executions
                SET Status = $status,
                    Result = $result,
                    Error = $error,
                    ExitCode = $exitCode,
                    CompletedAt = $completedAt
                WHERE ExecutionId = $executionId
                  AND Status = $running;
                """;
            command.Parameters.AddWithValue("$status", status.ToString());
            command.Parameters.AddWithValue("$result", Db(result));
            command.Parameters.AddWithValue("$error", Db(error));
            command.Parameters.AddWithValue("$exitCode", exitCode);
            command.Parameters.AddWithValue("$completedAt", Format(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$executionId", executionId);
            command.Parameters.AddWithValue("$running", AgentExecutionStatus.Running.ToString());
            command.ExecuteNonQuery();
        }
    }

    public void Fail(string executionId, string error)
    {
        Finish(
            executionId,
            AgentExecutionStatus.Failed,
            result: null,
            error,
            includeQueued: true);
    }

    public void Interrupt(string executionId)
    {
        Finish(
            executionId,
            AgentExecutionStatus.Interrupted,
            result: null,
            "MEŽS Agent stopped before this execution completed.",
            includeQueued: true);
    }

    public (ExecutionRecord Record, bool Changed) Cancel(string executionId)
    {
        lock (_writeLock)
        {
            var existing = GetExecution(executionId)
                ?? throw new ResourceNotFoundException($"Execution '{executionId}' was not found.");
            if (existing.Status is not (AgentExecutionStatus.Queued or AgentExecutionStatus.Running))
                return (existing, false);

            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE Executions
                SET Status = $cancelled,
                    Error = $error,
                    CompletedAt = $completedAt
                WHERE ExecutionId = $executionId
                  AND Status IN ($queued, $running);
                """;
            command.Parameters.AddWithValue("$cancelled", AgentExecutionStatus.Cancelled.ToString());
            command.Parameters.AddWithValue("$error", "Cancelled by user.");
            command.Parameters.AddWithValue("$completedAt", Format(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$executionId", executionId);
            command.Parameters.AddWithValue("$queued", AgentExecutionStatus.Queued.ToString());
            command.Parameters.AddWithValue("$running", AgentExecutionStatus.Running.ToString());
            var changed = command.ExecuteNonQuery() == 1;
            return (GetExecution(executionId)!, changed);
        }
    }

    private void InsertExecution(ExecutionRecord record)
    {
        lock (_writeLock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Executions (
                    ExecutionId, ParentExecutionId, CorrelationId, Kind, ChatId,
                    PolicyId, ConnectionId, Source, SourceReference, Status,
                    Request, Result, Error, ExitCode, PolicySnapshot,
                    CreatedAt, StartedAt, CompletedAt)
                VALUES (
                    $executionId, $parentExecutionId, $correlationId, $kind, $chatId,
                    $policyId, $connectionId, $source, $sourceReference, $status,
                    $request, NULL, NULL, NULL, $policySnapshot,
                    $createdAt, NULL, NULL);
                """;
            BindExecution(command, record);
            command.ExecuteNonQuery();
        }
    }

    private void Finish(
        string executionId,
        AgentExecutionStatus status,
        string? result,
        string? error,
        bool includeQueued = false)
    {
        lock (_writeLock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = includeQueued
                ? """
                  UPDATE Executions
                  SET Status = $status,
                      Result = $result,
                      Error = $error,
                      CompletedAt = $completedAt
                  WHERE ExecutionId = $executionId
                    AND Status IN ($queued, $running);
                  """
                : """
                  UPDATE Executions
                  SET Status = $status,
                      Result = $result,
                      Error = $error,
                      CompletedAt = $completedAt
                  WHERE ExecutionId = $executionId
                    AND Status = $running;
                  """;
            command.Parameters.AddWithValue("$status", status.ToString());
            command.Parameters.AddWithValue("$result", Db(result));
            command.Parameters.AddWithValue("$error", Db(error));
            command.Parameters.AddWithValue("$completedAt", Format(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$executionId", executionId);
            command.Parameters.AddWithValue("$queued", AgentExecutionStatus.Queued.ToString());
            command.Parameters.AddWithValue("$running", AgentExecutionStatus.Running.ToString());
            command.ExecuteNonQuery();
        }
    }

    private void UpdateActive(
        string executionId,
        string sql,
        Action<SqliteCommand> bind)
    {
        lock (_writeLock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$executionId", executionId);
            command.Parameters.AddWithValue("$queued", AgentExecutionStatus.Queued.ToString());
            command.Parameters.AddWithValue("$running", AgentExecutionStatus.Running.ToString());
            bind(command);
            command.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void EnsureColumn(
        SqliteConnection connection,
        string table,
        string column,
        string definition)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({table});";
        using var reader = inspect.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        reader.Close();

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    private static void EnsurePolicyMatches(
        string chatId,
        string existingPolicyId,
        string requestedPolicyId)
    {
        if (!string.Equals(existingPolicyId, requestedPolicyId, StringComparison.OrdinalIgnoreCase))
            throw new RequestValidationException(
                $"Agent chat '{chatId}' is already owned by policy '{existingPolicyId}'.");
    }

    private static void BindExecution(SqliteCommand command, ExecutionRecord record)
    {
        command.Parameters.AddWithValue("$executionId", record.ExecutionId);
        command.Parameters.AddWithValue("$parentExecutionId", Db(record.ParentExecutionId));
        command.Parameters.AddWithValue("$correlationId", record.CorrelationId);
        command.Parameters.AddWithValue("$kind", record.Kind.ToString());
        command.Parameters.AddWithValue("$chatId", Db(record.ChatId));
        command.Parameters.AddWithValue("$policyId", record.PolicyId);
        command.Parameters.AddWithValue("$connectionId", record.ConnectionId);
        command.Parameters.AddWithValue("$source", record.Source);
        command.Parameters.AddWithValue("$sourceReference", Db(record.SourceReference));
        command.Parameters.AddWithValue("$status", record.Status.ToString());
        command.Parameters.AddWithValue("$request", record.Request);
        command.Parameters.AddWithValue("$policySnapshot", record.PolicySnapshot);
        command.Parameters.AddWithValue("$createdAt", Format(record.CreatedAt));
    }

    private static ExecutionRecord ReadExecution(SqliteDataReader reader) => new()
    {
        ExecutionId = reader.GetString(reader.GetOrdinal("ExecutionId")),
        ParentExecutionId = GetNullableString(reader, "ParentExecutionId"),
        CorrelationId = reader.GetString(reader.GetOrdinal("CorrelationId")),
        Kind = Enum.Parse<AgentExecutionKind>(reader.GetString(reader.GetOrdinal("Kind"))),
        ChatId = GetNullableString(reader, "ChatId"),
        PolicyId = reader.GetString(reader.GetOrdinal("PolicyId")),
        ConnectionId = reader.GetString(reader.GetOrdinal("ConnectionId")),
        Source = reader.GetString(reader.GetOrdinal("Source")),
        SourceReference = GetNullableString(reader, "SourceReference"),
        Status = Enum.Parse<AgentExecutionStatus>(reader.GetString(reader.GetOrdinal("Status"))),
        Request = reader.GetString(reader.GetOrdinal("Request")),
        Result = GetNullableString(reader, "Result"),
        Error = GetNullableString(reader, "Error"),
        ExitCode = GetNullableInt(reader, "ExitCode"),
        PolicySnapshot = reader.GetString(reader.GetOrdinal("PolicySnapshot")),
        CreatedAt = Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
        StartedAt = GetNullableDateTimeOffset(reader, "StartedAt"),
        CompletedAt = GetNullableDateTimeOffset(reader, "CompletedAt")
    };

    private static AgentChatRecord ReadAgentChat(SqliteDataReader reader) => new()
    {
        ChatId = reader.GetString(reader.GetOrdinal("ChatId")),
        PolicyId = reader.GetString(reader.GetOrdinal("PolicyId")),
        OriginSource = reader.GetString(reader.GetOrdinal("OriginSource")),
        OriginReference = GetNullableString(reader, "OriginReference"),
        Paused = reader.GetInt64(reader.GetOrdinal("Paused")) != 0,
        CreatedAt = Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
        UpdatedAt = Parse(reader.GetString(reader.GetOrdinal("UpdatedAt")))
    };

    private static string? GetNullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int? GetNullableInt(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static DateTimeOffset? GetNullableDateTimeOffset(SqliteDataReader reader, string name)
    {
        var value = GetNullableString(reader, name);
        return value is null ? null : Parse(value);
    }

    private static string Format(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static object Db(string? value) => value is null ? DBNull.Value : value;
}
