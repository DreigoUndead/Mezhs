using System.Globalization;
using Microsoft.Data.Sqlite;
using Mezhs.Log.Shared;

namespace Mezhs.Log.Sql;

public sealed class LogSql(LogShared shared)
{
    private const string MigrationTable = "__MezhsMigrations";

    public SqliteConnection Open(string file)
    {
        var path = shared.Resolve(file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString());
        connection.Open();
        ExecutePragma(connection, "PRAGMA foreign_keys = ON;");
        ExecutePragma(connection, "PRAGMA busy_timeout = 5000;");
        ExecutePragma(connection, "PRAGMA journal_mode = WAL;");
        return connection;
    }

    public IReadOnlyList<string> GetTables(string file)
    {
        using var connection = Open(file);
        return GetTables(connection);
    }

    public SqlTable GetStruct(string file, string? table = null)
    {
        using var connection = Open(file);
        var tableName = ResolveTable(connection, table);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info($table) ORDER BY cid;";
        command.Parameters.AddWithValue("$table", tableName);
        using var reader = command.ExecuteReader();
        var columns = new List<SqlColumn>();
        while (reader.Read())
        {
            columns.Add(new SqlColumn(
                reader.GetString(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.GetInt64(2) != 0,
                reader.IsDBNull(3) ? null : reader.GetValue(3),
                checked((int)reader.GetInt64(4))));
        }
        return new SqlTable(tableName, columns);
    }

    public long Add(
        string file,
        IReadOnlyDictionary<string, object?> values,
        string? table = null)
    {
        if (values.Count == 0)
            throw new ArgumentException("At least one value is required.", nameof(values));

        using var connection = Open(file);
        var tableName = ResolveTable(connection, table);
        using var command = connection.CreateCommand();
        var columns = values.Keys.ToArray();
        var parameterNames = columns.Select((_, i) => $"$v{i}").ToArray();
        command.CommandText = $"INSERT INTO {QuoteIdentifier(tableName)} ({string.Join(", ", columns.Select(QuoteIdentifier))}) VALUES ({string.Join(", ", parameterNames)});";
        for (var i = 0; i < columns.Length; i++)
            command.Parameters.AddWithValue(parameterNames[i], DbValue(values[columns[i]]));
        command.ExecuteNonQuery();

        using var idCommand = connection.CreateCommand();
        idCommand.CommandText = "SELECT last_insert_rowid();";
        return Convert.ToInt64(idCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Get(
        string file,
        IReadOnlyDictionary<string, object?>? where = null,
        string? table = null,
        int limit = 50)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be greater than zero.");

        using var connection = Open(file);
        var tableName = ResolveTable(connection, table);
        using var command = connection.CreateCommand();
        var whereSql = BuildWhere(command, where, "w");
        command.CommandText = $"SELECT * FROM {QuoteIdentifier(tableName)}{whereSql} LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        return ReadRows(command);
    }

    public int Update(
        string file,
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyDictionary<string, object?> where,
        string? table = null)
    {
        if (values.Count == 0)
            throw new ArgumentException("At least one value is required.", nameof(values));
        if (where.Count == 0)
            throw new ArgumentException("Update requires at least one where value.", nameof(where));

        using var connection = Open(file);
        var tableName = ResolveTable(connection, table);
        using var command = connection.CreateCommand();
        var setters = new List<string>();
        var index = 0;
        foreach (var pair in values)
        {
            var parameter = $"$v{index++}";
            setters.Add($"{QuoteIdentifier(pair.Key)} = {parameter}");
            command.Parameters.AddWithValue(parameter, DbValue(pair.Value));
        }
        var whereSql = BuildWhere(command, where, "w");
        command.CommandText = $"UPDATE {QuoteIdentifier(tableName)} SET {string.Join(", ", setters)}{whereSql};";
        return command.ExecuteNonQuery();
    }

    public int Delete(
        string file,
        IReadOnlyDictionary<string, object?> where,
        string? table = null)
    {
        if (where.Count == 0)
            throw new ArgumentException("Delete requires at least one where value.", nameof(where));

        using var connection = Open(file);
        var tableName = ResolveTable(connection, table);
        using var command = connection.CreateCommand();
        var whereSql = BuildWhere(command, where, "w");
        command.CommandText = $"DELETE FROM {QuoteIdentifier(tableName)}{whereSql};";
        return command.ExecuteNonQuery();
    }

    public SqlResult Query(
        string file,
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        using var connection = Open(file);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        using var reader = command.ExecuteReader();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return new SqlResult(columns, rows);
    }

    public int Execute(
        string file,
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        using var connection = Open(file);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        return command.ExecuteNonQuery();
    }

    public int Migrate(string file, IReadOnlyList<SqlMigration> migrations)
    {
        var ordered = migrations.OrderBy(x => x.Version).ToArray();
        if (ordered.Any(x => x.Version <= 0))
            throw new ArgumentException("Migration versions must be greater than zero.", nameof(migrations));
        if (ordered.GroupBy(x => x.Version).Any(x => x.Count() > 1))
            throw new ArgumentException("Migration versions must be unique.", nameof(migrations));

        using var connection = Open(file);
        using (var create = connection.CreateCommand())
        {
            create.CommandText = $"CREATE TABLE IF NOT EXISTS {QuoteIdentifier(MigrationTable)} (Version INTEGER PRIMARY KEY, Name TEXT NOT NULL, AppliedAt TEXT NOT NULL);";
            create.ExecuteNonQuery();
        }

        var current = 0;
        using (var version = connection.CreateCommand())
        {
            version.CommandText = $"SELECT COALESCE(MAX(Version), 0) FROM {QuoteIdentifier(MigrationTable)};";
            current = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        foreach (var migration in ordered.Where(x => x.Version > current))
        {
            using var transaction = connection.BeginTransaction();
            using (var apply = connection.CreateCommand())
            {
                apply.Transaction = transaction;
                apply.CommandText = migration.Sql;
                apply.ExecuteNonQuery();
            }
            using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = $"INSERT INTO {QuoteIdentifier(MigrationTable)} (Version, Name, AppliedAt) VALUES ($version, $name, $appliedAt);";
                record.Parameters.AddWithValue("$version", migration.Version);
                record.Parameters.AddWithValue("$name", migration.Name);
                record.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                record.ExecuteNonQuery();
            }
            transaction.Commit();
            current = migration.Version;
        }

        return current;
    }

    public T Transaction<T>(string file, Func<SqliteConnection, SqliteTransaction, T> action)
    {
        using var connection = Open(file);
        using var transaction = connection.BeginTransaction();
        var result = action(connection, transaction);
        transaction.Commit();
        return result;
    }

    public void Transaction(string file, Action<SqliteConnection, SqliteTransaction> action) =>
        Transaction(file, (connection, transaction) =>
        {
            action(connection, transaction);
            return 0;
        });

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadRows(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static string BuildWhere(
        SqliteCommand command,
        IReadOnlyDictionary<string, object?>? where,
        string prefix)
    {
        if (where is null || where.Count == 0)
            return "";

        var parts = new List<string>();
        var index = 0;
        foreach (var pair in where)
        {
            if (pair.Value is null)
            {
                parts.Add($"{QuoteIdentifier(pair.Key)} IS NULL");
                continue;
            }

            var parameter = $"${prefix}{index++}";
            parts.Add($"{QuoteIdentifier(pair.Key)} = {parameter}");
            command.Parameters.AddWithValue(parameter, DbValue(pair.Value));
        }
        return " WHERE " + string.Join(" AND ", parts);
    }

    private static void AddParameters(SqliteCommand command, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (parameters is null)
            return;
        foreach (var pair in parameters)
        {
            var name = pair.Key.StartsWith('@') || pair.Key.StartsWith('$') || pair.Key.StartsWith(':')
                ? pair.Key
                : "$" + pair.Key;
            command.Parameters.AddWithValue(name, DbValue(pair.Value));
        }
    }

    private static object DbValue(object? value) => value switch
    {
        null => DBNull.Value,
        DateTimeOffset time => time.ToString("O", CultureInfo.InvariantCulture),
        DateTime time => time.ToString("O", CultureInfo.InvariantCulture),
        Enum item => item.ToString(),
        _ => value
    };

    private static IReadOnlyList<string> GetTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> $migrations ORDER BY name;";
        command.Parameters.AddWithValue("$migrations", MigrationTable);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    private static string ResolveTable(SqliteConnection connection, string? table)
    {
        var tables = GetTables(connection);
        if (!string.IsNullOrWhiteSpace(table))
            return tables.FirstOrDefault(x => x.Equals(table, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Table '{table}' was not found.", nameof(table));
        return tables.Count switch
        {
            0 => throw new InvalidOperationException("Database has no user tables."),
            1 => tables[0],
            _ => throw new InvalidOperationException($"Database has multiple user tables ({string.Join(", ", tables)}). Specify a table.")
        };
    }

    private static string QuoteIdentifier(string value) =>
        $"\"{value.Replace("\"", "\"\"")}\"";

    private static void ExecutePragma(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
