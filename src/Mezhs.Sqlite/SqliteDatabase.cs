using Microsoft.Data.Sqlite;

namespace Mezhs.Sqlite;

public sealed class SqliteDatabase
{
    private readonly int _busyTimeoutMilliseconds;

    public SqliteDatabase(string path, int busyTimeoutMilliseconds = 5000)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("SQLite path is required.", nameof(path));
        if (busyTimeoutMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(busyTimeoutMilliseconds));

        Path = System.IO.Path.GetFullPath(path);
        _busyTimeoutMilliseconds = busyTimeoutMilliseconds;
    }

    public string Path { get; }

    public void Initialize(string schemaSql)
    {
        ArgumentNullException.ThrowIfNull(schemaSql);
        using var connection = Open();
        using var schema = connection.CreateCommand();
        schema.CommandText = schemaSql;
        schema.ExecuteNonQuery();
    }

    public SqliteConnection Open()
    {
        EnsureDirectory();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = {_busyTimeoutMilliseconds};
            PRAGMA journal_mode = WAL;
            """;
        command.ExecuteNonQuery();
        return connection;
    }

    public static bool TableExists(SqliteConnection connection, string table)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ValidateIdentifier(table, nameof(table));

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $table;";
        command.Parameters.AddWithValue("$table", table);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    public static void EnsureColumn(
        SqliteConnection connection,
        string table,
        string column,
        string definition)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ValidateIdentifier(table, nameof(table));
        ValidateIdentifier(column, nameof(column));
        if (string.IsNullOrWhiteSpace(definition))
            throw new ArgumentException("Column definition is required.", nameof(definition));
        if (ColumnExists(connection, table, column))
            return;

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    public static void DropColumnIfExists(
        SqliteConnection connection,
        string table,
        string column)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ValidateIdentifier(table, nameof(table));
        ValidateIdentifier(column, nameof(column));
        if (!ColumnExists(connection, table, column))
            return;

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} DROP COLUMN {column};";
        alter.ExecuteNonQuery();
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({table});";
        using var reader = inspect.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void EnsureDirectory()
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => !char.IsLetterOrDigit(character) && character != '_'))
            throw new ArgumentException("SQLite identifier may contain only letters, digits and underscore.", parameterName);
    }
}
