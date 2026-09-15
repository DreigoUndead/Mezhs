using System.ComponentModel;
using System.Globalization;
using System.Reflection;

namespace Mezhs.Console;

internal static class ScalarConverter
{
    public static bool CanConvert(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsEnum ||
               typeof(IConvertible).IsAssignableFrom(type) ||
               FindParseMethod(type) is not null ||
               TypeDescriptor.GetConverter(type).CanConvertFrom(typeof(string));
    }

    public static object Parse(Type type, string value)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        try
        {
            if (type.IsEnum)
                return Enum.Parse(type, value, ignoreCase: true);

            if (typeof(IConvertible).IsAssignableFrom(type))
                return Convert.ChangeType(value, type, CultureInfo.CurrentCulture)!;

            if (FindParseMethod(type) is { } parseMethod)
                return parseMethod.Invoke(null, [value])
                    ?? throw new FormatException($"'{value}' cannot be converted to '{ValueBinder.FriendlyName(type)}'.");

            var converter = TypeDescriptor.GetConverter(type);
            if (converter.CanConvertFrom(typeof(string)))
                return converter.ConvertFromString(null, CultureInfo.CurrentCulture, value)
                    ?? throw new FormatException($"'{value}' cannot be converted to '{ValueBinder.FriendlyName(type)}'.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new FormatException($"'{value}' cannot be converted to '{ValueBinder.FriendlyName(type)}': {ex.InnerException.Message}", ex.InnerException);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidCastException or OverflowException or FormatException)
        {
            throw new FormatException($"'{value}' cannot be converted to '{ValueBinder.FriendlyName(type)}': {ex.Message}", ex);
        }

        throw new FormatException($"Type '{ValueBinder.FriendlyName(type)}' cannot be converted from text.");
    }

    public static string Format(object value, Type declaredType)
    {
        var type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (!type.IsInstanceOfType(value) && !declaredType.IsInstanceOfType(value))
            type = value.GetType();

        return value is IConvertible
            ? Convert.ToString(value, CultureInfo.CurrentCulture)
                ?? throw new FormatException($"Type '{ValueBinder.FriendlyName(type)}' cannot be formatted as a Console value.")
            : value.ToString()
                ?? throw new FormatException($"Type '{ValueBinder.FriendlyName(type)}' cannot be formatted as a Console value.");
    }

    private static MethodInfo? FindParseMethod(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method =>
                method.Name == "Parse" &&
                method.ReturnType == type &&
                method.GetParameters() is [{ ParameterType: var parameterType }] &&
                parameterType == typeof(string));
}
