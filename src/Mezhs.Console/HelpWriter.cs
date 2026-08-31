using System.Globalization;
using System.Reflection;

namespace Mezhs.Console;

internal static class HelpWriter
{
    public static void WriteAll(string applicationName, IReadOnlyList<CommandDescriptor> commands, CommandSyntax syntax)
    {
        global::System.Console.WriteLine(applicationName);
        global::System.Console.WriteLine();
        global::System.Console.WriteLine("Commands:");

        var rows = commands
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(command => (Command: command, Usage: Usage(command)))
            .ToArray();
        var width = rows.Length == 0 ? 0 : rows.Max(x => x.Usage.Length);

        foreach (var row in rows)
        {
            var invalid = row.Command.IsValid ? "" : " [INVALID]";
            var description = string.IsNullOrWhiteSpace(row.Command.Description) ? "" : $"  {row.Command.Description}";
            global::System.Console.WriteLine($"  {row.Usage.PadRight(width)}{invalid}{description}");
            foreach (var error in row.Command.ValidationErrors)
                global::System.Console.WriteLine($"    ERROR: {error}");
        }

        WriteInputFormat(syntax);
    }

    public static void WriteCommand(IReadOnlyList<CommandDescriptor> commands, string name, CommandSyntax syntax)
    {
        var command = commands.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (command is null)
        {
            global::System.Console.Error.WriteLine($"ERROR: Unknown command '{name}'.");
            return;
        }

        global::System.Console.WriteLine(command.Name);
        if (!string.IsNullOrWhiteSpace(command.Description))
            global::System.Console.WriteLine($"  {command.Description}");

        if (!command.IsValid)
        {
            global::System.Console.WriteLine();
            global::System.Console.WriteLine("Errors:");
            foreach (var error in command.ValidationErrors)
                global::System.Console.WriteLine($"  {error}");
        }

        global::System.Console.WriteLine();
        global::System.Console.WriteLine("Usage:");
        global::System.Console.WriteLine($"  {Usage(command)}");

        var parameters = command.Method.GetParameters();
        if (parameters.Length > 0)
        {
            global::System.Console.WriteLine();
            global::System.Console.WriteLine("Parameters:");
            var width = parameters.Max(x => x.Name?.Length ?? 0);
            foreach (var parameter in parameters)
            {
                var requirement = IsOptional(parameter)
                    ? $"optional  default: {DefaultValue(parameter)}"
                    : "required";
                global::System.Console.WriteLine($"  {(parameter.Name ?? "").PadRight(width)}  {ValueBinder.Describe(parameter.ParameterType, syntax)}  {requirement}");
            }
        }

        if (!string.IsNullOrWhiteSpace(command.Example))
        {
            global::System.Console.WriteLine();
            global::System.Console.WriteLine("Example:");
            global::System.Console.WriteLine($"  {command.Example}");
        }

        WriteInputFormat(syntax);
    }

    private static string Usage(CommandDescriptor command)
    {
        var parts = command.Method.GetParameters()
            .Select(parameter => IsOptional(parameter)
                ? $"[{parameter.Name}={DefaultValue(parameter)}]"
                : $"<{parameter.Name}>");
        return string.Join(' ', new[] { command.Name }.Concat(parts));
    }

    private static void WriteInputFormat(CommandSyntax syntax)
    {
        var culture = CultureInfo.CurrentCulture;
        global::System.Console.WriteLine();
        global::System.Console.WriteLine("Input format:");
        global::System.Console.WriteLine($"  Culture: {culture.Name}");
        global::System.Console.WriteLine($"  Date: {culture.DateTimeFormat.ShortDatePattern}");
        global::System.Console.WriteLine($"  Time: {culture.DateTimeFormat.LongTimePattern}");
        global::System.Console.WriteLine($"  Decimal: {culture.NumberFormat.NumberDecimalSeparator}");
        foreach (var token in syntax.Tokens)
        {
            var suffix = token.Type == CommandSyntaxTokenType.Object ? " using key:value pairs" : "";
            global::System.Console.WriteLine($"  {token.Type}: {token.Start}...{token.End}{suffix}");
        }
    }

    private static bool IsOptional(ParameterInfo parameter)
    {
        if (parameter.HasDefaultValue || Nullable.GetUnderlyingType(parameter.ParameterType) is not null)
            return true;
        return !parameter.ParameterType.IsValueType && new NullabilityInfoContext().Create(parameter).ReadState == NullabilityState.Nullable;
    }

    private static string DefaultValue(ParameterInfo parameter)
    {
        var value = parameter.HasDefaultValue ? parameter.DefaultValue : null;
        return value switch
        {
            null => "null",
            string text when text.Any(char.IsWhiteSpace) => $"\"{text}\"",
            string text => text,
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.CurrentCulture),
            _ => value.ToString() ?? "null"
        };
    }
}
