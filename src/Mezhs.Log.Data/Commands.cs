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

    private static string Format(IReadOnlyList<LogEntry> entries) =>
        entries.Count == 0 ? "No entries." : string.Join("\n\n", entries.Select(Format));

    private static string Format(LogEntry entry) =>
        $"[{entry.Id}] {entry.Time:O}\n{entry.Text}";
}
