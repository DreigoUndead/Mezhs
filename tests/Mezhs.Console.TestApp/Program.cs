using System.Globalization;
using Mezhs.Console;

return new TestApplication().Run();

internal sealed class TestApplication : ConsoleApplication
{
    [Command(Description = "Run the console framework regression suite.")]
    public override string Test()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("CallerMemberName command", () => Expect("Help Echo", "Echo")),
            ("Explicit command name", () => Expect("renamed 5", "5", new RenamedCommandApplication())),
            ("Echo nullable", () => Expect("Echo hello", "hello:null")),
            ("Literal null", () => Expect("Echo hello null", "hello:null")),
            ("Quoted null string", () => Expect("Echo \"null\"", "null:null")),
            ("Non-nullable literal null", () => ExpectFailure("Required null", 2, new NullabilityApplication())),
            ("Non-nullable quoted null", () => Expect("Required \"null\"", "null", new NullabilityApplication())),
            ("Enumerable", () => Expect("Insert [1 5 6] tail", "1,5,6|tail", new CollectionApplication())),
            ("Nested enumerable", () => Expect("Nested [[1 2] [3 4]]", "1,2;3,4", new CollectionApplication())),
            ("Object dictionary", () => Expect(
                "Map {name:\"test value\" count:5 active:true missing:null}",
                "name=String:test value|count=Int64:5|active=Boolean:True|missing=null",
                new ObjectApplication())),
            ("Typed dictionary", () => Expect("Typed {first:1 second:2}", "first=1,second=2", new ObjectApplication())),
            ("Nested dynamic values", () => Expect("Nested {values:[1 2] child:{name:test}}", "values=Object[]|child=Dictionary", new ObjectApplication())),
            ("Optional default help", () =>
            {
                Expect("Help Limited", "[limit=50]", new ObjectApplication());
                Expect("Help Limited", "default: 50", new ObjectApplication());
            }),
            ("Quoted string", () => Expect("Echo \"hello world\"", "hello world:null")),
            ("Invalid command help", () => Expect("Help Broken", "ComplexObject", new InvalidApplication())),
            ("Invalid enumerable help", () => Expect("Help BrokenEnumerable", "cannot be constructed from command input", new InvalidApplication())),
            ("Invalid ValueTask help", () => Expect("Help AsyncBroken", "Task/ValueTask return types are not supported", new InvalidApplication())),
            ("Invalid command isolated", () =>
            {
                if (RunCase("Echo ok").ExitCode != 0) throw new InvalidOperationException("Valid command failed.");
                if (RunCase("Broken nope", new InvalidApplication()).ExitCode != 3) throw new InvalidOperationException("Invalid command did not return exit code 3.");
                if (RunCase("BrokenEnumerable [1 2]", new InvalidApplication()).ExitCode != 3) throw new InvalidOperationException("Unconstructable enumerable command did not return exit code 3.");
            }),
            ("Inherited Help", () => Expect("Help Help", "Show available commands")),
            ("Inherited Validate", () => Expect("Validate", "All commands are valid.")),
            ("Invalid Validate", () => Expect("Validate", "Broken: INVALID", new InvalidApplication())),
            ("Current culture conversion", TestCulture),
            ("Syntax override", () =>
            {
                var result = RunCase("Insert (1 2 3) tail", new AlternateSyntaxApplication());
                if (!result.Out.Contains("1,2,3|tail", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Unexpected output: {result.Out}");
            }),
            ("Return object round trip", TestReturnObjectRoundTrip),
            ("Return object enumerable round trip", TestReturnObjectMany),
            ("Return object CLI output", () => Expect("Object", "Name: \"hello: world\"", new ReturnObjectApplication())),
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

    private void Expect(string command, string expected, ConsoleApplication? application = null)
    {
        var result = RunCase(command, application);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Exit code {result.ExitCode}. Error: {result.Error}");
        if (!result.Out.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected '{expected}' in output '{result.Out}'.");
    }

    private void ExpectFailure(string command, int exitCode, ConsoleApplication application)
    {
        var result = RunCase(command, application);
        if (result.ExitCode != exitCode)
            throw new InvalidOperationException($"Expected exit code {exitCode} but got {result.ExitCode}. Output: {result.Out} Error: {result.Error}");
    }

    private void TestCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("lv-LV");
            Expect("Decimal 1,5", "1,5", new CultureApplication());
            Expect("Help Date", "Culture: lv-LV", new CultureApplication());
            Expect("Map {value:1,5}", "value=Double:1,5", new ObjectApplication());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static void TestReturnObjectRoundTrip()
    {
        var expected = new TestReturnObject
        {
            Id = 17,
            Status = TestStatus.Running,
            Name = "null",
            Text = $"first: line{Environment.NewLine}{ReturnObjectBase.SeparatorLine}{Environment.NewLine}quote \" and slash \\",
            Optional = null,
            Time = new DateTimeOffset(2026, 9, 8, 20, 30, 0, TimeSpan.FromHours(3))
        };
        var serialized = expected.ToString();
        var actual = ReturnObjectBase.Parse<TestReturnObject>(serialized);
        if (actual.Id != expected.Id || actual.Status != expected.Status || actual.Name != expected.Name ||
            actual.Text != expected.Text || actual.Optional is not null || actual.Time != expected.Time)
            throw new InvalidOperationException($"Round trip mismatch. Serialized: {serialized}");
    }

    private static void TestReturnObjectMany()
    {
        var first = new TestReturnObject
        {
            Id = 1,
            Status = TestStatus.Running,
            Name = "one",
            Text = $"inside{Environment.NewLine}{ReturnObjectBase.SeparatorLine}{Environment.NewLine}value",
            Optional = 5,
            Time = DateTimeOffset.Parse("2026-09-08T17:00:00+00:00", CultureInfo.InvariantCulture)
        };
        var second = new TestReturnObject
        {
            Id = 2,
            Status = TestStatus.Completed,
            Name = "two",
            Text = "done",
            Optional = null,
            Time = DateTimeOffset.Parse("2026-09-08T18:00:00+00:00", CultureInfo.InvariantCulture)
        };
        var serialized = ReturnObjectBase.FormatMany([first, second]);
        var parsed = ReturnObjectBase.ParseMany<TestReturnObject>(serialized);
        if (parsed.Count != 2 || parsed[0].Text != first.Text || parsed[1].Id != 2)
            throw new InvalidOperationException($"Enumerable round trip mismatch. Serialized: {serialized}");
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

    private static RunResult RunCase(string command, ConsoleApplication? application = null, bool setContext = true)
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

internal sealed class CollectionApplication : ConsoleApplication
{
    [Command] public string Insert(IEnumerable<int> values, string tail) => $"{string.Join(',', values)}|{tail}";
    [Command] public string Nested(IEnumerable<IEnumerable<int>> values) => string.Join(';', values.Select(x => string.Join(',', x)));
}

internal sealed class ObjectApplication : ConsoleApplication
{
    [Command]
    public string Map(IReadOnlyDictionary<string, object?> values) =>
        string.Join('|', values.Select(x => x.Value is null
            ? $"{x.Key}=null"
            : $"{x.Key}={x.Value.GetType().Name}:{x.Value}"));

    [Command]
    public string Typed(IReadOnlyDictionary<string, int> values) =>
        string.Join(',', values.Select(x => $"{x.Key}={x.Value}"));

    [Command]
    public string Nested(IReadOnlyDictionary<string, object?> values) =>
        $"values={values["values"]!.GetType().Name}|child={values["child"]!.GetType().Name.Split('`')[0]}";

    [Command]
    public int Limited(int limit = 50) => limit;
}

internal sealed class AlternateSyntaxApplication : ConsoleApplication
{
    protected override CommandSyntax Syntax => new([
        new(CommandSyntaxTokenType.Quote, '\'', '\'', '\\'),
        new(CommandSyntaxTokenType.Collection, '(', ')')
    ]);

    [Command] public string Insert(IEnumerable<int> values, string tail) => $"{string.Join(',', values)}|{tail}";
}

internal sealed class CultureApplication : ConsoleApplication
{
    [Command] public string Date(DateTime value) => value.ToString("O");
    [Command] public decimal Decimal(decimal value) => value;
}

internal sealed class InvalidApplication : ConsoleApplication
{
    [Command] public void Broken(ComplexObject value) { }
    [Command] public void BrokenEnumerable(IOrderedEnumerable<int> values) { }
    [Command] public ValueTask AsyncBroken() => ValueTask.CompletedTask;
}

internal sealed class NullabilityApplication : ConsoleApplication
{
    [Command] public string Required(string value) => value;
}

internal sealed class RenamedCommandApplication : ConsoleApplication
{
    [Command("renamed")] public int OriginalName(int value) => value;
}

internal sealed class ReturnObjectApplication : ConsoleApplication
{
    [Command]
    public TestReturnObject Object() => new()
    {
        Id = 7,
        Status = TestStatus.Running,
        Name = "hello: world",
        Text = "line",
        Time = DateTimeOffset.Parse("2026-09-08T17:00:00+00:00", CultureInfo.InvariantCulture)
    };
}

internal enum TestStatus
{
    Running,
    Completed
}

internal sealed class TestReturnObject : ReturnObjectBase
{
    public int Id { get; set; }
    public TestStatus Status { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public int? Optional { get; set; }
    public DateTimeOffset Time { get; set; }
}

internal sealed record ComplexObject(int Val1, int Val2, int Val3);
