namespace Mezhs.Log.Data;

public sealed record LogEntry(
    int Id,
    DateTimeOffset Time,
    string Text);
