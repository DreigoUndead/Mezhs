using System.Runtime.CompilerServices;

namespace Mezhs.Console;

[AttributeUsage(AttributeTargets.Method)]
public sealed class CommandAttribute : Attribute
{
    public CommandAttribute([CallerMemberName] string name = "") => Name = name;

    public string Name { get; }
    public string? Description { get; init; }
    public string? Example { get; init; }
}
