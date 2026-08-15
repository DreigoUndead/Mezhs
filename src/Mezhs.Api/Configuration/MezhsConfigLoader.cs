using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mezhs.Configuration;

public static partial class MezhsConfigLoader
{
    public static MezhsOptions Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("MEŽS configuration file was not found.", path);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        var options = deserializer.Deserialize<MezhsOptions>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("MEŽS configuration is empty.");

        var configDirectory = Path.GetDirectoryName(path)!;
        options.Storage.Root = Resolve(configDirectory, options.Storage.Root);
        options.Transport.ElectronDirectory = Resolve(
            configDirectory,
            options.Transport.ElectronDirectory);
        Validate(options);
        return options;
    }

    private static void Validate(MezhsOptions options)
    {
        if (options.Version != 1)
            throw new InvalidOperationException($"Unsupported config version {options.Version}.");
        if (!string.Equals(options.Transport.Type, "electron", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The cross-platform API host currently supports transport.type: electron.");
        if (options.Transport.IdleMinutes < 0)
            throw new InvalidOperationException("transport.idleMinutes cannot be negative.");
        if (options.Connections.Count == 0)
            throw new InvalidOperationException("At least one connection must be configured.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var connection in options.Connections)
        {
            if (!SafeId().IsMatch(connection.Id))
                throw new InvalidOperationException(
                    $"Connection id '{connection.Id}' must contain only letters, numbers, '-' or '_'.");
            if (!ids.Add(connection.Id))
                throw new InvalidOperationException($"Duplicate connection id '{connection.Id}'.");
            if (string.IsNullOrWhiteSpace(connection.Name))
                connection.Name = connection.Id;
            if (string.IsNullOrWhiteSpace(connection.Integration))
                throw new InvalidOperationException(
                    $"Integration is required on connection '{connection.Id}'.");
        }
    }

    private static string Resolve(string baseDirectory, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex SafeId();
}
