namespace Mezhs.Services;

public static class ChatTitle
{
    public static string? FromPrompt(string? prompt)
    {
        if (prompt is null) return null;
        var prefix = prompt.AsSpan(0, Math.Min(prompt.Length, 200));
        var newline = prefix.IndexOfAny('\r', '\n');
        var title = (newline < 0 ? prefix : prefix[..newline]).Trim();
        return title.IsEmpty ? null : title.ToString();
    }
}
