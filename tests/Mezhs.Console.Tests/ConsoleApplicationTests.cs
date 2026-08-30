using System.Globalization;
using Mezhs.Console;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Mezhs.Console.Tests;

public sealed class ConsoleApplicationTests
{
    [Fact]
    public void CallerMemberName_becomes_command_name()
    {
        var result = Run("Help");
        Assert.Contains("Echo", result.Out);
    }

    [Fact]
    public void Explicit_command_name_is_used()
    {
        var result = Run("renamed 5");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("5", result.Out);
    }

    [Fact]
    public void Missing_nullable_parameter_becomes_null()
    {
        var result = Run("Echo hello");
        Assert.Contains("hello:null", result.Out);
    }

    [Fact]
    public void Literal_null_binds_to_nullable_parameter()
    {
        var result = Run("Echo hello null");
        Assert.Contains("hello:null", result.Out);
    }

    [Fact]
    public void Collection_binds_to_IEnumerable()
    {
        var result = Run("Insert [1 5 6] tail");
        Assert.Contains("1,5,6|tail", result.Out);
    }

    [Fact]
    public void Nested_collection_binds_recursively()
    {
        var result = Run("Nested [[1 2] [3 4]]");
        Assert.Contains("1,2;3,4", result.Out);
    }

    [Fact]
    public void Quoted_text_is_one_scalar()
    {
        var result = Run("Echo \"hello world\"");
        Assert.Contains("hello world:null", result.Out);
    }

    [Fact]
    public void Invalid_command_is_reported_in_help()
    {
        var result = Run("Help Broken");
        Assert.Contains("ERROR:", result.Out);
        Assert.Contains("ComplexObject", result.Out);
    }

    [Fact]
    public void Invalid_command_does_not_break_valid_commands()
    {
        Assert.Equal(0, Run("Echo ok").ExitCode);
        Assert.Equal(3, Run("Broken nope").ExitCode);
    }

    [Fact]
    public void Validate_is_an_inherited_command()
    {
        var result = Run("Validate");
        Assert.Contains("Broken: INVALID", result.Out);
    }

    [Fact]
    public void Help_is_an_inherited_command()
    {
        var result = Run("Help Help");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Show available commands", result.Out);
    }

    [Fact]
    public void Current_culture_is_used_for_decimal_conversion()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("lv-LV");
            var result = Run("Decimal 1,5");
            Assert.Contains("1,5", result.Out);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void Help_reports_current_os_culture_formats()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("lv-LV");
            var result = Run("Help Date");
            Assert.Contains("OS culture: lv-LV", result.Out);
            Assert.Contains(CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern, result.Out);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void Syntax_tokens_can_be_replaced_by_derived_application()
    {
        var result = Run("Insert (1 2 3) tail", new AlternateSyntaxApplication());
        Assert.Contains("1,2,3|tail", result.Out);
    }

    [Fact]
    public void Missing_execution_context_is_visible_but_does_not_fail()
    {
        var old = Environment.GetEnvironmentVariable(MezhsExecutionContext.ExecutionIdVariable);
        try
        {
            Environment.SetEnvironmentVariable(MezhsExecutionContext.ExecutionIdVariable, null);
            var result = Run("Echo ok", setContext: false);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("No execution context found", result.Error);
        }
        finally { Environment.SetEnvironmentVariable(MezhsExecutionContext.ExecutionIdVariable, old); }
    }

    private static Result Run(string command, TestApplication? app = null, bool setContext = true)
    {
        var oldOut = Console.Out;
        var oldError = Console.Error;
        var oldExecution = Environment.GetEnvironmentVariable(MezhsExecutionContext.ExecutionIdVariable);
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            if (setContext) Environment.SetEnvironmentVariable(MezhsExecutionContext.ExecutionIdVariable, "test");
            return new Result((app ?? new TestApplication()).Run(command), output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
            Environment.SetEnvironmentVariable(MezhsExecutionContext.ExecutionIdVariable, oldExecution);
        }
    }

    private sealed record Result(int ExitCode, string Out, string Error);

    private class TestApplication : ConsoleApplication
    {
        [Command] public string Echo(string value, int? count = null) => $"{value}:{count?.ToString() ?? "null"}";
        [Command] public string Insert(IEnumerable<int> values, string tail) => $"{string.Join(',', values)}|{tail}";
        [Command] public string Nested(IEnumerable<IEnumerable<int>> values) => string.Join(';', values.Select(x => string.Join(',', x)));
        [Command] public decimal Decimal(decimal value) => value;
        [Command] public DateTime Date(DateTime value) => value;
        [Command("renamed")] public int OriginalName(int value) => value;
        [Command] public void Broken(ComplexObject value) { }
    }

    private sealed class AlternateSyntaxApplication : TestApplication
    {
        protected override CommandSyntax Syntax => new([
            new(CommandSyntaxTokenType.Quote, '\'', '\'', '\\'),
            new(CommandSyntaxTokenType.Collection, '(', ')')
        ]);
    }

    private sealed record ComplexObject(int A, int B);
}
