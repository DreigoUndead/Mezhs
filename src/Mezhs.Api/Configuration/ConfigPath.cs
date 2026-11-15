namespace Mezhs.Configuration;

public static class ConfigPath
{
    // Default launches use an editable personal copy next to the tracked sample.
    // Explicit --config paths retain their exact existing meaning.
    public static string FindLocal(string[] args, string fileName, string argumentName = "--config")
    {
        var path = Find(args, fileName, argumentName);
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], argumentName, StringComparison.OrdinalIgnoreCase))
                return path;
        }
        return EnsureLocalCopy(path);
    }

    public static string EnsureLocalCopy(string defaultPath)
    {
        var localPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(defaultPath))!,
            Path.GetFileNameWithoutExtension(defaultPath) + ".local" + Path.GetExtension(defaultPath));
        if (!File.Exists(localPath))
        {
            try { File.Copy(defaultPath, localPath); }
            catch (IOException) when (File.Exists(localPath)) { /* Concurrent first startup. */ }
        }
        return localPath;
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
