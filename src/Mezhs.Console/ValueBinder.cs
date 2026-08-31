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

        if (type == typeof(object))
        {
            reason = null;
            return true;
        }

        if (TryGetDictionaryTypes(type, out var keyType, out var valueType))
        {
            if (!CanBind(keyType, out reason) || !CanBind(valueType, out reason))
                return false;

            if (CanMaterializeDictionary(type, keyType, valueType))
            {
                reason = null;
                return true;
            }

            reason = $"Dictionary type '{FriendlyName(type)}' cannot be constructed from command input.";
            return false;
        }

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
        if (node is ScalarNode { Value: var nullValue, IsQuoted: false } &&
            nullValue.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            if (!type.IsValueType || nullable is not null)
                return null;
            throw new FormatException($"null is not valid for non-nullable type '{FriendlyName(type)}'.");
        }

        if (nullable is not null)
            return Bind(node, nullable);

        if (type == typeof(object))
            return BindDynamic(node);

        if (TryGetDictionaryTypes(type, out var keyType, out var valueType))
        {
            if (node is not ObjectNode map)
                throw new FormatException($"Expected object syntax for '{FriendlyName(type)}'.");
            return BindDictionary(map, type, keyType, valueType);
        }

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
        if (type == typeof(object))
            return "Object";
        if (TryGetDictionaryTypes(type, out var keyType, out var valueType))
        {
            var map = syntax.Tokens.FirstOrDefault(x => x.Type == CommandSyntaxTokenType.Object);
            return map is null
                ? $"Dictionary<{Describe(keyType, syntax)}, {Describe(valueType, syntax)}>"
                : $"{map.Start}{Describe(keyType, syntax)}:{Describe(valueType, syntax)} ...{map.End}";
        }
        if (TryGetEnumerableElementType(type, out var item))
        {
            var collection = syntax.Tokens.First(x => x.Type == CommandSyntaxTokenType.Collection);
            return $"{collection.Start}{Describe(item, syntax)} ...{collection.End}";
        }
        return FriendlyName(type);
    }

    private static object? BindDynamic(ValueNode node)
    {
        return node switch
        {
            ScalarNode { IsQuoted: true } scalar => scalar.Value,
            ScalarNode scalar => InferScalar(scalar.Value),
            ListNode list => list.Items.Select(BindDynamic).ToArray(),
            ObjectNode map => map.Properties.ToDictionary(x => x.Key, x => BindDynamic(x.Value), StringComparer.OrdinalIgnoreCase),
            _ => throw new FormatException("Unsupported dynamic value.")
        };
    }

    private static object? InferScalar(string value)
    {
        if (value.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
        if (bool.TryParse(value, out var boolean)) return boolean;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var integer)) return integer;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var number)) return number;
        return value;
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

    private static object BindDictionary(ObjectNode map, Type targetType, Type keyType, Type valueType)
    {
        var dictionaryType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
        var result = Activator.CreateInstance(dictionaryType)!;
        var add = dictionaryType.GetMethod("Add", [keyType, valueType])!;

        foreach (var property in map.Properties)
        {
            var key = ConvertScalar(property.Key, keyType);
            var value = Bind(property.Value, valueType);
            add.Invoke(result, [key, value]);
        }

        if (targetType.IsAssignableFrom(dictionaryType))
            return result;

        var dictionaryInterface = typeof(IDictionary<,>).MakeGenericType(keyType, valueType);
        var constructor = targetType.GetConstructor([dictionaryInterface]);
        if (constructor is not null)
            return constructor.Invoke([result]);

        throw new FormatException($"Dictionary type '{FriendlyName(targetType)}' cannot be constructed.");
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

    private static bool CanMaterializeDictionary(Type targetType, Type keyType, Type valueType)
    {
        var dictionaryType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
        if (targetType.IsAssignableFrom(dictionaryType))
            return true;

        var dictionaryInterface = typeof(IDictionary<,>).MakeGenericType(keyType, valueType);
        return targetType.GetConstructor([dictionaryInterface]) is not null;
    }

    private static bool TryGetDictionaryTypes(Type type, out Type keyType, out Type valueType)
    {
        var dictionary = IsDictionaryInterface(type)
            ? type
            : type.GetInterfaces().FirstOrDefault(IsDictionaryInterface);

        if (dictionary is null)
        {
            keyType = null!;
            valueType = null!;
            return false;
        }

        var arguments = dictionary.GetGenericArguments();
        keyType = arguments[0];
        valueType = arguments[1];
        return true;
    }

    private static bool IsDictionaryInterface(Type type) =>
        type.IsGenericType &&
        (type.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
         type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));

    private static bool TryGetEnumerableElementType(Type type, out Type elementType)
    {
        if (type == typeof(string) || TryGetDictionaryTypes(type, out _, out _))
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
