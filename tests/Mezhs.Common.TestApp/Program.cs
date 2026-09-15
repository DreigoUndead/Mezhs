using System.ComponentModel;
using System.Globalization;
using Mezhs.Common;

var tests = new (string Name, Action Body)[]
{
    ("Convertible scalar", () => Equal(42, Cast.Parse<int>("42"))),
    ("Enum scalar", () => Equal(TestStatus.Running, Cast.Parse<TestStatus>("running"))),
    ("Static Parse scalar", () => Equal(new ParsedValue(17), Cast.Parse<ParsedValue>("17"))),
    ("Scalar format", () => Equal("17", Cast.Format(new ParsedValue(17), CultureInfo.InvariantCulture))),
    ("Invalid scalar fails", TestInvalidScalar),
    ("No TypeConverter fallback", TestNoTypeConverterFallback),
    ("Nullable type", () => Equal(typeof(int), typeof(int?).GetNotNullable())),
    ("Enumerable element type", TestEnumerableElementType),
    ("Registry prefix lookup", TestRegistryPrefixLookup),
    ("Registry overwrite", TestRegistryOverwrite),
    ("Registry remove and prune", TestRegistryRemoveAndPrune),
    ("Registry prefix clear", TestRegistryPrefixClear),
    ("Registry five dimensions", TestRegistryFiveDimensions),
    ("Registry concurrent writes", TestRegistryConcurrentWrites)
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
        var failure = $"FAIL: {test.Name}: {ex.Message}";
        failures.Add(failure);
        Console.WriteLine(failure);
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count}/{tests.Length} tests failed.");
    return 1;
}

Console.WriteLine($"PASS: {tests.Length}/{tests.Length} tests");
return 0;

static void TestInvalidScalar()
{
    try
    {
        Cast.Parse<TestStatus>("missing");
        throw new InvalidOperationException("Invalid enum value was accepted.");
    }
    catch (FormatException)
    {
    }
}

static void TestNoTypeConverterFallback()
{
    if (Cast.CanParse(typeof(ConverterOnly)))
        throw new InvalidOperationException("TypeConverter-only type was reported as supported.");

    try
    {
        Cast.Parse(typeof(ConverterOnly), "works");
        throw new InvalidOperationException("TypeConverter fallback was used unexpectedly.");
    }
    catch (NotSupportedException)
    {
    }
}

static void TestEnumerableElementType()
{
    if (!typeof(List<string>).TryGetEnumerableElementType(out var listType) || listType != typeof(string))
        throw new InvalidOperationException("List element type was not resolved.");
    if (!typeof(int[]).TryGetEnumerableElementType(out var arrayType) || arrayType != typeof(int))
        throw new InvalidOperationException("Array element type was not resolved.");
    if (typeof(string).TryGetEnumerableElementType(out _))
        throw new InvalidOperationException("String was treated as a collection value.");
}

static void TestRegistryPrefixLookup()
{
    var registry = new Registry<string, int, string>();
    registry.Set("browser-a", 1, "a1");
    registry.Set("browser-a", 2, "a2");
    registry.Set("browser-b", 1, "b1");

    Equal("a1", registry.Get("browser-a", 1));
    SetEqual(["a1", "a2"], registry.GetRange("browser-a"));
    SetEqual(["a1", "a2", "b1"], registry.GetRange());
    SetEqual(["browser-a", "browser-b"], registry.GetKeys());
    SetEqual([1, 2], registry.GetKeys("browser-a"));
}

static void TestRegistryOverwrite()
{
    var registry = new Registry<string, int, string>();
    registry.Set("browser", 1, "old");
    registry.Set("browser", 1, "new");
    Equal("new", registry.Get("browser", 1));
    Equal(1, registry.GetRange().Count);
}

static void TestRegistryRemoveAndPrune()
{
    var registry = new Registry<string, int, string>();
    registry.Set("browser", 1, "value");

    if (!registry.Remove("browser", 1))
        throw new InvalidOperationException("Existing value was not removed.");
    if (registry.Remove("browser", 1))
        throw new InvalidOperationException("Missing value reported as removed.");
    if (registry.GetKeys().Count != 0)
        throw new InvalidOperationException("Empty registry branch was not pruned.");
}

static void TestRegistryPrefixClear()
{
    var registry = new Registry<string, int, string>();
    registry.Set("browser-a", 1, "a1");
    registry.Set("browser-a", 2, "a2");
    registry.Set("browser-b", 1, "b1");

    if (!registry.Clear("browser-a"))
        throw new InvalidOperationException("Existing prefix was not cleared.");
    SetEqual(["b1"], registry.GetRange());
    if (registry.Clear("missing"))
        throw new InvalidOperationException("Missing prefix reported as cleared.");
}

static void TestRegistryFiveDimensions()
{
    var registry = new Registry<int, int, int, int, int, string>();
    registry.Set(1, 2, 3, 4, 5, "value");
    Equal("value", registry.Get(1, 2, 3, 4, 5));
    SetEqual(["value"], registry.GetRange(1, 2, 3, 4));
}

static void TestRegistryConcurrentWrites()
{
    var registry = new Registry<int, int, string>();
    Parallel.For(0, 200, index => registry.Set(index / 20, index, index.ToString(CultureInfo.InvariantCulture)));

    Equal(200, registry.GetRange().Count);
    for (var bucket = 0; bucket < 10; bucket++)
        Equal(20, registry.GetRange(bucket).Count);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}' but got '{actual}'.");
}

static void SetEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
{
    var expectedSet = expected.ToHashSet();
    var actualSet = actual.ToHashSet();
    if (!expectedSet.SetEquals(actualSet))
        throw new InvalidOperationException($"Expected [{string.Join(", ", expectedSet)}] but got [{string.Join(", ", actualSet)}].");
}

internal enum TestStatus
{
    Running,
    Completed
}

internal sealed record ParsedValue(int Value)
{
    public static ParsedValue Parse(string value) => new(int.Parse(value, CultureInfo.InvariantCulture));
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

[TypeConverter(typeof(ConverterOnlyTypeConverter))]
internal sealed class ConverterOnly
{
}

internal sealed class ConverterOnlyTypeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) =>
        value is string ? new ConverterOnly() : base.ConvertFrom(context, culture, value);
}
