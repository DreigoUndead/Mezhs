using Mezhs.Common.RichUi;

namespace Mezhs.Agent.Commands;

public enum CommandForm
{
    Marker,
    Block
}

public enum CommandBehavior
{
    Complete,
    Shell,
    Attach
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
        new("SH", CommandForm.Block, CommandBehavior.Shell),
        new("ATTACH", CommandForm.Block, CommandBehavior.Attach)
    ];

    // Executable tag registration belongs to the Agent host. Core uses only
    // the UI catalog; both hosts continue to share the single C# parser.
    public static readonly RichTagCatalog RichTags = RichTagRegistry.Ui.With(
        All.Select(command => new RichTagDefinition(
            command.Name,
            command.Form == CommandForm.Block ? RichTagRole.Command : RichTagRole.Marker,
            new HashSet<string>(StringComparer.Ordinal),
            SearchChildren: false,
            AllowAsChild: false,
            ProcessContent: RichTagHandlers.Command)));

    private static readonly IReadOnlyDictionary<string, CommandDefinition> ByName =
        All.ToDictionary(command => command.Name, StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string name, out CommandDefinition definition) =>
        ByName.TryGetValue(name, out definition!);

    public static CommandDefinition Get(CommandBehavior behavior) =>
        All.Single(command => command.Behavior == behavior);
}
