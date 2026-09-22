namespace Mezhs.WhatsApp;

public sealed class WhatsAppAccountStatus
{
    public string State { get; set; } = string.Empty;
    public bool Connected { get; set; }
    public bool QrAvailable { get; set; }
    public string? AccountId { get; set; }
}

public sealed class WhatsAppChat
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public int? UnreadCount { get; set; }
    public DateTimeOffset? ConversationTimestamp { get; set; }
}

public sealed class WhatsAppMessage
{
    public string Id { get; set; } = string.Empty;
    public string? ChatId { get; set; }
    public string? ParticipantId { get; set; }
    public bool FromMe { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public string? Type { get; set; }
    public string? Text { get; set; }
}
