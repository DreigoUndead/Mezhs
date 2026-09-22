using System.Globalization;
using System.Reflection;

namespace Mezhs.Common;

public static class Cast
{
    public static bool CanParse(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        type = type.GetNotNullable();
        return type.IsEnum ||
               typeof(IConvertible).IsAssignableFrom(type) ||
               FindParseMethod(type) is not null;
    }

    public static object Parse(Type type, string value, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(value);

        var targetType = type.GetNotNullable();
        provider ??= CultureInfo.CurrentCulture;

        try
        {
            if (targetType.IsEnum)
                return Enum.Parse(targetType, value, ignoreCase: true);

            if (typeof(IConvertible).IsAssignableFrom(targetType))
                return Convert.ChangeType(value, targetType, provider)!;

            var parseMethod = FindParseMethod(targetType);
            if (parseMethod is not null)
                return parseMethod.Invoke(null, [value])
                    ?? throw new FormatException($"'{value}' cannot be converted to '{targetType.Name}'.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new FormatException($"'{value}' cannot be converted to '{targetType.Name}': {ex.InnerException.Message}", ex.InnerException);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidCastException or OverflowException or FormatException)
        {
            throw new FormatException($"'{value}' cannot be converted to '{targetType.Name}': {ex.Message}", ex);
        }

        throw new NotSupportedException($"Type '{targetType.Name}' does not expose a supported text conversion.");
    }

    public static T Parse<T>(string value, IFormatProvider? provider = null) =>
        (T)Parse(typeof(T), value, provider);

    public static string Format(object value, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToString(value, provider ?? CultureInfo.CurrentCulture)
            ?? throw new FormatException($"Type '{value.GetType().Name}' cannot be formatted as text.");
    }

    private static MethodInfo? FindParseMethod(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method =>
            {
                var parameters = method.GetParameters();
                return method.Name == "Parse" &&
                       method.ReturnType == type &&
                       parameters.Length == 1 &&
                       parameters[0].ParameterType == typeof(string);
            });
}
