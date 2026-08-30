using System.Reflection;

namespace Mezhs.Console;

internal static class HelpWriter
{
    public static void WriteAll(string applicationName, IReadOnlyList<CommandDescriptor> commands)
    {
        global::System.Console.WriteLine($"{applicationName} commands:");
        global::System.Console.WriteLine();
        foreach (var command in commands.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var status = command.IsValid ? string.Empty : "  [INVALID]";
            global::System.Console.WriteLine($"  {command.Name}{status}");
            if (!string.IsNullOrWhiteSpace(command.Description))
                global::System.Console.WriteLine($"      {command.Description}");
            if (!command.IsValid)
            {
                foreach (var error in command.ValidationErrors)
                    global::System.Console.WriteLine($"      ERROR: {error}");
            }
        }
        global::System.Console.WriteLine();
        global::System.Console.WriteLine("Use 'help <command>' for parameter syntax.");
    }

    public static void WriteCommand(IReadOnlyList<CommandDescriptor> commands, string name)
    {
        var matches = commands.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
        {
            global::System.Console.Error.WriteLine($"ERROR: Unknown command '{name}'.");
            return;
        }

        var command = matches[0];
        global::System.Console.WriteLine(command.Name);
        if (!string.IsNullOrWhiteSpace(command.Description))
            global::System.Console.WriteLine(command.Description);
        global::System.Console.WriteLine();

        if (!command.IsValid)
        {
            global::System.Console.WriteLine("Status: INVALID COMMAND");
            foreach (var error in command.ValidationErrors)
                global::System.Console.WriteLine($"  - {error}");
            global::System.Console.WriteLine();
        }

        var parameters = command.Method.GetParameters();
        global::System.Console.Write("Usage: ");
        global::System.Console.Write(command.Name);
        foreach (var parameter in parameters)
        {
            var optional = IsOptional(parameter);
            global::System.Console.Write(optional ? $" [{parameter.Name}]" : $" <{parameter.Name}>");
        }
        global::System.Console.WriteLine();

        if (parameters.Length > 0)
        {
            global::System.Console.WriteLine();
            global::System.Console.WriteLine("Parameters:");
            foreach (var parameter in parameters)
            {
                var requirement = IsOptional(parameter) ? "optional" : "required";
                var defaultText = parameter.HasDefaultValue ? $", default: {FormatDefault(parameter.DefaultValue)}" : string.Empty;
                var format = ValueBinder.CanBind(parameter.ParameterType, out _) ? ValueBinder.Describe(parameter.ParameterType) : "UNSUPPORTED";
                global::System.Console.WriteLine($"  {parameter.Name} : {ValueBinder.FriendlyName(parameter.ParameterType)} ({requirement}{defaultText})");
                global::System.Console.WriteLine($"      Format: {format}");
            }
        }

        if (!string.IsNullOrWhiteSpace(command.Example))
        {
            global::System.Console.WriteLine();
            global::System.Console.WriteLine($"Example: {command.Example}");
        }
    }

    private static bool IsOptional(ParameterInfo parameter)
    {
        if (parameter.HasDefaultValue || Nullable.GetUnderlyingType(parameter.ParameterType) is not null)
            return true;
        if (parameter.ParameterType.IsValueType)
            return false;
        return new NullabilityInfoContext().Create(parameter).ReadState == NullabilityState.Nullable;
    }

    private static string FormatDefault(object? value) => value is null ? "null" : value.ToString() ?? "null";
}
