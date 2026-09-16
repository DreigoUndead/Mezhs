namespace Mezhs.Common;

public static class TypeExtensions
{
    public static Type GetNotNullable(this Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Nullable.GetUnderlyingType(type) ?? type;
    }

    public static bool TryGetEnumerableElementType(this Type type, out Type elementType)
    {
        ArgumentNullException.ThrowIfNull(type);

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

        var candidates = new List<Type>();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            candidates.Add(type.GetGenericArguments()[0]);

        candidates.AddRange(type.GetInterfaces()
            .Where(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(candidate => candidate.GetGenericArguments()[0]));

        var distinct = candidates.Distinct().ToArray();
        if (distinct.Length == 1)
        {
            elementType = distinct[0];
            return true;
        }

        elementType = null!;
        return false;
    }
}
