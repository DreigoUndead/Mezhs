using Mezhs.Console;
using Mezhs.Log.Shared;

namespace Mezhs.Log.Data;

public sealed class Commands(LogData log, LogShared shared) : LogCommands(shared)
{
    [Command(Description = "Add a text entry to a log.")]
    public string Add(string file, string text, DateTimeOffset? time = null)
    {
        var entry = log.Add(file, text, time);
        return $"Added entry {entry.Id} at {entry.Time:O}.";
    }

    [Command(Description = "Get one entry by id.")]
    public string Get(string file, int id) =>
        log.Get(file, id) is { } entry ? Format(entry) : $"Entry {id} was not found.";

    [Command(Description = "Get entries with ids greater than the supplied id.")]
    public string GetAfter(string file, int id, int limit = 50) =>
        Format(log.GetAfter(file, id, limit));

    [Command(Description = "Get the newest entries from a log.")]
    public string GetLast(string file, int limit = 50) =>
        Format(log.GetLast(file, limit));

    [Command(Description = "Search all entries and return the newest matching entries.")]
    public string Search(string file, string query, int limit = 20) =>
        Format(log.Search(file, query, limit));

    [Command(Description = "Replace the text of one entry.")]
    public string Update(string file, int id, string text)
    {
        var entry = log.Update(file, id, text);
        return $"Updated entry {entry.Id}.";
    }

    [Command(Description = "Delete one entry by id.")]
    public string Delete(string file, int id) =>
        log.Delete(file, id) ? $"Deleted entry {id}." : $"Entry {id} was not found.";

    [Command(Description = "Run LogData self-tests.")]
    public override string Test()
    {
        var root = Path.Combine(Path.GetTempPath(), "mezhs-log-data-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var testShared = new LogShared(root);
            var testLog = new LogData(testShared);
            var commands = new Commands(testLog, testShared);

            var first = testLog.Add("Watermelons.md", "Planted the last watermelon row in field 2.");
            var second = testLog.Add("Watermelons.md", "Replaced 7 holes in field 2.");
            Require(first.Id == 1 && second.Id == 2, "IDs are not sequential.");
            Require(testLog.Delete("Watermelons.md", 2), "Delete failed.");

            var third = testLog.Add("Watermelons.md", "Replaced another 3 holes in field 2.");
            Require(third.Id == 3, "A deleted ID was reused.");

            var search = testLog.Search("Watermelons.md", "holes field 2", 10);
            Require(search.Count == 1 && search[0].Id == 3, "Search returned unexpected entries.");

            var after = testLog.GetAfter("Watermelons.md", 1, 10);
            Require(after.Count == 1 && after[0].Id == 3, "GetAfter returned unexpected entries.");

            File.WriteAllText(testShared.GetNotesPath("Watermelons.md"), "After logging planting changes, review irrigation needs.");
            var get = RunCase(commands, "Get Watermelons.md 1");
            Require(get.ExitCode == 0, $"Get command failed: {get.Error}");
            Require(get.Out.Contains("Planted the last watermelon row", StringComparison.Ordinal), "Get command lost entry text.");
            Require(get.Out.Contains("review irrigation needs", StringComparison.Ordinal), "Associated notes were not returned.");

            var notes = RunCase(commands, "Notes Watermelons.md");
            Require(notes.ExitCode == 0, $"Notes command failed: {notes.Error}");
            Require(Count(notes.Out, "review irrigation needs") == 1, "Notes command appended notes twice.");

            return "PASS: LogData";
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    private static string Format(IReadOnlyList<LogEntry> entries) =>
        entries.Count == 0 ? "No entries." : string.Join("\n\n", entries.Select(Format));

    private static string Format(LogEntry entry) =>
        $"[{entry.Id}] {entry.Time:O}\n{entry.Text}";

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
