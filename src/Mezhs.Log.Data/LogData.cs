using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mezhs.Log.Shared;

namespace Mezhs.Log.Data;

public sealed class LogData(LogShared shared)
{
    private static readonly Regex HeaderRegex = new("^## (?<id>\\d+) \\| (?<time>.+)$", RegexOptions.Compiled);
    private static readonly Regex NextIdRegex = new("^<!-- mezhs-log-data next-id:(?<id>\\d+) -->$", RegexOptions.Compiled);

    public LogEntry Add(string file, string text, DateTimeOffset? time = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text is required.", nameof(text));

        var path = shared.Resolve(file);
        return Edit(path, document =>
        {
            var entry = new LogEntry(document.NextId, time ?? DateTimeOffset.Now, text.Trim());
            document.Entries.Add(entry);
            document.NextId++;
            return entry;
        });
    }

    public LogEntry? Get(string file, int id) =>
        Read(shared.Resolve(file)).Entries.FirstOrDefault(x => x.Id == id);

    public IReadOnlyList<LogEntry> GetAfter(string file, int id, int limit = 50)
    {
        ValidateLimit(limit);
        return Read(shared.Resolve(file)).Entries
            .Where(x => x.Id > id)
            .OrderBy(x => x.Id)
            .Take(limit)
            .ToArray();
    }

    public IReadOnlyList<LogEntry> GetLast(string file, int limit = 50)
    {
        ValidateLimit(limit);
        return Read(shared.Resolve(file)).Entries
            .OrderByDescending(x => x.Id)
            .Take(limit)
            .ToArray();
    }

    public IReadOnlyList<LogEntry> Search(string file, string query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Search query is required.", nameof(query));
        ValidateLimit(limit);

        var terms = Regex.Matches(query, "[\\p{L}\\p{N}]+")
            .Select(x => x.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (terms.Length == 0)
            throw new ArgumentException("Search query must contain at least one word or number.", nameof(query));

        return Read(shared.Resolve(file)).Entries
            .Where(entry => terms.All(term => entry.Text.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.Id)
            .Take(limit)
            .ToArray();
    }

    public LogEntry Update(string file, int id, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text is required.", nameof(text));

        var path = shared.Resolve(file);
        return Edit(path, document =>
        {
            var index = document.Entries.FindIndex(x => x.Id == id);
            if (index < 0)
                throw new KeyNotFoundException($"Entry {id} was not found.");
            var updated = document.Entries[index] with { Text = text.Trim() };
            document.Entries[index] = updated;
            return updated;
        });
    }

    public bool Delete(string file, int id)
    {
        var path = shared.Resolve(file);
        return Edit(path, document =>
        {
            var index = document.Entries.FindIndex(x => x.Id == id);
            if (index < 0)
                return false;
            document.Entries.RemoveAt(index);
            return true;
        });
    }

    private static void ValidateLimit(int limit)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be greater than zero.");
    }

    private static T Edit<T>(string path, Func<Document, T> edit)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fileLock = AcquireLock(path + ".lock");
        var document = Read(path);
        var result = edit(document);
        Write(path, document);
        return result;
    }

    private static FileStream AcquireLock(string path)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 99)
            {
                Thread.Sleep(50);
            }
        }

        throw new IOException($"Could not acquire log lock '{path}'.");
    }

    private static Document Read(string path)
    {
        if (!File.Exists(path))
            return new Document();

        var lines = File.ReadAllLines(path);
        var nextId = 1;
        var entries = new List<LogEntry>();
        var i = 0;
        if (lines.Length > 0 && NextIdRegex.Match(lines[0]) is { Success: true } nextMatch)
        {
            nextId = int.Parse(nextMatch.Groups["id"].Value, CultureInfo.InvariantCulture);
            i = 1;
        }

        while (i < lines.Length)
        {
            if (!HeaderRegex.Match(lines[i]) is { Success: true } header)
            {
                i++;
                continue;
            }

            var id = int.Parse(header.Groups["id"].Value, CultureInfo.InvariantCulture);
            if (!DateTimeOffset.TryParse(header.Groups["time"].Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time))
                throw new FormatException($"Invalid timestamp for log entry {id}.");
            i++;
            if (i < lines.Length && string.IsNullOrEmpty(lines[i]))
                i++;

            var text = new StringBuilder();
            while (i < lines.Length && !HeaderRegex.IsMatch(lines[i]))
            {
                if (text.Length > 0)
                    text.AppendLine();
                text.Append(lines[i]);
                i++;
            }

            entries.Add(new LogEntry(id, time, text.ToString().TrimEnd()));
            nextId = Math.Max(nextId, id + 1);
        }

        return new Document(nextId, entries);
    }

    private static void Write(string path, Document document)
    {
        var text = new StringBuilder();
        text.Append("<!-- mezhs-log-data next-id:")
            .Append(document.NextId.ToString(CultureInfo.InvariantCulture))
            .AppendLine(" -->");

        foreach (var entry in document.Entries.OrderBy(x => x.Id))
        {
            text.AppendLine();
            text.Append("## ")
                .Append(entry.Id.ToString(CultureInfo.InvariantCulture))
                .Append(" | ")
                .AppendLine(entry.Time.ToString("O", CultureInfo.InvariantCulture));
            text.AppendLine();
            text.AppendLine(entry.Text);
        }

        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, text.ToString());
        File.Move(temporary, path, overwrite: true);
    }

    private sealed class Document
    {
        public Document(int nextId = 1, List<LogEntry>? entries = null)
        {
            NextId = nextId;
            Entries = entries ?? [];
        }

        public int NextId { get; set; }
        public List<LogEntry> Entries { get; }
    }
}
