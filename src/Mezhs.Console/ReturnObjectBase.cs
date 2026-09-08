using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Mezhs.Console;

public abstract class ReturnObjectBase
{
    public const string SeparatorLine = "------------------------------------";

    public override string ToString()
    {
        var result = new StringBuilder();
        foreach (var property in SerializableProperties(GetType()))
        {
            if (result.Length > 0)
                result.AppendLine();
            result.Append(property.Name)
                .Append(": ")
                .Append(FormatValue(property.GetValue(this), property.PropertyType));
        }
        return result.ToString();
    }

    public static T Parse<T>(string value)
        where T : ReturnObjectBase, new()
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = new T();
        var properties = SerializableProperties(typeof(T))
            .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var record in SplitPropertyRecords(value))
        {
            var separator = FindStructuralColon(record);
            if (separator < 1)
                throw new FormatException($"Invalid return-object property line '{FirstLine(record)}'.");

            var name = record[..separator].Trim();
            var propertyValue = record[(separator + 1)..].TrimStart();
            if (!properties.TryGetValue(name, out var property))
                continue;
            if (propertyValue.Length == 0)
                throw new FormatException($"Property '{property.Name}' has no value.");

            var nodes = CommandLineParser.Parse(propertyValue, CommandSyntax.Default);
            if (nodes.Count != 1)
                throw new FormatException($"Property '{property.Name}' must contain exactly one value.");
            try
            {
                property.SetValue(result, ValueBinder.Bind(nodes[0], property.PropertyType));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or TargetInvocationException)
            {
                throw new FormatException($"Invalid value for property '{property.Name}': {ex.InnerException?.Message ?? ex.Message}", ex);
            }
        }

        return result;
    }

    public static IReadOnlyList<T> ParseMany<T>(string value)
        where T : ReturnObjectBase, new() =>
        SplitObjects(value).Select(Parse<T>).ToArray();

    public static string FormatMany(IEnumerable<ReturnObjectBase> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return string.Join(
            $"{Environment.NewLine}{Environment.NewLine}{SeparatorLine}{Environment.NewLine}{Environment.NewLine}",
            values.Select(value => value.ToString()));
    }

    private static IReadOnlyList<PropertyInfo> SerializableProperties(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetMethod is not null && property.SetMethod is not null && property.GetIndexParameters().Length == 0)
            .OrderBy(property => property.MetadataToken)
            .ToArray();

    private static string FormatValue(object? value, Type declaredType)
    {
        if (value is null)
            return "null";

        var type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (type == typeof(string))
            return Quote((string)value);
        if (type == typeof(char))
            return Quote(value.ToString()!);
        if (type == typeof(DateTimeOffset))
            return ((DateTimeOffset)value).ToString("O", CultureInfo.InvariantCulture);
        if (type == typeof(DateTime))
            return ((DateTime)value).ToString("O", CultureInfo.InvariantCulture);
        if (type == typeof(TimeSpan))
            return ((TimeSpan)value).ToString("c", CultureInfo.InvariantCulture);
        if (type.IsEnum)
            return value.ToString()!;

        if (value is IEnumerable enumerable and not string)
        {
            var items = new List<string>();
            Type itemType = typeof(object);
            if (type.IsArray)
                itemType = type.GetElementType()!;
            else
            {
                var enumerableType = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                    ? type
                    : type.GetInterfaces().FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
                if (enumerableType is not null)
                    itemType = enumerableType.GetGenericArguments()[0];
            }
            foreach (var item in enumerable)
                items.Add(FormatValue(item, itemType));
            return $"[{string.Join(' ', items)}]";
        }

        return Convert.ToString(value, CultureInfo.CurrentCulture)
            ?? throw new FormatException($"Type '{type.Name}' cannot be formatted as a Console value.");
    }

    private static string Quote(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static IReadOnlyList<string> SplitPropertyRecords(string value)
    {
        var records = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;
        var escaped = false;
        var collectionDepth = 0;

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (inQuote)
            {
                current.Append(character);
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (character == '"')
                    inQuote = false;
                continue;
            }

            if (character == '"')
            {
                inQuote = true;
                current.Append(character);
                continue;
            }
            if (character == '[')
                collectionDepth++;
            else if (character == ']' && collectionDepth > 0)
                collectionDepth--;

            if ((character == '\r' || character == '\n') && collectionDepth == 0)
            {
                if (character == '\r' && index + 1 < value.Length && value[index + 1] == '\n')
                    index++;
                AddRecord(records, current);
                continue;
            }
            current.Append(character);
        }

        if (inQuote)
            throw new FormatException("Unterminated quoted value in return object.");
        if (collectionDepth != 0)
            throw new FormatException("Unterminated collection value in return object.");
        AddRecord(records, current);
        return records;
    }

    private static void AddRecord(List<string> records, StringBuilder current)
    {
        if (current.Length > 0 && !string.IsNullOrWhiteSpace(current.ToString()))
            records.Add(current.ToString());
        current.Clear();
    }

    private static int FindStructuralColon(string record)
    {
        var inQuote = false;
        var escaped = false;
        var depth = 0;
        for (var index = 0; index < record.Length; index++)
        {
            var character = record[index];
            if (inQuote)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (character == '\\')
                    escaped = true;
                else if (character == '"')
                    inQuote = false;
                continue;
            }
            if (character == '"')
            {
                inQuote = true;
                continue;
            }
            if (character == '[')
                depth++;
            else if (character == ']' && depth > 0)
                depth--;
            else if (character == ':' && depth == 0)
                return index;
        }
        return -1;
    }

    private static IReadOnlyList<string> SplitObjects(string value)
    {
        var objects = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;
        var escaped = false;
        var position = 0;

        while (position < value.Length)
        {
            var lineStart = position;
            var lineEnd = value.IndexOf('\n', position);
            if (lineEnd < 0)
                lineEnd = value.Length;
            var rawLine = value[lineStart..lineEnd];
            var line = rawLine.TrimEnd('\r');

            if (!inQuote && line.Trim().Equals(SeparatorLine, StringComparison.Ordinal))
            {
                AddObject(objects, current);
            }
            else
            {
                if (current.Length > 0)
                    current.Append('\n');
                current.Append(line);
                UpdateQuoteState(line, ref inQuote, ref escaped);
            }
            position = lineEnd < value.Length ? lineEnd + 1 : value.Length;
        }

        if (inQuote)
            throw new FormatException("Unterminated quoted value in return-object list.");
        AddObject(objects, current);
        return objects;
    }

    private static void UpdateQuoteState(string line, ref bool inQuote, ref bool escaped)
    {
        foreach (var character in line)
        {
            if (inQuote)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (character == '\\')
                    escaped = true;
                else if (character == '"')
                    inQuote = false;
            }
            else if (character == '"')
            {
                inQuote = true;
            }
        }
        escaped = false;
    }

    private static void AddObject(List<string> objects, StringBuilder current)
    {
        var text = current.ToString().Trim();
        if (text.Length > 0)
            objects.Add(text);
        current.Clear();
    }

    private static string FirstLine(string value)
    {
        var index = value.IndexOfAny(['\r', '\n']);
        return index < 0 ? value : value[..index];
    }
}
