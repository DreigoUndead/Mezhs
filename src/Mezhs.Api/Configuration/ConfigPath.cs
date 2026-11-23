namespace Mezhs.Configuration;

public static class ConfigPath
{
    private const string OverrideComment = "# Add personal overrides for the adjacent base configuration here.\n";

    // Default launches use a sparse personal override next to the tracked sample.
    // Explicit --config paths retain their exact existing meaning.
    public static ConfigSource FindWithOverride(
        string[] args, string fileName, string argumentName = "--config", string? companionFileName = null)
    {
        var path = Find(args, fileName, argumentName);
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], argumentName, StringComparison.OrdinalIgnoreCase))
                return new(path);
        }

        var overridePath = EnsureOverride(path);
        if (companionFileName is not null)
            EnsureOverride(Path.Combine(Path.GetDirectoryName(path)!, companionFileName));
        return new(path, overridePath);
    }

    public static string EnsureOverride(string defaultPath)
    {
        var overridePath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(defaultPath))!,
            Path.GetFileNameWithoutExtension(defaultPath) + ".override" + Path.GetExtension(defaultPath));
        if (!File.Exists(overridePath))
        {
            try
            {
                using var file = new FileStream(overridePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(file);
                writer.Write(OverrideComment);
            }
            catch (IOException) when (File.Exists(overridePath)) { /* Concurrent first startup. */ }
        }
        return overridePath;
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

public sealed record ConfigSource(string Path, string? OverridePath = null)
{
    public override string ToString() => OverridePath is null ? Path : $"{Path} + {OverridePath}";
}
