using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mezhs.Agent.Configuration;

public static class YamlModelMapper
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public static T Map<T>(YamlNode node, string path)
    {
        var value = Deserializer.Deserialize<T>(Serialize(node))
            ?? throw new InvalidOperationException($"{path} configuration is empty.");
        Validate(value, path, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return value;
    }

    private static void Validate(object value, string path, HashSet<object> visited)
    {
        if (value is string || value.GetType().IsValueType || !visited.Add(value))
            return;

        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Value is null)
                    throw new InvalidOperationException($"{path}.{entry.Key} is required.");
                Validate(entry.Value, $"{path}.{entry.Key}", visited);
            }
            return;
        }

        var context = new ValidationContext(value);
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(value, context, results, validateAllProperties: true))
        {
            var error = results[0];
            var member = error.MemberNames.FirstOrDefault();
            var memberPath = string.IsNullOrWhiteSpace(member)
                ? path
                : $"{path}.{char.ToLowerInvariant(member[0])}{member[1..]}";
            throw new InvalidOperationException($"{memberPath}: {error.ErrorMessage}");
        }

        foreach (var property in value.GetType().GetProperties())
        {
            var child = property.GetValue(value);
            if (child is null || child is string)
                continue;
            var childPath = $"{path}.{char.ToLowerInvariant(property.Name[0])}{property.Name[1..]}";
            if (child is IEnumerable items)
            {
                var index = 0;
                foreach (var item in items)
                {
                    if (item is not null)
                        Validate(item, $"{childPath}[{index}]", visited);
                    index++;
                }
                continue;
            }
            Validate(child, childPath, visited);
        }
    }

    private static string Serialize(YamlNode node)
    {
        var yaml = new YamlStream(new YamlDocument(node));
        var buffer = new StringBuilder();
        using var writer = new StringWriter(buffer, CultureInfo.InvariantCulture);
        yaml.Save(writer, assignAnchors: false);
        return buffer.ToString();
    }
}
