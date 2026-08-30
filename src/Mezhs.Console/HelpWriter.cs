using System.Globalization;
using System.Reflection;

namespace Mezhs.Console;

internal static class HelpWriter
{
    public static void WriteAll(string applicationName, IReadOnlyList<CommandDescriptor> commands, CommandSyntax syntax)
    {
        global::System.Console.WriteLine($"{applicationName} commands:");
        global::System.Console.WriteLine();
        foreach (var command in commands.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            global::System.Console.WriteLine($"  {command.Name}{(command.IsValid ? "" : "  [INVALID]")}");
            if (!string.IsNullOrWhiteSpace(command.Description)) global::System.Console.WriteLine($"      {command.Description}");
            foreach (var error in command.ValidationErrors) global::System.Console.WriteLine($"      ERROR: {error}");
        }
        WriteEnvironment(syntax);
    }

    public static void WriteCommand(IReadOnlyList<CommandDescriptor> commands, string name, CommandSyntax syntax)
    {
        var command = commands.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (command is null) { global::System.Console.Error.WriteLine($"ERROR: Unknown command '{name}'."); return; }
        global::System.Console.WriteLine(command.Name);
        if (!string.IsNullOrWhiteSpace(command.Description)) global::System.Console.WriteLine(command.Description);
        if (!command.IsValid) foreach (var error in command.ValidationErrors) global::System.Console.WriteLine($"ERROR: {error}");
        global::System.Console.Write($"Usage: {command.Name}");
        foreach (var parameter in command.Method.GetParameters()) global::System.Console.Write(IsOptional(parameter) ? $" [{parameter.Name}]" : $" <{parameter.Name}>");
        global::System.Console.WriteLine();
        foreach (var parameter in command.Method.GetParameters())
            global::System.Console.WriteLine($"  {parameter.Name}: {ValueBinder.Describe(parameter.ParameterType, syntax)} ({(IsOptional(parameter) ? "optional" : "required")})");
        WriteEnvironment(syntax);
    }

    private static void WriteEnvironment(CommandSyntax syntax)
    {
        var culture = CultureInfo.CurrentCulture;
        global::System.Console.WriteLine();
        global::System.Console.WriteLine($"OS culture: {culture.Name}");
        global::System.Console.WriteLine($"Date: {culture.DateTimeFormat.ShortDatePattern}");
        global::System.Console.WriteLine($"Time: {culture.DateTimeFormat.LongTimePattern}");
        global::System.Console.WriteLine($"Decimal separator: {culture.NumberFormat.NumberDecimalSeparator}");
        foreach (var token in syntax.Tokens) global::System.Console.WriteLine($"{token.Type}: {token.Start}...{token.End}");
    }

    private static bool IsOptional(ParameterInfo parameter)
    {
        if (parameter.HasDefaultValue || Nullable.GetUnderlyingType(parameter.ParameterType) is not null) return true;
        return !parameter.ParameterType.IsValueType && new NullabilityInfoContext().Create(parameter).ReadState == NullabilityState.Nullable;
    }
}
