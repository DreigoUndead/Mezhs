namespace Mezhs.Log.Sql;

public sealed record SqlColumn(
    string Name,
    string Type,
    bool NotNull,
    object? DefaultValue,
    int PrimaryKeyOrder);

public sealed record SqlTable(
    string Name,
    IReadOnlyList<SqlColumn> Columns);

public sealed record SqlResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);

public sealed record SqlMigration(
    int Version,
    string Name,
    string Sql);
