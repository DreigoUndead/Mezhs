using Mezhs.Console;
using Mezhs.Log.Shared;
using DataLog = Mezhs.Log.Data.LogData;
using DataCommands = Mezhs.Log.Data.Commands;
using SqlLog = Mezhs.Log.Sql.LogSql;
using SqlCommands = Mezhs.Log.Sql.Commands;
using SqlMigration = Mezhs.Log.Sql.SqlMigration;

return new TestApplication().Run();

internal sealed class TestApplication : ConsoleApplication
{
    [Command(Description = "Run log library and command regression tests.")]
    public string Test()
    {
        var root = Path.Combine(Path.GetTempPath(), "mezhs-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var shared = new LogShared(root);
            var data = new DataLog(shared);
            var sql = new SqlLog(shared);

            TestDataLibrary(shared, data);
            TestDataCommands(shared, data);
            TestSqlLibrary(sql);
            TestSqlCommands(shared, sql);

            return "PASS: log regression suite";
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    private static void TestDataLibrary(LogShared shared, DataLog data)
    {
        var first = data.Add("Watermelons.md", "Planted the last watermelon row in field 2.");
        var second = data.Add("Watermelons.md", "Replaced 7 holes in field 2.");
        if (first.Id != 1 || second.Id != 2)
            throw new InvalidOperationException("LogData IDs are not sequential.");
        if (!data.Delete("Watermelons.md", 2))
            throw new InvalidOperationException("LogData delete failed.");
        var third = data.Add("Watermelons.md", "Replaced another 3 holes in field 2.");
        if (third.Id != 3)
            throw new InvalidOperationException("LogData reused a deleted ID.");

        var search = data.Search("Watermelons.md", "holes field 2", 10);
        if (search.Count != 1 || search[0].Id != 3)
            throw new InvalidOperationException("LogData search returned unexpected entries.");

        var after = data.GetAfter("Watermelons.md", 1, 10);
        if (after.Count != 1 || after[0].Id != 3)
            throw new InvalidOperationException("LogData GetAfter returned unexpected entries.");

        File.WriteAllText(shared.GetNotesPath("Watermelons.md"), "After logging planting changes, review irrigation needs.");
    }

    private static void TestDataCommands(LogShared shared, DataLog data)
    {
        var commands = new DataCommands(data, shared);
        var result = RunCase(commands, "Get Watermelons.md 1");
        ExpectSuccess(result);
        ExpectContains(result.Out, "Planted the last watermelon row");
        ExpectContains(result.Out, "Notes:");
        ExpectContains(result.Out, "review irrigation needs");

        var notes = RunCase(commands, "Notes Watermelons.md");
        ExpectSuccess(notes);
        if (Count(notes.Out, "review irrigation needs") != 1)
            throw new InvalidOperationException("Notes command appended notes twice.");
    }

    private static void TestSqlLibrary(SqlLog sql)
    {
        var version = sql.Migrate("Farm.sqlite", [
            new SqlMigration(1, "entries", "CREATE TABLE entries (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, count INTEGER NOT NULL);")
        ]);
        if (version != 1)
            throw new InvalidOperationException("LogSql migration version is wrong.");

        var id = sql.Add("Farm.sqlite", new Dictionary<string, object?>
        {
            ["name"] = "watermelon",
            ["count"] = 5L
        });
        if (id != 1)
            throw new InvalidOperationException("LogSql insert id is wrong.");

        var rows = sql.Get("Farm.sqlite", new Dictionary<string, object?> { ["name"] = "watermelon" });
        if (rows.Count != 1 || Convert.ToInt64(rows[0]["count"]) != 5)
            throw new InvalidOperationException("LogSql Get returned unexpected data.");

        var updated = sql.Update(
            "Farm.sqlite",
            new Dictionary<string, object?> { ["count"] = 7L },
            new Dictionary<string, object?> { ["id"] = 1L });
        if (updated != 1)
            throw new InvalidOperationException("LogSql Update affected the wrong number of rows.");

        var query = sql.Query("Farm.sqlite", "SELECT count FROM entries WHERE id = 1;");
        if (query.Rows.Count != 1 || Convert.ToInt64(query.Rows[0]["count"]) != 7)
            throw new InvalidOperationException("LogSql Query returned unexpected data.");
    }

    private static void TestSqlCommands(LogShared shared, SqlLog sql)
    {
        File.WriteAllText(shared.GetNotesPath("Farm.sqlite"), "Keep farm measurements consistent.");
        var commands = new SqlCommands(sql, shared);

        var add = RunCase(commands, "Add Farm.sqlite {name:\"test value\" count:5}");
        ExpectSuccess(add);
        ExpectContains(add.Out, "Inserted row");
        ExpectContains(add.Out, "Keep farm measurements consistent");

        var get = RunCase(commands, "Get Farm.sqlite {name:\"test value\"}");
        ExpectSuccess(get);
        ExpectContains(get.Out, "test value");

        sql.Execute("Farm.sqlite", "CREATE TABLE extra (id INTEGER PRIMARY KEY);");
        var ambiguous = RunCase(commands, "Get Farm.sqlite");
        if (ambiguous.ExitCode == 0 || !ambiguous.Error.Contains("multiple user tables", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("LogSql did not require a table for a multi-table database.");

        var explicitTable = RunCase(commands, "Get Farm.sqlite null entries 50");
        ExpectSuccess(explicitTable);
        ExpectContains(explicitTable.Out, "watermelon");
    }

    private static RunResult RunCase(ConsoleApplication application, string command)
    {
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var previousExecution = Environment.GetEnvironmentVariable(MezhsExecutionContext.ExecutionIdVariable);
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            Environment.SetEnvironmentVariable(MezhsExecutionContext.ExecutionIdVariable, "test");
            return new RunResult(application.Run(command), output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable(MezhsExecutionContext.ExecutionIdVariable, previousExecution);
        }
    }

    private static void ExpectSuccess(RunResult result)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Command failed with {result.ExitCode}: {result.Error}");
    }

    private static void ExpectContains(string text, string expected)
    {
        if (!text.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected '{expected}' in '{text}'.");
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var position = 0;
        while ((position = text.IndexOf(value, position, StringComparison.Ordinal)) >= 0)
        {
            count++;
            position += value.Length;
        }
        return count;
    }

    private sealed record RunResult(int ExitCode, string Out, string Error);
}
