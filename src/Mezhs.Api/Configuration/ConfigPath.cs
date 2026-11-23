namespace Mezhs.Configuration;

public static class ConfigPath
{
    // Default launches opt into overrides; explicit paths retain their exact existing meaning.
    public static ConfigSource FindWithOverride(
        string[] args, string fileName, string argumentName = "--config")
    {
        var path = Find(args, fileName, argumentName);
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], argumentName, StringComparison.OrdinalIgnoreCase))
                return new(path);
        }
        return new(path, Overridable: true);
    }

    public static string Find(string[] args, string fileName, string argumentName = "--config")
    {
        string? configuredPath = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], argumentName, StringComparison.OrdinalIgnoreCase))
            {
                configuredPath = args[i + 1];
                break;
            }
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
            return Path.GetFullPath(configuredPath);

        var currentCandidate = Path.GetFullPath(fileName);
        if (File.Exists(currentCandidate))
            return currentCandidate;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Mezhs.sln")))
            {
                var repositoryCandidate = Path.Combine(directory.FullName, fileName);
                if (File.Exists(repositoryCandidate))
                    return repositoryCandidate;
            }
            directory = directory.Parent;
        }

        var outputCandidate = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(outputCandidate))
            return outputCandidate;

        return currentCandidate;
    }
}

public sealed record ConfigSource(string Path, bool Overridable = false)
{
    public override string ToString() => Overridable ? $"{Path} + override" : Path;
}
