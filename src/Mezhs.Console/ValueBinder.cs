using System.Collections;
using System.Globalization;
using System.Reflection;

namespace Mezhs.Console;

internal static class ValueBinder
{
    public static bool CanBind(Type type, out string? reason)
    {
        if (Nullable.GetUnderlyingType(type) is { } nullable)
            return CanBind(nullable, out reason);

        if (TryGetEnumerableElementType(type, out var elementType))
            return CanBind(elementType, out reason);

        if (CanConvertScalar(type))
        {
            reason = null;
            return true;
        }

        reason = $"Type '{FriendlyName(type)}' cannot be constructed from command input.";
        return false;
    }

    public static object? Bind(ValueNode node, Type type)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        if (node is ScalarNode { Value: var nullValue } && nullValue.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            if (!type.IsValueType || nullable is not null)
                return null;
            throw new FormatException($"null is not valid for non-nullable type '{FriendlyName(type)}'.");
        }

        if (nullable is not null)
            return Bind(node, nullable);

        if (TryGetEnumerableElementType(type, out var elementType))
        {
            if (node is not ListNode list)
                throw new FormatException($"Expected list syntax [...] for '{FriendlyName(type)}'.");
            return BindList(list, type, elementType);
        }

        if (node is not ScalarNode scalar)
            throw new FormatException($"Expected scalar value for '{FriendlyName(type)}'.");

        return ConvertScalar(scalar.Value, type);
    }

    public static string Describe(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } nullable)
            return $"{Describe(nullable)} | null";
        if (TryGetEnumerableElementType(type, out var item))
            return $"[{Describe(item)} ...]";
        if (type.IsEnum)
            return string.Join(" | ", Enum.GetNames(type));
        if (type == typeof(DateTime))
            return "yyyy-MM-ddTHH:mm:ss";
        if (type == typeof(DateTimeOffset))
            return "yyyy-MM-ddTHH:mm:sszzz";
        if (type == typeof(TimeSpan))
            return "[-][d.]hh:mm:ss[.fffffff]";
        if (type == typeof(Guid))
            return "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx";
        if (type == typeof(string))
            return "text; quote with \"...\" when it contains whitespace";
        return FriendlyName(type);
    }

    private static bool CanConvertScalar(Type type)
    {
        if (type == typeof(string) || type.IsEnum || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan))
            return true;
        if (type == typeof(object))
            return false;
        return typeof(IConvertible).IsAssignableFrom(type);
    }

    private static object ConvertScalar(string value, Type type)
    {
        if (type == typeof(string)) return value;
        if (type.IsEnum) return Enum.Parse(type, value, true);
        if (type == typeof(Guid)) return Guid.Parse(value);
        if (type == typeof(DateTime)) return DateTime.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None);
        if (type == typeof(DateTimeOffset)) return DateTimeOffset.ParseExact(value, "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None);
        if (type == typeof(TimeSpan)) return TimeSpan.Parse(value, CultureInfo.InvariantCulture);
        return Convert.ChangeType(value, type, CultureInfo.InvariantCulture)!;
    }

    private static object BindList(ListNode list, Type targetType, Type elementType)
    {
        var array = Array.CreateInstance(elementType, list.Items.Count);
        for (var i = 0; i < list.Items.Count; i++)
            array.SetValue(Bind(list.Items[i], elementType), i);

        if (targetType.IsArray)
            return array;

        var listType = typeof(List<>).MakeGenericType(elementType);
        var result = (IList)Activator.CreateInstance(listType)!;
        foreach (var value in array)
            result.Add(value);

        if (targetType.IsAssignableFrom(listType))
            return result;

        var enumerableCtor = targetType.GetConstructor([typeof(IEnumerable<>).MakeGenericType(elementType)]);
        if (enumerableCtor is not null)
            return enumerableCtor.Invoke([result]);

        throw new FormatException($"Enumerable type '{FriendlyName(targetType)}' cannot be constructed.");
    }

    private static bool TryGetEnumerableElementType(Type type, out Type elementType)
    {
        if (type == typeof(string))
        {
            elementType = null!;
            return false;
        }
        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        var enumerable = (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ? type
            : type.GetInterfaces().FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerable is null)
        {
            elementType = null!;
            return false;
        }

        elementType = enumerable.GetGenericArguments()[0];
        return true;
    }

    public static string FriendlyName(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;
        var name = type.Name[..type.Name.IndexOf('`')];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(FriendlyName))}>";
    }
}
