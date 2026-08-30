using System.Reflection;

namespace Mezhs.Console;

internal sealed record CommandDescriptor(
    MethodInfo Method,
    string Name,
    string? Description,
    string? Example,
    IReadOnlyList<string> ValidationErrors)
{
    public bool IsValid => ValidationErrors.Count == 0;
}

internal static class CommandDiscovery
{
    public static IReadOnlyList<CommandDescriptor> Discover(Type applicationType)
    {
        var commands = applicationType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => (method, attribute: method.GetCustomAttribute<CommandAttribute>()))
            .Where(x => x.attribute is not null)
            .Select(x => Describe(x.method, x.attribute!))
            .ToList();

        foreach (var group in commands.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
        {
            foreach (var command in group.ToArray())
            {
                var index = commands.IndexOf(command);
                commands[index] = command with
                {
                    ValidationErrors = command.ValidationErrors.Concat([$"Duplicate command name '{command.Name}'. Command overloads are not supported."]).ToArray()
                };
            }
        }

        return commands;
    }

    private static CommandDescriptor Describe(MethodInfo method, CommandAttribute attribute)
    {
        var errors = new List<string>();
        if (method.IsGenericMethodDefinition || method.ContainsGenericParameters)
            errors.Add("Generic command methods are not supported.");
        if (typeof(Task).IsAssignableFrom(method.ReturnType))
            errors.Add("Task return types are not supported. Console commands must complete synchronously.");

        foreach (var parameter in method.GetParameters())
        {
            if (parameter.ParameterType.IsByRef || parameter.IsOut)
            {
                errors.Add($"Parameter '{parameter.Name}' uses ref/out, which is not supported.");
                continue;
            }
            if (!ValueBinder.CanBind(parameter.ParameterType, out var reason))
                errors.Add($"Parameter '{parameter.Name}' ({ValueBinder.FriendlyName(parameter.ParameterType)}): {reason}");
        }

        return new CommandDescriptor(method, attribute.Name, attribute.Description, attribute.Example, errors);
    }
}
