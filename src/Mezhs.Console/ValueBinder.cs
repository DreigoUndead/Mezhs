using System.Collections;
using System.ComponentModel;
using System.Globalization;

namespace Mezhs.Console;

internal static class ValueBinder
{
    public static bool CanBind(Type type, out string? reason)
    {
        if (Nullable.GetUnderlyingType(type) is { } nullable)
            return CanBind(nullable, out reason);

        if (TryGetEnumerableElementType(type, out var elementType))
        {
            if (!CanBind(elementType, out reason))
                return false;

            if (CanMaterializeEnumerable(type, elementType))
            {
                reason = null;
                return true;
            }

            reason = $"Enumerable type '{FriendlyName(type)}' cannot be constructed from command input.";
            return false;
        }

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
                throw new FormatException($"Expected collection syntax for '{FriendlyName(type)}'.");
            return BindList(list, type, elementType);
        }

        if (node is not ScalarNode scalar)
            throw new FormatException($"Expected scalar value for '{FriendlyName(type)}'.");

        return ConvertScalar(scalar.Value, type);
    }

    public static string Describe(Type type, CommandSyntax syntax)
    {
        if (Nullable.GetUnderlyingType(type) is { } nullable)
            return $"{Describe(nullable, syntax)} | null";
        if (TryGetEnumerableElementType(type, out var item))
        {
            var collection = syntax.Tokens.First(x => x.Type == CommandSyntaxTokenType.Collection);
            return $"{collection.Start}{Describe(item, syntax)} ...{collection.End}";
        }
        return FriendlyName(type);
    }

    private static bool CanConvertScalar(Type type)
    {
        var converter = TypeDescriptor.GetConverter(type);
        return converter.CanConvertFrom(typeof(string)) || typeof(IConvertible).IsAssignableFrom(type);
    }

    private static object ConvertScalar(string value, Type type)
    {
        var converter = TypeDescriptor.GetConverter(type);
        if (converter.CanConvertFrom(typeof(string)))
            return converter.ConvertFromString(null, CultureInfo.CurrentCulture, value)
                ?? throw new FormatException($"'{value}' cannot be converted to '{FriendlyName(type)}'.");

        if (typeof(IConvertible).IsAssignableFrom(type))
            return Convert.ChangeType(value, type, CultureInfo.CurrentCulture)!;

        throw new FormatException($"Type '{FriendlyName(type)}' cannot be converted from text.");
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

        var enumerableType = typeof(IEnumerable<>).MakeGenericType(elementType);
        var enumerableCtor = targetType.GetConstructor([enumerableType]);
        if (enumerableCtor is not null)
            return enumerableCtor.Invoke([result]);

        throw new FormatException($"Enumerable type '{FriendlyName(targetType)}' cannot be constructed.");
    }

    private static bool CanMaterializeEnumerable(Type targetType, Type elementType)
    {
        if (targetType.IsArray)
            return true;

        var listType = typeof(List<>).MakeGenericType(elementType);
        if (targetType.IsAssignableFrom(listType))
            return true;

        var enumerableType = typeof(IEnumerable<>).MakeGenericType(elementType);
        return targetType.GetConstructor([enumerableType]) is not null;
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

        var enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
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
