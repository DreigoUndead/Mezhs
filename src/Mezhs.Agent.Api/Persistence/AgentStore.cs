using System.Globalization;
using System.Text.Json;
using Mezhs.Agent.Configuration;
using Mezhs.Agent.Models;
using Mezhs.Sqlite;
using Microsoft.Data.Sqlite;

namespace Mezhs.Agent.Persistence;

public sealed class AgentStore(AgentOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SqliteDatabase _database = new(options.AgentStorage);

    public void Initialize()
    {
        _database.Initialize("""
            CREATE TABLE IF NOT EXISTS AgentChats (
                ChatId TEXT PRIMARY KEY,
                PolicyId TEXT NOT NULL,
                OriginSource TEXT NOT NULL,
                OriginReference TEXT NULL,
                Paused INTEGER NOT NULL DEFAULT 0,
                EnvironmentJson TEXT NOT NULL DEFAULT '{}',
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Executions (
                ExecutionId TEXT PRIMARY KEY,
                ParentExecutionId TEXT NULL,
                CorrelationId TEXT NOT NULL,
                Kind TEXT NOT NULL,
                CommandName TEXT NULL,
                TriggerMessageId TEXT NULL,
                CommandIndex INTEGER NULL,
                ChatId TEXT NULL,
                PolicyId TEXT NOT NULL,
                ConnectionId TEXT NOT NULL,
                Model TEXT NULL,
                Source TEXT NOT NULL,
                SourceReference TEXT NULL,
                Status TEXT NOT NULL,
                Request TEXT NOT NULL,
                EnvironmentJson TEXT NOT NULL DEFAULT '{}',
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
            """);

        using var connection = _database.Open();
        SqliteDatabase.EnsureColumn(connection, "AgentChats", "Paused", "INTEGER NOT NULL DEFAULT 0");
        SqliteDatabase.EnsureColumn(connection, "AgentChats", "EnvironmentJson", "TEXT NOT NULL DEFAULT '{}'");
        SqliteDatabase.EnsureColumn(connection, "Executions", "EnvironmentJson", "TEXT NOT NULL DEFAULT '{}'");
        SqliteDatabase.EnsureColumn(connection, "Executions", "CommandName", "TEXT NULL");
        SqliteDatabase.EnsureColumn(connection, "Executions", "TriggerMessageId", "TEXT NULL");
        SqliteDatabase.EnsureColumn(connection, "Executions", "CommandIndex", "INTEGER NULL");
        SqliteDatabase.EnsureColumn(connection, "Executions", "Model", "TEXT NULL");
        SqliteDatabase.DropColumnIfExists(connection, "Executions", "Requester");

        // Interrupted was a terminal Agent status before restart recovery became resumable.
        // Preserve that historical terminal evidence while upgrading to the current status model.
        using var legacyInterrupted = connection.CreateCommand();
        legacyInterrupted.CommandText = """
            UPDATE Executions
            SET Status = $failed
            WHERE Status = $interrupted;
            """;
        legacyInterrupted.Parameters.AddWithValue("$failed", AgentExecutionStatus.Failed.ToString());
        legacyInterrupted.Parameters.AddWithValue("$interrupted", "Interrupted");
        legacyInterrupted.ExecuteNonQuery();
    }

    public ExecutionRecord? TryCreateRootExecution(
        string policyId,
        string connectionId,
        string? chatId,
        string source,
        string? sourceReference,
        string request,
        IReadOnlyDictionary<string, string> environment,
        string policySnapshot,
        long maxOutstandingExecutions,
        string? model = null)
    {
        if (maxOutstandingExecutions <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxOutstandingExecutions));

        var executionId = AgentIds.New("exec");
        var record = new ExecutionRecord
        {
            ExecutionId = executionId,
            CorrelationId = executionId,
            Kind = AgentExecutionKind.Agent,
            ChatId = chatId,
            PolicyId = policyId,
            ConnectionId = connectionId,
            Model = model,
            Source = source,
            SourceReference = sourceReference,
            Status = AgentExecutionStatus.Queued,
            Request = request,
            Environment = environment,
            PolicySnapshot = policySnapshot
        };
        return InsertExecution(record, maxOutstandingExecutions) ? record : null;
    }

    public ExecutionRecord? GetExecution(string executionId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT *
            FROM Executions
            WHERE ExecutionId = $executionId
              AND Kind = $agentKind;
            """;
        command.Parameters.AddWithValue("$executionId", executionId);
        command.Parameters.AddWithValue("$agentKind", AgentExecutionKind.Agent.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadExecution(reader) : null;
    }

    public IReadOnlyList<ExecutionRecord> GetExecutions(string? chatId = null)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(chatId)
            ? "SELECT * FROM Executions WHERE Kind = $agentKind ORDER BY CreatedAt DESC;"
            : "SELECT * FROM Executions WHERE Kind = $agentKind AND ChatId = $chatId ORDER BY CreatedAt DESC;";
        command.Parameters.AddWithValue("$agentKind", AgentExecutionKind.Agent.ToString());
        if (!string.IsNullOrWhiteSpace(chatId))
            command.Parameters.AddWithValue("$chatId", chatId);
        using var reader = command.ExecuteReader();
        var records = new List<ExecutionRecord>();
        while (reader.Read())
            records.Add(ReadExecution(reader));
        return records;
    }

    public IReadOnlyList<AgentExecutionState> GetExecutionStates(string chatId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ExecutionId, Status, CompletedAt
            FROM Executions
            WHERE Kind = $agentKind
              AND ChatId = $chatId
            ORDER BY CreatedAt DESC, ExecutionId DESC;
            """;
        command.Parameters.AddWithValue("$agentKind", AgentExecutionKind.Agent.ToString());
        command.Parameters.AddWithValue("$chatId", chatId);
        using var reader = command.ExecuteReader();
        var records = new List<AgentExecutionState>();
        while (reader.Read())
        {
            records.Add(new AgentExecutionState(
                reader.GetString(0),
                Enum.Parse<AgentExecutionStatus>(reader.GetString(1)),
                reader.IsDBNull(2) ? null : Parse(reader.GetString(2))));
        }
        return records;
    }

    public string? GetFirstRootExecutionRequest(string chatId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT substr(Request, 1, 200)
            FROM Executions
            WHERE Kind = $agentKind
              AND ChatId = $chatId
              AND ParentExecutionId IS NULL
            ORDER BY CreatedAt, ExecutionId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$agentKind", AgentExecutionKind.Agent.ToString());
        command.Parameters.AddWithValue("$chatId", chatId);
        return command.ExecuteScalar() as string;
    }

    public AgentChatRecord? GetAgentChat(string chatId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM AgentChats WHERE ChatId = $chatId;";
        command.Parameters.AddWithValue("$chatId", chatId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAgentChat(reader) : null;
    }

    public IReadOnlyList<AgentChatRecord> GetAgentChats()
    {
        using var connection = _database.Open();
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
        using var connection = _database.Open();
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
        return GetAgentChat(chatId)!;
    }

    public void ClaimAgentChat(
        string executionId,
        string chatId,
        string policyId,
        string originSource,
        string? originReference,
        IReadOnlyDictionary<string, string> environment)
    {
        var now = DateTimeOffset.UtcNow;
        var environmentJson = SerializeEnvironment(environment);
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO AgentChats (
                    ChatId, PolicyId, OriginSource, OriginReference, Paused, EnvironmentJson, CreatedAt, UpdatedAt)
                VALUES (
                    $chatId, $policyId, $originSource, $originReference, 0, $environmentJson, $createdAt, $updatedAt)
                ON CONFLICT(ChatId) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$chatId", chatId);
            insert.Parameters.AddWithValue("$policyId", policyId);
            insert.Parameters.AddWithValue("$originSource", originSource);
            insert.Parameters.AddWithValue("$originReference", Db(originReference));
            insert.Parameters.AddWithValue("$environmentJson", environmentJson);
            insert.Parameters.AddWithValue("$createdAt", Format(now));
            insert.Parameters.AddWithValue("$updatedAt", Format(now));
            insert.ExecuteNonQuery();
        }

        string existingPolicyId;
        string existingEnvironmentJson;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT PolicyId, EnvironmentJson FROM AgentChats WHERE ChatId = $chatId;";
            select.Parameters.AddWithValue("$chatId", chatId);
            using var reader = select.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException($"Agent chat '{chatId}' could not be claimed.");
            existingPolicyId = reader.GetString(0);
            existingEnvironmentJson = reader.GetString(1);
        }
        EnsurePolicyMatches(chatId, existingPolicyId, policyId);
        if (!EnvironmentEquals(DeserializeEnvironment(existingEnvironmentJson), environment))
            throw new RequestValidationException(
                $"Agent chat '{chatId}' already has a different environment. Environment is fixed when the chat is first claimed.");

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

        using (var attach = connection.CreateCommand())
        {
            attach.Transaction = transaction;
            attach.CommandText = """
                UPDATE Executions
                SET ChatId = $chatId
                WHERE ExecutionId = $executionId
                  AND Kind = $agentKind
                  AND Status IN ($queued, $running);
                """;
            attach.Parameters.AddWithValue("$chatId", chatId);
            attach.Parameters.AddWithValue("$executionId", executionId);
            attach.Parameters.AddWithValue("$agentKind", AgentExecutionKind.Agent.ToString());
            attach.Parameters.AddWithValue("$queued", AgentExecutionStatus.Queued.ToString());
            attach.Parameters.AddWithValue("$running", AgentExecutionStatus.Running.ToString());
            if (attach.ExecuteNonQuery() != 1)
                throw new InvalidOperationException(
                    $"Execution '{executionId}' changed state before Agent chat '{chatId}' could be claimed.");
        }

        transaction.Commit();
    }

    public ExecutionRecord? TryClaimNextQueuedExecution()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Executions
            SET Status = $running,
                StartedAt = $startedAt
            WHERE ExecutionId = (
                SELECT queued.ExecutionId
                FROM Executions queued
                WHERE queued.Kind = $agentKind
                  AND queued.Status = $queued
                  AND NOT EXISTS (
                      SELECT 1
                      FROM AgentChats chat
                      WHERE chat.ChatId = queued.ChatId
                        AND chat.Paused = 1)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM Executions active
                      WHERE active.Kind = $agentKind
                        AND active.Status IN ($running, $cancelRequested)
                        AND active.ChatId = queued.ChatId)
                ORDER BY queued.CreatedAt, queued.ExecutionId
                LIMIT 1)
            RETURNING *;
            """;
        command.Parameters.AddWithValue("$agentKind", AgentExecutionKind.Agent.ToString());
        command.Parameters.AddWithValue("$queued", AgentExecutionStatus.Queued.ToString());
        command.Parameters.AddWithValue("$running", AgentExecutionStatus.Running.ToString());
        command.Parameters.AddWithValue("$cancelRequested", AgentExecutionStatus.CancelRequested.ToString());
        command.Parameters.AddWithValue("$startedAt", Format(DateTimeOffset.UtcNow));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadExecution(reader) : null;
    }

    public bool Complete(string executionId, string? result) =>
        Finish(executionId, AgentExecutionStatus.Completed, result, error: null, AgentExecutionStatus.Running);

    public bool Fail(string executionId, string error) =>
        Finish(
            executionId,
            AgentExecutionStatus.Failed,
            result: null,
            error,
            AgentExecutionStatus.Queued,
            AgentExecutionStatus.Running,
            AgentExecutionStatus.CancelRequested);

    public (ExecutionRecord Record, bool Changed) RequestCancel(string executionId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Executions
            SET Status = CASE Status
                    WHEN $queued THEN $cancelled
                    ELSE $cancelRequested
                END,
                Error = CASE Status
                    WHEN $queued THEN $cancelledError
                    ELSE $requestedError
                END,
                CompletedAt = CASE Status
                    WHEN $queued THEN $completedAt
                    ELSE NULL
                END
            WHERE ExecutionId = $executionId
              AND Kind = $agentKind
              AND Status IN ($queued, $running);
            """;
        command.Parameters.AddWithValue("$agentKind", AgentExecutionKind.Agent.ToString());
        command.Parameters.AddWithValue("$queued", AgentExecutionStatus.Queued.ToString());
        command.Parameters.AddWithValue("$running", AgentExecutionStatus.Running.ToString());
        command.Parameters.AddWithValue("$cancelled", AgentExecutionStatus.Cancelled.ToString());
        command.Parameters.AddWithValue("$cancelRequested", AgentExecutionStatus.CancelRequested.ToString());
        command.Parameters.AddWithValue("$cancelledError", "Cancelled before execution started.");
        command.Parameters.AddWithValue("$requestedError", "Cancellation requested by user.");
        command.Parameters.AddWithValue("$completedAt", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$executionId", executionId);
        var changed = command.ExecuteNonQuery() == 1;
        var record = GetExecution(executionId)
            ?? throw new ResourceNotFoundException($"Execution '{executionId}' was not found.");
        return (record, changed);
    }

    public bool CompleteCancellation(string executionId) =>
        Finish(
            executionId,
            AgentExecutionStatus.Cancelled,
            result: null,
            "Cancelled by user.",
            AgentExecutionStatus.Running,
            AgentExecutionStatus.CancelRequested);

    public AgentRecoveryPlan RecoverAfterRestart()
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
            cancel.Parameters.AddWithValue("$completedAt", Format(DateTimeOffset.UtcNow));
            cancel.ExecuteNonQuery();
        }

        transaction.Commit();
        return new AgentRecoveryPlan(recovered, pendingCancellations);
    }

    private bool InsertExecution(ExecutionRecord record, long maxOutstandingAgentExecutions)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Executions (
                ExecutionId, ParentExecutionId, CorrelationId, Kind, ChatId,
                PolicyId, ConnectionId, Model, Source, SourceReference, Status,
                Request, EnvironmentJson, Result, Error, ExitCode, PolicySnapshot,
                CreatedAt, StartedAt, CompletedAt)
            SELECT
                $executionId, $parentExecutionId, $correlationId, $kind, $chatId,
                $policyId, $connectionId, $model, $source, $sourceReference, $status,
                $request, $environmentJson, NULL, NULL, NULL, $policySnapshot,
                $createdAt, NULL, NULL
            WHERE (
                SELECT COUNT(*)
                FROM Executions
                WHERE Kind = $agentKind
                  AND Status IN ($queued, $running, $cancelRequested)) < $maxOutstanding;
            """;
        BindExecution(command, record);
        command.Parameters.AddWithValue("$maxOutstanding", maxOutstandingAgentExecutions);
        command.Parameters.AddWithValue("$agentKind", AgentExecutionKind.Agent.ToString());
        command.Parameters.AddWithValue("$queued", AgentExecutionStatus.Queued.ToString());
        command.Parameters.AddWithValue("$running", AgentExecutionStatus.Running.ToString());
        command.Parameters.AddWithValue("$cancelRequested", AgentExecutionStatus.CancelRequested.ToString());
        return command.ExecuteNonQuery() == 1;
    }

    private bool Finish(
        string executionId,
        AgentExecutionStatus status,
        string? result,
        string? error,
        params AgentExecutionStatus[] allowedStatuses)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        var allowedParameters = allowedStatuses
            .Select((_, index) => $"$allowed{index}")
            .ToArray();
        command.CommandText = $"""
            UPDATE Executions
            SET Status = $status,
                Result = $result,
                Error = $error,
                CompletedAt = $completedAt
            WHERE ExecutionId = $executionId
              AND Kind = $agentKind
              AND Status IN ({string.Join(", ", allowedParameters)});
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$result", Db(result));
        command.Parameters.AddWithValue("$error", Db(error));
        command.Parameters.AddWithValue("$completedAt", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$executionId", executionId);
        command.Parameters.AddWithValue("$agentKind", AgentExecutionKind.Agent.ToString());
        for (var index = 0; index < allowedStatuses.Length; index++)
            command.Parameters.AddWithValue(allowedParameters[index], allowedStatuses[index].ToString());
        return command.ExecuteNonQuery() == 1;
    }

    private static void EnsurePolicyMatches(string chatId, string existingPolicyId, string requestedPolicyId)
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
        command.Parameters.AddWithValue("$model", Db(record.Model));
        command.Parameters.AddWithValue("$source", record.Source);
        command.Parameters.AddWithValue("$sourceReference", Db(record.SourceReference));
        command.Parameters.AddWithValue("$status", record.Status.ToString());
        command.Parameters.AddWithValue("$request", record.Request);
        command.Parameters.AddWithValue("$environmentJson", SerializeEnvironment(record.Environment));
        command.Parameters.AddWithValue("$policySnapshot", record.PolicySnapshot);
        command.Parameters.AddWithValue("$createdAt", Format(record.CreatedAt));
    }

    private static ExecutionRecord ReadExecution(SqliteDataReader reader) => new()
    {
        ExecutionId = reader.GetString(reader.GetOrdinal("ExecutionId")),
        ParentExecutionId = GetNullableString(reader, "ParentExecutionId"),
        CorrelationId = reader.GetString(reader.GetOrdinal("CorrelationId")),
        Kind = Enum.Parse<AgentExecutionKind>(reader.GetString(reader.GetOrdinal("Kind"))),
        CommandName = GetNullableString(reader, "CommandName"),
        TriggerMessageId = GetNullableString(reader, "TriggerMessageId"),
        CommandIndex = GetNullableInt(reader, "CommandIndex"),
        ChatId = GetNullableString(reader, "ChatId"),
        PolicyId = reader.GetString(reader.GetOrdinal("PolicyId")),
        ConnectionId = reader.GetString(reader.GetOrdinal("ConnectionId")),
        Model = GetNullableString(reader, "Model"),
        Source = reader.GetString(reader.GetOrdinal("Source")),
        SourceReference = GetNullableString(reader, "SourceReference"),
        Status = Enum.Parse<AgentExecutionStatus>(reader.GetString(reader.GetOrdinal("Status"))),
        Request = reader.GetString(reader.GetOrdinal("Request")),
        Environment = DeserializeEnvironment(reader.GetString(reader.GetOrdinal("EnvironmentJson"))),
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
        Environment = DeserializeEnvironment(reader.GetString(reader.GetOrdinal("EnvironmentJson"))),
        CreatedAt = Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
        UpdatedAt = Parse(reader.GetString(reader.GetOrdinal("UpdatedAt")))
    };

    private static string SerializeEnvironment(IReadOnlyDictionary<string, string> environment) =>
        JsonSerializer.Serialize(environment, Json);

    private static IReadOnlyDictionary<string, string> DeserializeEnvironment(string value) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(value, Json)
        ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static bool EnvironmentEquals(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out var value) && string.Equals(pair.Value, value, StringComparison.Ordinal));

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

public sealed record AgentRecoveryPlan(
    IReadOnlyList<string> ExecutionIds,
    IReadOnlyList<AgentPendingCancellation> PendingCancellations);

public sealed record AgentPendingCancellation(string ExecutionId, string? ChatId);

