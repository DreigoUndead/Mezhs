using System.Reflection;
using System.Text.Json;

namespace Mezhs.Console;

public static class MezhsConsole
{
    public static int Run<T>() where T : new() => Run(new T(), GetCurrentCommandLine());
    public static int Run(object application) => Run(application, GetCurrentCommandLine());
    public static int Run<T>(string commandLine) where T : new() => Run(new T(), commandLine);

    public static int Run(object application, string commandLine)
    {
        if (!MezhsExecutionContext.IsAvailable)
            global::System.Console.Error.WriteLine("MEZHS INFO: No execution context found. Running standalone.");

        var commands = CommandDiscovery.Discover(application.GetType());
        IReadOnlyList<ValueNode> nodes;
        try
        {
            nodes = CommandLineParser.Parse(commandLine);
        }
        catch (Exception ex)
        {
            return Error($"Invalid command syntax: {ex.Message}");
        }

        if (nodes.Count == 0)
        {
            HelpWriter.WriteAll(application.GetType().Name, commands);
            return 0;
        }

        if (nodes[0] is not ScalarNode commandNode)
            return Error("Command name must be a scalar value.");

        if (commandNode.Value.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            if (nodes.Count == 1)
                HelpWriter.WriteAll(application.GetType().Name, commands);
            else if (nodes.Count == 2 && nodes[1] is ScalarNode requested)
                HelpWriter.WriteCommand(commands, requested.Value);
            else
                return Error("Usage: help [command]");
            return 0;
        }

        var matching = commands.Where(x => x.Name.Equals(commandNode.Value, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matching.Length == 0)
            return Error($"Unknown command '{commandNode.Value}'. Run 'help' to list commands.");

        var command = matching[0];
        if (!command.IsValid)
        {
            global::System.Console.Error.WriteLine($"ERROR: Command '{command.Name}' cannot be executed because its method signature is invalid.");
            foreach (var validationError in command.ValidationErrors)
                global::System.Console.Error.WriteLine($"  - {validationError}");
            return 3;
        }

        object?[] arguments;
        try
        {
            arguments = BindArguments(command.Method, nodes.Skip(1).ToArray());
        }
        catch (Exception ex)
        {
            return Error($"Invalid arguments for '{command.Name}': {ex.Message}");
        }

        try
        {
            var result = command.Method.Invoke(command.Method.IsStatic ? null : application, arguments);
            WriteResult(result, command.Method.ReturnType);
            return 0;
        }
        catch (TargetInvocationException ex)
        {
            return Error(ex.InnerException?.Message ?? ex.Message, 4);
        }
        catch (Exception ex)
        {
            return Error(ex.Message, 4);
        }
    }

    private static object?[] BindArguments(MethodInfo method, IReadOnlyList<ValueNode> values)
    {
        var parameters = method.GetParameters();
        if (values.Count > parameters.Length)
            throw new FormatException($"Expected at most {parameters.Length} parameters but received {values.Count}.");

        var result = new object?[parameters.Length];
        var nullability = new NullabilityInfoContext();
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            if (i < values.Count)
            {
                result[i] = ValueBinder.Bind(values[i], parameter.ParameterType);
                continue;
            }

            if (parameter.HasDefaultValue)
            {
                result[i] = parameter.DefaultValue;
                continue;
            }

            if (Nullable.GetUnderlyingType(parameter.ParameterType) is not null ||
                (!parameter.ParameterType.IsValueType && nullability.Create(parameter).ReadState == NullabilityState.Nullable))
            {
                result[i] = null;
                continue;
            }

            throw new FormatException($"Missing required parameter '{parameter.Name}'.");
        }

        return result;
    }

    private static void WriteResult(object? result, Type returnType)
    {
        if (returnType == typeof(void))
            return;
        if (result is null)
        {
            global::System.Console.WriteLine("null");
            return;
        }
        if (result is string text)
        {
            global::System.Console.WriteLine(text);
            return;
        }
        if (result.GetType().IsPrimitive || result is decimal or Enum)
        {
            global::System.Console.WriteLine(Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture));
            return;
        }
        global::System.Console.WriteLine(JsonSerializer.Serialize(result, result.GetType()));
    }

    private static string GetCurrentCommandLine()
    {
        var nodes = CommandLineParser.Parse(Environment.CommandLine);
        if (nodes.Count <= 1)
            return string.Empty;
        return SerializeForReparse(nodes.Skip(1));
    }

    private static string SerializeForReparse(IEnumerable<ValueNode> nodes) => string.Join(" ", nodes.Select(SerializeNode));

    private static string SerializeNode(ValueNode node) => node switch
    {
        ScalarNode scalar => QuoteIfNeeded(scalar.Value),
        ListNode list => $"[{SerializeForReparse(list.Items)}]",
        _ => throw new InvalidOperationException()
    };

    private static string QuoteIfNeeded(string value)
    {
        if (value.Length > 0 && value.All(c => !char.IsWhiteSpace(c) && c is not '[' and not ']' and not '"'))
            return value;
        return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    private static int Error(string message, int exitCode = 2)
    {
        global::System.Console.Error.WriteLine($"ERROR: {message}");
        return exitCode;
    }
}
