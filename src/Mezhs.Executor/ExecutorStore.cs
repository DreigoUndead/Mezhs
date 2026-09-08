using System.Globalization;
using System.Text.Json;
using Mezhs.Sqlite;
using Microsoft.Data.Sqlite;

namespace Mezhs.Executor;

internal sealed record StoredExecution(Execution Execution, string EnvironmentJson);
internal sealed record CreateExecutionResult(Execution Execution, bool Created);
internal sealed record RestartPlan(Execution OldExecution, Execution NewExecution, bool CallerLaunchesReplacement);

internal sealed class ExecutorStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SqliteDatabase _database;

    public ExecutorStore(string path)
    {
        _database = new SqliteDatabase(path);
        _database.Initialize("""
            CREATE TABLE IF NOT EXISTS ExecutorExecutions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Status TEXT NOT NULL,
                Command TEXT NOT NULL,
                Directory TEXT NOT NULL,
                TimeoutSeconds INTEGER NOT NULL,
                OwnerProcessId INTEGER NULL,
                ProcessId INTEGER NULL,
                ExitCode INTEGER NULL,
                Result TEXT NULL,
                Error TEXT NULL,
                CreatedAt TEXT NOT NULL,
                StartedAt TEXT NULL,
                HeartbeatAt TEXT NULL,
                CompletedAt TEXT NULL,
                RestartedFromId INTEGER NULL,
                RestartedAsId INTEGER NULL,
                ChatId TEXT NULL,
                ParentExecutionId TEXT NULL,
                CorrelationId TEXT NULL,
                Source TEXT NULL,
                Workspace TEXT NULL,
                TriggerMessageId TEXT NULL,
                CommandIndex INTEGER NULL,
                EnvironmentJson TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_ExecutorExecutions_ChatId_CreatedAt
                ON ExecutorExecutions(ChatId, CreatedAt DESC);
            CREATE INDEX IF NOT EXISTS IX_ExecutorExecutions_Status
                ON ExecutorExecutions(Status);
            CREATE INDEX IF NOT EXISTS IX_ExecutorExecutions_RestartedFromId
                ON ExecutorExecutions(RestartedFromId);
            CREATE UNIQUE INDEX IF NOT EXISTS UX_ExecutorExecutions_AgentCommand
                ON ExecutorExecutions(ChatId, TriggerMessageId, CommandIndex)
                WHERE RestartedFromId IS NULL
                  AND ChatId IS NOT NULL
                  AND TriggerMessageId IS NOT NULL
                  AND CommandIndex IS NOT NULL;
            """);
    }

    public string Path => _database.Path;

    public CreateExecutionResult Create(
        string commandText,
        string directory,
        int timeoutSeconds,
        IReadOnlyDictionary<string, string> environment)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var context = ReadContext(environment);
        var environmentJson = JsonSerializer.Serialize(environment, Json);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ExecutorExecutions (
                Status, Command, Directory, TimeoutSeconds, CreatedAt,
                ChatId, ParentExecutionId, CorrelationId, Source, Workspace,
                TriggerMessageId, CommandIndex, EnvironmentJson)
            VALUES (
                $status, $command, $directory, $timeoutSeconds, $createdAt,
                $chatId, $parentExecutionId, $correlationId, $source, $workspace,
                $triggerMessageId, $commandIndex, $environmentJson)
            ON CONFLICT DO NOTHING
            RETURNING Id;
            """;
        command.Parameters.AddWithValue("$status", ExecutionStatus.Created.ToString());
        command.Parameters.AddWithValue("$command", commandText);
        command.Parameters.AddWithValue("$directory", directory);
        command.Parameters.AddWithValue("$timeoutSeconds", timeoutSeconds);
        command.Parameters.AddWithValue("$createdAt", Format(createdAt));
        BindContext(command, context);
        command.Parameters.AddWithValue("$environmentJson", environmentJson);
        var inserted = command.ExecuteScalar();
        if (inserted is not null && inserted != DBNull.Value)
        {
            var id = Convert.ToInt32(inserted, CultureInfo.InvariantCulture);
            return new CreateExecutionResult(Get(id)!.Execution, true);
        }

        if (context.ChatId is null || context.TriggerMessageId is null || context.CommandIndex is null)
            throw new InvalidOperationException("Executor execution could not be inserted.");

        using var existing = connection.CreateCommand();
        existing.CommandText = """
            SELECT *
            FROM ExecutorExecutions
            WHERE RestartedFromId IS NULL
              AND ChatId = $chatId
              AND TriggerMessageId = $triggerMessageId
              AND CommandIndex = $commandIndex
            LIMIT 1;
            """;
        existing.Parameters.AddWithValue("$chatId", context.ChatId);
        existing.Parameters.AddWithValue("$triggerMessageId", context.TriggerMessageId);
        existing.Parameters.AddWithValue("$commandIndex", context.CommandIndex.Value);
        using var reader = existing.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("Executor command identity conflicted but the existing execution could not be read.");
        return new CreateExecutionResult(Read(reader).Execution, false);
    }

    public StoredExecution? Get(int id)
    {
        using var connection = _database.Open();
        return Get(connection, null, id);
    }

    public IReadOnlyList<Execution> List(string? chatId, int limit)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = chatId is null
            ? "SELECT * FROM ExecutorExecutions ORDER BY Id DESC LIMIT $limit;"
            : "SELECT * FROM ExecutorExecutions WHERE ChatId = $chatId ORDER BY Id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        if (chatId is not null)
            command.Parameters.AddWithValue("$chatId", chatId);
        using var reader = command.ExecuteReader();
        var result = new List<Execution>();
        while (reader.Read())
            result.Add(Read(reader).Execution);
        return result;
    }

    public StoredExecution? Claim(int id, int ownerProcessId)
    {
        var now = DateTimeOffset.UtcNow;
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ExecutorExecutions
            SET Status = $running,
                OwnerProcessId = $ownerProcessId,
                StartedAt = $now,
                HeartbeatAt = $now
            WHERE Id = $id
              AND Status = $created
            RETURNING *;
            """;
        command.Parameters.AddWithValue("$running", ExecutionStatus.Running.ToString());
        command.Parameters.AddWithValue("$created", ExecutionStatus.Created.ToString());
        command.Parameters.AddWithValue("$ownerProcessId", ownerProcessId);
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public void SetProcessId(int id, int processId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ExecutorExecutions
            SET ProcessId = $processId
            WHERE Id = $id
              AND Status IN ($running, $killRequested);
            """;
        command.Parameters.AddWithValue("$processId", processId);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$running", ExecutionStatus.Running.ToString());
        command.Parameters.AddWithValue("$killRequested", ExecutionStatus.KillRequested.ToString());
        command.ExecuteNonQuery();
    }

    public Execution? HeartbeatAndGet(int id)
    {
        using var connection = _database.Open();
        using (var heartbeat = connection.CreateCommand())
        {
            heartbeat.CommandText = """
                UPDATE ExecutorExecutions
                SET HeartbeatAt = $heartbeatAt
                WHERE Id = $id
                  AND Status IN ($running, $killRequested);
                """;
            heartbeat.Parameters.AddWithValue("$heartbeatAt", Format(DateTimeOffset.UtcNow));
            heartbeat.Parameters.AddWithValue("$id", id);
            heartbeat.Parameters.AddWithValue("$running", ExecutionStatus.Running.ToString());
            heartbeat.Parameters.AddWithValue("$killRequested", ExecutionStatus.KillRequested.ToString());
            heartbeat.ExecuteNonQuery();
        }
        return Get(connection, null, id)?.Execution;
    }

    public Execution? RequestKill(int id)
    {
        var now = DateTimeOffset.UtcNow;
        using var connection = _database.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE ExecutorExecutions
                SET Status = CASE Status
                        WHEN $created THEN $killed
                        WHEN $running THEN $killRequested
                        ELSE Status
                    END,
                    Error = CASE Status
                        WHEN $created THEN $killedBeforeStart
                        WHEN $running THEN $killRequestedError
                        ELSE Error
                    END,
                    CompletedAt = CASE Status
                        WHEN $created THEN $now
                        ELSE CompletedAt
                    END
                WHERE Id = $id
                  AND Status IN ($created, $running);
                """;
            command.Parameters.AddWithValue("$created", ExecutionStatus.Created.ToString());
            command.Parameters.AddWithValue("$running", ExecutionStatus.Running.ToString());
            command.Parameters.AddWithValue("$killed", ExecutionStatus.Killed.ToString());
            command.Parameters.AddWithValue("$killRequested", ExecutionStatus.KillRequested.ToString());
            command.Parameters.AddWithValue("$killedBeforeStart", "Killed before execution started.");
            command.Parameters.AddWithValue("$killRequestedError", "Kill requested.");
            command.Parameters.AddWithValue("$now", Format(now));
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
        return Get(connection, null, id)?.Execution;
    }

    public bool Finish(
        int id,
        ExecutionStatus status,
        int? exitCode,
        string? result,
        string? error)
    {
        if (status is not (ExecutionStatus.Completed or ExecutionStatus.Failed or ExecutionStatus.Killed or ExecutionStatus.TimedOut))
            throw new ArgumentOutOfRangeException(nameof(status));

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ExecutorExecutions
            SET Status = $status,
                ExitCode = $exitCode,
                Result = $result,
                Error = $error,
                HeartbeatAt = $completedAt,
                CompletedAt = $completedAt
            WHERE Id = $id
              AND Status IN ($running, $killRequested);
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$exitCode", Db(exitCode));
        command.Parameters.AddWithValue("$result", Db(result));
        command.Parameters.AddWithValue("$error", Db(error));
        command.Parameters.AddWithValue("$completedAt", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$running", ExecutionStatus.Running.ToString());
        command.Parameters.AddWithValue("$killRequested", ExecutionStatus.KillRequested.ToString());
        return command.ExecuteNonQuery() == 1;
    }

    public void FailCreated(int id, string error)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ExecutorExecutions
            SET Status = $failed,
                Error = $error,
                CompletedAt = $completedAt
            WHERE Id = $id
              AND Status = $created;
            """;
        command.Parameters.AddWithValue("$failed", ExecutionStatus.Failed.ToString());
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$completedAt", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$created", ExecutionStatus.Created.ToString());
        command.ExecuteNonQuery();
    }

    public void ReconcileStale(int id, DateTimeOffset cutoff)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ExecutorExecutions
            SET Status = $dead,
                Error = CASE
                    WHEN Error IS NULL OR Error = '' THEN $error
                    ELSE Error
                END,
                CompletedAt = $completedAt
            WHERE Id = $id
              AND Status IN ($running, $killRequested)
              AND HeartbeatAt IS NOT NULL
              AND HeartbeatAt < $cutoff;
            """;
        command.Parameters.AddWithValue("$dead", ExecutionStatus.Dead.ToString());
        command.Parameters.AddWithValue("$error", "Execution owner heartbeat became stale.");
        command.Parameters.AddWithValue("$completedAt", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$running", ExecutionStatus.Running.ToString());
        command.Parameters.AddWithValue("$killRequested", ExecutionStatus.KillRequested.ToString());
        command.Parameters.AddWithValue("$cutoff", Format(cutoff));
        command.ExecuteNonQuery();
    }

    public RestartPlan PrepareRestart(int id)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow;
        StoredExecution? old;
        using (var reserve = connection.CreateCommand())
        {
            reserve.Transaction = transaction;
            reserve.CommandText = """
                UPDATE ExecutorExecutions
                SET RestartedAsId = -1,
                    Status = CASE Status
                        WHEN $created THEN $killed
                        WHEN $running THEN $killRequested
                        ELSE Status
                    END,
                    Error = CASE Status
                        WHEN $created THEN $createdError
                        WHEN $running THEN $runningError
                        ELSE Error
                    END,
                    CompletedAt = CASE Status
                        WHEN $created THEN $now
                        ELSE CompletedAt
                    END
                WHERE Id = $id
                  AND RestartedAsId IS NULL
                RETURNING *;
                """;
            reserve.Parameters.AddWithValue("$created", ExecutionStatus.Created.ToString());
            reserve.Parameters.AddWithValue("$running", ExecutionStatus.Running.ToString());
            reserve.Parameters.AddWithValue("$killed", ExecutionStatus.Killed.ToString());
            reserve.Parameters.AddWithValue("$killRequested", ExecutionStatus.KillRequested.ToString());
            reserve.Parameters.AddWithValue("$createdError", "Restarted before execution started.");
            reserve.Parameters.AddWithValue("$runningError", "Restart requested.");
            reserve.Parameters.AddWithValue("$now", Format(now));
            reserve.Parameters.AddWithValue("$id", id);
            using var reader = reserve.ExecuteReader();
            old = reader.Read() ? Read(reader) : null;
        }

        if (old is null)
        {
            var existing = Get(connection, transaction, id)
                ?? throw new KeyNotFoundException($"Execution '{id}' was not found.");
            if (existing.Execution.RestartedAsId is not > 0)
                throw new InvalidOperationException($"Execution '{id}' has an incomplete restart lineage.");
            var replacement = Get(connection, transaction, existing.Execution.RestartedAsId.Value)
                ?? throw new InvalidOperationException($"Replacement execution '{existing.Execution.RestartedAsId}' was not found.");
            transaction.Commit();
            return new RestartPlan(existing.Execution, replacement.Execution, CallerLaunchesReplacement: false);
        }

        int newId;
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO ExecutorExecutions (
                    Status, Command, Directory, TimeoutSeconds, CreatedAt,
                    RestartedFromId, ChatId, ParentExecutionId, CorrelationId,
                    Source, Workspace, TriggerMessageId, CommandIndex, EnvironmentJson)
                VALUES (
                    $created, $command, $directory, $timeoutSeconds, $now,
                    $restartedFromId, $chatId, $parentExecutionId, $correlationId,
                    $source, $workspace, $triggerMessageId, $commandIndex, $environmentJson)
                RETURNING Id;
                """;
            var execution = old.Execution;
            insert.Parameters.AddWithValue("$created", ExecutionStatus.Created.ToString());
            insert.Parameters.AddWithValue("$command", execution.Command);
            insert.Parameters.AddWithValue("$directory", execution.Directory);
            insert.Parameters.AddWithValue("$timeoutSeconds", execution.TimeoutSeconds);
            insert.Parameters.AddWithValue("$now", Format(now));
            insert.Parameters.AddWithValue("$restartedFromId", execution.Id);
            insert.Parameters.AddWithValue("$chatId", Db(execution.ChatId));
            insert.Parameters.AddWithValue("$parentExecutionId", Db(execution.ParentExecutionId));
            insert.Parameters.AddWithValue("$correlationId", Db(execution.CorrelationId));
            insert.Parameters.AddWithValue("$source", Db(execution.Source));
            insert.Parameters.AddWithValue("$workspace", Db(execution.Workspace));
            insert.Parameters.AddWithValue("$triggerMessageId", Db(execution.TriggerMessageId));
            insert.Parameters.AddWithValue("$commandIndex", Db(execution.CommandIndex));
            insert.Parameters.AddWithValue("$environmentJson", old.EnvironmentJson);
            newId = Convert.ToInt32(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        using (var link = connection.CreateCommand())
        {
            link.Transaction = transaction;
            link.CommandText = "UPDATE ExecutorExecutions SET RestartedAsId = $newId WHERE Id = $id AND RestartedAsId = -1;";
            link.Parameters.AddWithValue("$newId", newId);
            link.Parameters.AddWithValue("$id", id);
            if (link.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Restart lineage could not be finalized.");
        }

        var newExecution = Get(connection, transaction, newId)!.Execution;
        var oldExecution = Get(connection, transaction, id)!.Execution;
        transaction.Commit();
        var ownerHandoff = oldExecution.Status == ExecutionStatus.KillRequested;
        return new RestartPlan(oldExecution, newExecution, CallerLaunchesReplacement: !ownerHandoff);
    }

    public IReadOnlyDictionary<string, string> DeserializeEnvironment(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json)
        ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static StoredExecution? Get(SqliteConnection connection, SqliteTransaction? transaction, int id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM ExecutorExecutions WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static StoredExecution Read(SqliteDataReader reader)
    {
        var execution = new Execution
        {
            Id = reader.GetInt32(reader.GetOrdinal("Id")),
            Status = Enum.Parse<ExecutionStatus>(reader.GetString(reader.GetOrdinal("Status"))),
            Command = reader.GetString(reader.GetOrdinal("Command")),
            Directory = reader.GetString(reader.GetOrdinal("Directory")),
            TimeoutSeconds = reader.GetInt32(reader.GetOrdinal("TimeoutSeconds")),
            OwnerProcessId = NullableInt(reader, "OwnerProcessId"),
            ProcessId = NullableInt(reader, "ProcessId"),
            ExitCode = NullableInt(reader, "ExitCode"),
            Result = NullableString(reader, "Result"),
            Error = NullableString(reader, "Error"),
            CreatedAt = Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
            StartedAt = NullableDate(reader, "StartedAt"),
            HeartbeatAt = NullableDate(reader, "HeartbeatAt"),
            CompletedAt = NullableDate(reader, "CompletedAt"),
            RestartedFromId = NullableInt(reader, "RestartedFromId"),
            RestartedAsId = NullableInt(reader, "RestartedAsId"),
            ChatId = NullableString(reader, "ChatId"),
            ParentExecutionId = NullableString(reader, "ParentExecutionId"),
            CorrelationId = NullableString(reader, "CorrelationId"),
            Source = NullableString(reader, "Source"),
            Workspace = NullableString(reader, "Workspace"),
            TriggerMessageId = NullableString(reader, "TriggerMessageId"),
            CommandIndex = NullableInt(reader, "CommandIndex")
        };
        return new StoredExecution(execution, reader.GetString(reader.GetOrdinal("EnvironmentJson")));
    }

    private static ExecutionContextValues ReadContext(IReadOnlyDictionary<string, string> environment)
    {
        string? Value(string name) => environment.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
        int? commandIndex = null;
        var commandIndexText = Value(ExecutorEnvironment.CommandIndexVariable);
        if (commandIndexText is not null && int.TryParse(commandIndexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            commandIndex = parsed;
        return new ExecutionContextValues(
            Value(ExecutorEnvironment.ChatIdVariable),
            Value(ExecutorEnvironment.ExecutionIdVariable),
            Value(ExecutorEnvironment.CorrelationIdVariable),
            Value(ExecutorEnvironment.SourceVariable),
            Value(ExecutorEnvironment.WorkspaceVariable),
            Value(ExecutorEnvironment.TriggerMessageIdVariable),
            commandIndex);
    }

    private static void BindContext(SqliteCommand command, ExecutionContextValues context)
    {
        command.Parameters.AddWithValue("$chatId", Db(context.ChatId));
        command.Parameters.AddWithValue("$parentExecutionId", Db(context.ParentExecutionId));
        command.Parameters.AddWithValue("$correlationId", Db(context.CorrelationId));
        command.Parameters.AddWithValue("$source", Db(context.Source));
        command.Parameters.AddWithValue("$workspace", Db(context.Workspace));
        command.Parameters.AddWithValue("$triggerMessageId", Db(context.TriggerMessageId));
        command.Parameters.AddWithValue("$commandIndex", Db(context.CommandIndex));
    }

    private static string? NullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int? NullableInt(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static DateTimeOffset? NullableDate(SqliteDataReader reader, string name)
    {
        var value = NullableString(reader, name);
        return value is null ? null : Parse(value);
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static object Db(string? value) => value is null ? DBNull.Value : value;
    private static object Db(int? value) => value.HasValue ? value.Value : DBNull.Value;

    private sealed record ExecutionContextValues(
        string? ChatId,
        string? ParentExecutionId,
        string? CorrelationId,
        string? Source,
        string? Workspace,
        string? TriggerMessageId,
        int? CommandIndex);
}
