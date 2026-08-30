using System.Globalization;
using Mezhs.Console;

return new TestApplication().Run();

internal class TestApplication : ConsoleApplication
{
    [Command(Description = "Run the console framework regression suite.")]
    public string Test()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("Echo nullable", () => Expect("Echo hello", "hello:null")),
            ("Literal null", () => Expect("Echo hello null", "hello:null")),
            ("Enumerable", () => Expect("Insert [1 5 6] tail", "1,5,6|tail")),
            ("Nested enumerable", () => Expect("Nested [[1 2] [3 4]]", "1,2;3,4")),
            ("Quoted string", () => Expect("Echo \"hello world\"", "hello world:null")),
            ("Invalid command help", () => Expect("Help Broken", "ComplexObject")),
            ("Invalid enumerable help", () => Expect("Help BrokenEnumerable", "cannot be constructed from command input")),
            ("Invalid command isolated", () =>
            {
                if (RunCase("Echo ok").ExitCode != 0) throw new InvalidOperationException("Valid command failed.");
                if (RunCase("Broken nope").ExitCode != 3) throw new InvalidOperationException("Invalid command did not return exit code 3.");
                if (RunCase("BrokenEnumerable [1 2]").ExitCode != 3) throw new InvalidOperationException("Unconstructable enumerable command did not return exit code 3.");
            }),
            ("Inherited Help", () => Expect("Help Help", "Show available commands")),
            ("Inherited Validate", () => Expect("Validate", "Broken: INVALID")),
            ("Current culture conversion", TestCulture),
            ("Syntax override", () =>
            {
                var result = RunCase("Insert (1 2 3) tail", new AlternateSyntaxApplication());
                if (!result.Out.Contains("1,2,3|tail", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Unexpected output: {result.Out}");
            }),
            ("Missing MEZHS context warning", TestMissingContext)
        };

        var failures = new List<string>();
        foreach (var test in tests)
        {
            try
            {
                test.Body();
                Console.WriteLine($"PASS: {test.Name}");
            }
            catch (Exception ex)
            {
                failures.Add($"FAIL: {test.Name}: {ex.Message}");
                Console.WriteLine(failures[^1]);
            }
        }

        if (failures.Count > 0)
            throw new InvalidOperationException($"{failures.Count}/{tests.Length} tests failed.");

        return $"PASS: {tests.Length}/{tests.Length} tests";
    }

    [Command(Description = "Echo a string and optional number.")]
    public string Echo(string value, int? count = null) => $"{value}:{count?.ToString() ?? "null"}";

    [Command(Description = "Insert integer values.", Example = "Insert [1 5 6] tail")]
    public string Insert(IEnumerable<int> values, string tail) => $"{string.Join(',', values)}|{tail}";

    [Command(Description = "Insert nested integer values.")]
    public string Nested(IEnumerable<IEnumerable<int>> values) => string.Join(';', values.Select(x => string.Join(',', x)));

    [Command(Description = "Parse a date using the current OS culture.")]
    public string Date(DateTime value) => value.ToString("O");

    [Command(Description = "Parse a decimal using the current OS culture.")]
    public decimal Decimal(decimal value) => value;

    [Command(Description = "Intentionally invalid; help must report this without breaking other commands.")]
    public void Broken(ComplexObject value) { }

    [Command(Description = "Intentionally invalid enumerable type.")]
    public void BrokenEnumerable(IOrderedEnumerable<int> values) { }

    private void Expect(string command, string expected)
    {
        var result = RunCase(command);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Exit code {result.ExitCode}. Error: {result.Error}");
        if (!result.Out.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected '{expected}' in output '{result.Out}'.");
    }

    private void TestCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("lv-LV");
            Expect("Decimal 1,5", "1,5");
            Expect("Help Date", "OS culture: lv-LV");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private void TestMissingContext()
    {
        var previous = Environment.GetEnvironmentVariable(MezhsExecutionContext.ExecutionIdVariable);
        try
        {
            Environment.SetEnvironmentVariable(MezhsExecutionContext.ExecutionIdVariable, null);
            var result = RunCase("Echo ok", setContext: false);
            if (result.ExitCode != 0 || !result.Error.Contains("No execution context found", StringComparison.Ordinal))
                throw new InvalidOperationException("Standalone context warning was not emitted correctly.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(MezhsExecutionContext.ExecutionIdVariable, previous);
        }
    }

    private static RunResult RunCase(string command, TestApplication? application = null, bool setContext = true)
    {
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var previousExecution = Environment.GetEnvironmentVariable(MezhsExecutionContext.ExecutionIdVariable);
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            if (setContext)
                Environment.SetEnvironmentVariable(MezhsExecutionContext.ExecutionIdVariable, "test");
            return new RunResult((application ?? new TestApplication()).Run(command), output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable(MezhsExecutionContext.ExecutionIdVariable, previousExecution);
        }
    }

    private sealed record RunResult(int ExitCode, string Out, string Error);
}

internal sealed class AlternateSyntaxApplication : TestApplication
{
    protected override CommandSyntax Syntax => new([
        new(CommandSyntaxTokenType.Quote, '\'', '\'', '\\'),
        new(CommandSyntaxTokenType.Collection, '(', ')')
    ]);
}

internal sealed record ComplexObject(int Val1, int Val2, int Val3);
