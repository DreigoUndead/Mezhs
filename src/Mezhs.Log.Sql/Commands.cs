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
}
