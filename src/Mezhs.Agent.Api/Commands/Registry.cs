namespace Mezhs.Agent.Commands;

public enum CommandForm
{
    Marker,
    Block
}

public enum CommandBehavior
{
    Complete,
    Shell
}

public sealed record CommandDefinition(
    string Name,
    CommandForm Form,
    CommandBehavior Behavior);

public static class Registry
{
    public static readonly CommandDefinition[] All =
    [
        new("DONE", CommandForm.Marker, CommandBehavior.Complete),
        new("SH", CommandForm.Block, CommandBehavior.Shell)
    ];

    private static readonly IReadOnlyDictionary<string, CommandDefinition> ByName =
        All.ToDictionary(command => command.Name, StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string name, out CommandDefinition definition) =>
        ByName.TryGetValue(name, out definition!);

    public static CommandDefinition Get(CommandBehavior behavior) =>
        All.Single(command => command.Behavior == behavior);
}
