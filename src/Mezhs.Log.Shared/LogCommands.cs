using System.Reflection;
using Mezhs.Console;

namespace Mezhs.Log.Shared;

public abstract class LogCommands(LogShared shared) : ConsoleApplication
{
    protected LogShared Shared { get; } = shared;

    [Command(Description = "Show the notes/instructions associated with a log file.")]
    public string Notes(string file) =>
        Shared.GetNotes(file) ?? $"No notes found for '{file}'.";

    protected override object? ExecuteCommand(MethodInfo method, object?[] arguments)
    {
        var result = base.ExecuteCommand(method, arguments);
        if (method.DeclaringType == typeof(ConsoleApplication) || method.DeclaringType == typeof(LogCommands))
            return result;
        if (arguments.FirstOrDefault() is not string file)
            return result;

        var notes = Shared.GetNotes(file);
        if (string.IsNullOrWhiteSpace(notes))
            return result;

        return result is null
            ? $"Notes:\n{notes}"
            : $"{result}\n\nNotes:\n{notes}";
    }
}
