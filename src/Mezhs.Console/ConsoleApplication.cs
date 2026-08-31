using System.Reflection;
using System.Text.Json;

namespace Mezhs.Console;

public abstract class ConsoleApplication
{
    protected virtual CommandSyntax Syntax => CommandSyntax.Default;

    public int Run()
    {
        try
        {
            var nodes = CommandLineParser.Parse(Environment.CommandLine, Syntax);
            return Execute(nodes.Skip(1).ToArray());
        }
        catch (Exception ex)
        {
            return Error($"Invalid command syntax: {ex.Message}");
        }
    }

    public int Run(string commandLine)
    {
        try
        {
            return Execute(CommandLineParser.Parse(commandLine, Syntax));
        }
        catch (Exception ex)
        {
            return Error($"Invalid command syntax: {ex.Message}");
        }
    }

    [Command(Description = "Show available commands or detailed help for one command.")]
    public void Help(string? command = null)
    {
        var commands = CommandDiscovery.Discover(GetType());
        if (string.IsNullOrWhiteSpace(command))
            HelpWriter.WriteAll(GetType().Name, commands, Syntax);
        else
            HelpWriter.WriteCommand(commands, command, Syntax);
    }

    [Command(Description = "Validate all command method signatures.")]
    public void Validate()
    {
        var invalid = CommandDiscovery.Discover(GetType()).Where(x => !x.IsValid).ToArray();
        if (invalid.Length == 0)
        {
            global::System.Console.WriteLine("All commands are valid.");
            return;
        }

        foreach (var command in invalid)
        {
            global::System.Console.WriteLine($"{command.Name}: INVALID");
            foreach (var error in command.ValidationErrors)
                global::System.Console.WriteLine($"  - {error}");
        }
    }

    [Command(Description = "Run application self-tests.")]
    public virtual string Test() => "No tests are defined for this application.";

    protected virtual object? ExecuteCommand(MethodInfo method, object?[] arguments) =>
        method.Invoke(this, arguments);

    private int Execute(IReadOnlyList<ValueNode> nodes)
    {
        if (!MezhsExecutionContext.IsAvailable)
            global::System.Console.Error.WriteLine("MEZHS INFO: No execution context found. Running standalone.");

        var commands = CommandDiscovery.Discover(GetType());
        if (nodes.Count == 0)
        {
            HelpWriter.WriteAll(GetType().Name, commands, Syntax);
            return 0;
        }

        if (nodes[0] is not ScalarNode commandNode)
            return Error("Command name must be a scalar value.");

        var matches = commands.Where(x => x.Name.Equals(commandNode.Value, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
            return Error($"Unknown command '{commandNode.Value}'. Run 'Help' to list commands.");

        var command = matches[0];
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
            var result = ExecuteCommand(command.Method, arguments);
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
                if (values[i] is ScalarNode { Value: var value, IsQuoted: false } &&
                    value.Equals("null", StringComparison.OrdinalIgnoreCase) &&
                    !IsNullable(parameter, nullability))
                {
                    throw new FormatException($"null is not valid for non-nullable parameter '{parameter.Name}'.");
                }

                result[i] = ValueBinder.Bind(values[i], parameter.ParameterType);
                continue;
            }

            if (parameter.HasDefaultValue)
            {
                result[i] = parameter.DefaultValue;
                continue;
            }

            if (IsNullable(parameter, nullability))
            {
                result[i] = null;
                continue;
            }

            throw new FormatException($"Missing required parameter '{parameter.Name}'.");
        }

        return result;
    }

    private static bool IsNullable(ParameterInfo parameter, NullabilityInfoContext nullability) =>
        Nullable.GetUnderlyingType(parameter.ParameterType) is not null ||
        (!parameter.ParameterType.IsValueType && nullability.Create(parameter).ReadState == NullabilityState.Nullable);

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
        if (result is IConvertible)
        {
            global::System.Console.WriteLine(Convert.ToString(result, System.Globalization.CultureInfo.CurrentCulture));
            return;
        }
        global::System.Console.WriteLine(JsonSerializer.Serialize(result, result.GetType()));
    }

    private static int Error(string message, int exitCode = 2)
    {
        global::System.Console.Error.WriteLine($"ERROR: {message}");
        return exitCode;
    }
}
