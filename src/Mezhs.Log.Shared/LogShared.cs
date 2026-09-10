namespace Mezhs.Log.Shared;

public sealed class LogShared
{
    public const string RootEnvironmentVariable = "MEZHS_LOG_ROOT";

    public LogShared(string? root = null)
    {
        var configured = string.IsNullOrWhiteSpace(root)
            ? Environment.GetEnvironmentVariable(RootEnvironmentVariable)
            : root;
        Root = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Notes")
            : configured);
    }

    public string Root { get; }

    public string Resolve(string file)
    {
        if (string.IsNullOrWhiteSpace(file))
            throw new ArgumentException("Log file is required.", nameof(file));

        var path = Path.GetFullPath(Path.Combine(Root, file));
        var rootPrefix = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(rootPrefix, comparison) && !string.Equals(path, Root, comparison))
            throw new ArgumentException("Log file must stay inside the configured log root.", nameof(file));
        return path;
    }

    public string GetNotesPath(string file) => Resolve(file) + ".notes.md";

    public string? GetNotes(string file)
    {
        var path = GetNotesPath(file);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}
