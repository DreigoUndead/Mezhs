using System.Text.Json;
using System.Text.RegularExpressions;
using Mezhs.Console;
using Mezhs.Log.Shared;

namespace Mezhs.Log.Sql;

public sealed class Commands(LogSql log, LogShared shared) : LogCommands(shared)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Command(Description = "List user tables in a SQLite log.")]
    public string Tables(string file)
    {
        var tables = log.GetTables(file);
        return tables.Count == 0 ? "No user tables." : string.Join(Environment.NewLine, tables);
    }

    [Command(Description = "Show a table schema. The table may be omitted when the database has exactly one user table.")]
    public string Struct(string file, string? table = null) =>
        JsonSerializer.Serialize(log.GetStruct(file, table), JsonOptions);

    [Command(Description = "Insert one row. The table may be omitted when the database has exactly one user table.")]
    public string Add(
        string file,
        IReadOnlyDictionary<string, object?> values,
        string? table = null)
    {
        var id = log.Add(file, values, table);
        return $"Inserted row. last_insert_rowid={id}.";
    }

    [Command(Description = "Read rows by equality filters. An omitted filter reads rows without a WHERE clause.")]
    public string Get(
        string file,
        IReadOnlyDictionary<string, object?>? where = null,
        string? table = null,
        int limit = 50)
    {
        var rows = log.Get(file, where, table, limit);
        return rows.Count == 0 ? "No rows." : JsonSerializer.Serialize(rows, JsonOptions);
    }

    [Command(Description = "Update rows matching all supplied equality filters.")]
    public string Update(
        string file,
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyDictionary<string, object?> where,
        string? table = null) =>
        $"Updated {log.Update(file, values, where, table)} row(s).";

    [Command(Description = "Delete rows matching all supplied equality filters.")]
    public string Delete(
        string file,
        IReadOnlyDictionary<string, object?> where,
        string? table = null) =>
        $"Deleted {log.Delete(file, where, table)} row(s).";

    [Command(Description = "Run a SQL query and return rows.")]
    public string Query(string file, string sql)
    {
        var result = log.Query(file, sql);
        return result.Rows.Count == 0 ? "No rows." : JsonSerializer.Serialize(result.Rows, JsonOptions);
    }

    [Command(Description = "Run SQL that does not need row output. Broad destructive statements require force=true.")]
    public string Execute(string file, string sql, bool force = false)
    {
        ValidateExecute(sql, force);
        return $"Affected {log.Execute(file, sql)} row(s).";
    }

    [Command(Description = "Run LogSql self-tests.")]
    public override string Test()
    {
        var root = Path.Combine(Path.GetTempPath(), "mezhs-log-sql-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var testShared = new LogShared(root);
            var testLog = new LogSql(testShared);
            var commands = new Commands(testLog, testShared);

            var version = testLog.Migrate("Farm.sqlite", [
                new SqlMigration(1, "entries", "CREATE TABLE entries (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, count INTEGER NOT NULL);")
            ]);
            Require(version == 1, "Migration version is wrong.");

            var id = testLog.Add("Farm.sqlite", new Dictionary<string, object?>
            {
                ["name"] = "watermelon",
                ["count"] = 5L
            });
            Require(id == 1, "Insert id is wrong.");

            var rows = testLog.Get("Farm.sqlite", new Dictionary<string, object?> { ["name"] = "watermelon" });
            Require(rows.Count == 1 && Convert.ToInt64(rows[0]["count"]) == 5, "Get returned unexpected data.");

            var updated = testLog.Update(
                "Farm.sqlite",
                new Dictionary<string, object?> { ["count"] = 7L },
                new Dictionary<string, object?> { ["id"] = 1L });
            Require(updated == 1, "Update affected the wrong number of rows.");

            var query = testLog.Query("Farm.sqlite", "SELECT count FROM entries WHERE id = 1;");
            Require(query.Rows.Count == 1 && Convert.ToInt64(query.Rows[0]["count"]) == 7, "Query returned unexpected data.");

            File.WriteAllText(testShared.GetNotesPath("Farm.sqlite"), "Keep farm measurements consistent.");
            var add = RunCase(commands, "Add Farm.sqlite {name:\"test value\" count:5}");
            Require(add.ExitCode == 0, $"Add command failed: {add.Error}");
            Require(add.Out.Contains("Inserted row", StringComparison.Ordinal), "Add command returned unexpected output.");
            Require(add.Out.Contains("Keep farm measurements consistent", StringComparison.Ordinal), "Associated notes were not returned.");

            var get = RunCase(commands, "Get Farm.sqlite {name:\"test value\"}");
            Require(get.ExitCode == 0, $"Get command failed: {get.Error}");
            Require(get.Out.Contains("test value", StringComparison.Ordinal), "Object-literal filter did not bind correctly.");

            testLog.Execute("Farm.sqlite", "CREATE TABLE extra (id INTEGER PRIMARY KEY);");
            var ambiguous = RunCase(commands, "Get Farm.sqlite");
            Require(ambiguous.ExitCode != 0 && ambiguous.Error.Contains("multiple user tables", StringComparison.OrdinalIgnoreCase),
                "A multi-table database did not require an explicit table.");

            var explicitTable = RunCase(commands, "Get Farm.sqlite null entries 50");
            Require(explicitTable.ExitCode == 0, $"Explicit-table Get failed: {explicitTable.Error}");
            Require(explicitTable.Out.Contains("watermelon", StringComparison.Ordinal), "Explicit table selection returned unexpected rows.");

            return "PASS: LogSql";
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    private static void ValidateExecute(string sql, bool force)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("SQL is required.", nameof(sql));

        var withoutComments = Regex.Replace(sql, @"--[^\r\n]*|/\*.*?\*/", " ", RegexOptions.Singleline);
        foreach (var statement in withoutComments.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if ((Regex.IsMatch(statement, @"^UPDATE\b", RegexOptions.IgnoreCase) ||
                 Regex.IsMatch(statement, @"^DELETE\b", RegexOptions.IgnoreCase)) &&
                !Regex.IsMatch(statement, @"\bWHERE\b", RegexOptions.IgnoreCase))
            {
                throw new InvalidOperationException("Raw UPDATE/DELETE without WHERE is not allowed.");
            }

            if (!force && Regex.IsMatch(statement, @"^(DROP|ATTACH|DETACH|VACUUM|REINDEX)\b", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("This SQL requires force=true.");
        }
    }

    private static RunResult RunCase(ConsoleApplication application, string command)
    {
        var previousOut = global::System.Console.Out;
        var previousError = global::System.Console.Error;
        var previousExecution = Environment.GetEnvironmentVariable(MezhsExecutionContext.ExecutionIdVariable);
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            global::System.Console.SetOut(output);
            global::System.Console.SetError(error);
            Environment.SetEnvironmentVariable(MezhsExecutionContext.ExecutionIdVariable, "test");
            return new RunResult(application.Run(command), output.ToString(), error.ToString());
        }
        finally
        {
            global::System.Console.SetOut(previousOut);
            global::System.Console.SetError(previousError);
            Environment.SetEnvironmentVariable(MezhsExecutionContext.ExecutionIdVariable, previousExecution);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record RunResult(int ExitCode, string Out, string Error);
}
