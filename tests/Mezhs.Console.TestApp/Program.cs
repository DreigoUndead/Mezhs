using Mezhs.Console;

return MezhsConsole.Run(new Commands());

internal sealed class Commands
{
    [Command(Description = "Echo a string and optional number.")]
    public string Echo(string value, int? count = null) => $"{value}:{count?.ToString() ?? "null"}";

    [Command(Description = "Insert integer values.", Example = "Insert [1 5 6] tail")]
    public string Insert(IEnumerable<int> values, string tail) => $"{string.Join(',', values)}|{tail}";

    [Command(Description = "Show deterministic date parsing.")]
    public string Date(DateTimeOffset value) => value.ToString("O");

    [Command(Description = "Intentionally invalid; help must report this without breaking other commands.")]
    public void Broken(ComplexObject value) { }
}

internal sealed record ComplexObject(int Val1, int Val2, int Val3);
