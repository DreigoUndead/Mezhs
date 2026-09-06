using System.Globalization;
using System.Text.Json;
using Mezhs.Agent.Configuration;
using Mezhs.Agent.Models;
using Microsoft.Data.Sqlite;

namespace Mezhs.Agent.Persistence;

public sealed record ExecutionStoreMetrics(
    int TotalExecutions,
    int Failures,
    int ShellFailures,
    double AverageDurationMilliseconds);

public sealed class AgentStore(AgentOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path = options.Storage;

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
                Source TEXT NOT NULL,
                SourceReference TEXT NULL,
                Requester TEXT NOT NULL,
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
            CREATE INDEX IF NOT EXISTS IX_Executions_TriggerMessageId_CommandIndex
                ON Executions(TriggerMessageId, CommandIndex);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "AgentChats", "Paused", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "AgentChats", "EnvironmentJson", "TEXT NOT NULL DEFAULT '{}'");
        EnsureColumn(connection, "Executions", "EnvironmentJson", "TEXT NOT NULL DEFAULT '{}'");
        EnsureColumn(connection, "Executions", "Requester", "TEXT NOT NULL DEFAULT 'legacy'");
        EnsureColumn(connection, "Executions", "CommandName", "TEXT NULL");
        EnsureColumn(connection, "Executions", "TriggerMessageId", "TEXT NULL");
        EnsureColumn(connection, "Executions", "CommandIndex", "INTEGER NULL");

        using var recovery = connection.CreateCommand();
        recovery.CommandText = """
            UPDATE Executions
            SET Status = $interrupted,
                Error = CASE
                    WHEN Error IS NULL OR Error = '' THEN $error
                    ELSE Error
                END,
                CompletedAt = $completedAt
            WHERE Status IN ($queued, $running, $cancelRequested);
            """;
        recovery.Parameters.AddWithValue("$interrupted", AgentExecutionStatus.Interrupted.ToString());
        recovery.Parameters.AddWithValue("$queued", AgentExecutionStatus.Queued.ToString());
        recovery.Parameters.AddWithValue("$running", AgentExecutionStatus.Running.ToString());
        recovery.Parameters.AddWithValue("$cancelRequested", AgentExecutionStatus.CancelRequested.ToString());
        recovery.Parameters.AddWithValue("$error", "MEŽS Agent restarted before this execution completed.");
        recovery.Parameters.AddWithValue("$completedAt", Format(DateTimeOffset.UtcNow));
        recovery.ExecuteNonQuery();
    }

    public ExecutionRecord CreateRootExecution(
        string policyId,
        string connectionId,
        string? chatId,
        string source,
        string? sourceReference,
        string requester,
        string request,
        IReadOnlyDictionary<string, string> environment,
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
            Requester = requester,
            Status = AgentExecutionStatus.Queued,
            Request = request,
            Environment = environment,
            PolicySnapshot = policySnapshot
        };
        InsertExecution(record);
        return record;
    }

    public ExecutionRecord CreateChildExecution(
        ExecutionRecord parent,
        AgentExecutionKind kind,
        string commandName,
        string request,
        string triggerMessageId,
        int commandIndex)
    {
        var record = new ExecutionRecord
        {
            ExecutionId = AgentIds.New("exec"),
            ParentExecutionId = parent.ExecutionId,
            CorrelationId = parent.CorrelationId,
            Kind = kind,
            CommandName = commandName,
            TriggerMessageId = triggerMessageId,
            CommandIndex = commandIndex,
            ChatId = parent.ChatId,
            PolicyId = parent.PolicyId,
            ConnectionId = parent.ConnectionId,
            Source = parent.Source,
            SourceReference = parent.SourceReference,
            Requester = parent.Requester,
            Status = AgentExecutionStatus.Queued,
            Request = request,
            Environment = parent.Environment,
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

    public ExecutionStoreMetrics GetMetrics()
    {
        var executions = GetExecutions();
        var durations = executions
            .Where(record => record.StartedAt.HasValue && record.CompletedAt.HasValue)
            .Select(record => (record.CompletedAt!.Value - record.StartedAt!.Value).TotalMilliseconds)
            .ToArray();
        return new ExecutionStoreMetrics(
            executions.Count,
            executions.Count(record => record.Status == AgentExecutionStatus.Failed),
            executions.Count(record => record.Kind == AgentExecutionKind.Shell && record.Status == AgentExecutionStatus.Failed),
            durations.Length == 0 ? 0 : durations.Average());
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
        string? originReference,
        IReadOnlyDictionary<string, string> environment)
    {
        var now = DateTimeOffset.UtcNow;
        var environmentJson = SerializeEnvironment(environment);
        using var connection = Open();
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

        transaction.Commit();
    }

    public bool TryMarkRunning(string executionId)
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

    public bool Complete(string executionId, string? result) =>
        Finish(executionId, AgentExecutionStatus.Completed, result, error: null, AgentExecutionStatus.Running);

    public bool CompleteShell(string executionId, int exitCode, string? result)
    {
        var status = exitCode == 0 ? AgentExecutionStatus.Completed : AgentExecutionStatus.Failed;
        var error = exitCode == 0 ? null : $"Shell exited with code {exitCode}.";

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
        return command.ExecuteNonQuery() == 1;
    }

    public bool Fail(string executionId, string error) =>
        Finish(
            executionId,
            AgentExecutionStatus.Failed,
            result: null,
            error,
            AgentExecutionStatus.Queued,
            AgentExecutionStatus.Running);

    public bool Interrupt(string executionId) =>
        Finish(
            executionId,
            AgentExecutionStatus.Interrupted,
            result: null,
            "MEŽS Agent stopped before this execution completed.",
            AgentExecutionStatus.Queued,
            AgentExecutionStatus.Running,
            AgentExecutionStatus.CancelRequested);

    public (ExecutionRecord Record, bool Changed) RequestCancel(string executionId)
    {
        using var connection = Open();
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
              AND Status IN ($queued, $running);
            """;
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

    private void InsertExecution(ExecutionRecord record)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Executions (
                ExecutionId, ParentExecutionId, CorrelationId, Kind,
                CommandName, TriggerMessageId, CommandIndex, ChatId,
                PolicyId, ConnectionId, Source, SourceReference, Requester, Status,
                Request, EnvironmentJson, Result, Error, ExitCode, PolicySnapshot,
                CreatedAt, StartedAt, CompletedAt)
            VALUES (
                $executionId, $parentExecutionId, $correlationId, $kind,
                $commandName, $triggerMessageId, $commandIndex, $chatId,
                $policyId, $connectionId, $source, $sourceReference, $requester, $status,
                $request, $environmentJson, NULL, NULL, NULL, $policySnapshot,
                $createdAt, NULL, NULL);
            """;
        BindExecution(command, record);
        command.ExecuteNonQuery();
    }

    private bool Finish(
        string executionId,
        AgentExecutionStatus status,
        string? result,
        string? error,
        params AgentExecutionStatus[] allowedStatuses)
    {
        using var connection = Open();
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
              AND Status IN ({string.Join(", ", allowedParameters)});
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$result", Db(result));
        command.Parameters.AddWithValue("$error", Db(error));
        command.Parameters.AddWithValue("$completedAt", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$executionId", executionId);
        for (var index = 0; index < allowedStatuses.Length; index++)
            command.Parameters.AddWithValue(allowedParameters[index], allowedStatuses[index].ToString());
        return command.ExecuteNonQuery() == 1;
    }

    private void UpdateActive(string executionId, string sql, Action<SqliteCommand> bind)
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

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
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
        command.Parameters.AddWithValue("$commandName", Db(record.CommandName));
        command.Parameters.AddWithValue("$triggerMessageId", Db(record.TriggerMessageId));
        command.Parameters.AddWithValue("$commandIndex", Db(record.CommandIndex));
        command.Parameters.AddWithValue("$chatId", Db(record.ChatId));
        command.Parameters.AddWithValue("$policyId", record.PolicyId);
        command.Parameters.AddWithValue("$connectionId", record.ConnectionId);
        command.Parameters.AddWithValue("$source", record.Source);
        command.Parameters.AddWithValue("$sourceReference", Db(record.SourceReference));
        command.Parameters.AddWithValue("$requester", record.Requester);
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
        Source = reader.GetString(reader.GetOrdinal("Source")),
        SourceReference = GetNullableString(reader, "SourceReference"),
        Requester = reader.GetString(reader.GetOrdinal("Requester")),
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
    private static object Db(int? value) => value.HasValue ? value.Value : DBNull.Value;
}
