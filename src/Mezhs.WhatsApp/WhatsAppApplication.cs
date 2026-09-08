using Mezhs.Console;

namespace Mezhs.WhatsApp;

internal sealed partial class WhatsAppApplication : ConsoleApplication
{
    public const string IdentityVariable = "MEZHS_WHATSAPP_IDENTITY";

    private readonly WhatsAppApiClient _client;
    private readonly Func<string?> _identityProvider;

    public WhatsAppApplication()
        : this(new WhatsAppApiClient(), () => Environment.GetEnvironmentVariable(IdentityVariable))
    {
    }

    internal WhatsAppApplication(WhatsAppApiClient client, Func<string?> identityProvider)
    {
        _client = client;
        _identityProvider = identityProvider;
    }

    [Command(Description = "Get WhatsApp account connection status.")]
    public WhatsAppAccountStatus Status() => _client.Status();

    [Command(Description = "Connect the configured WhatsApp linked-device session.")]
    public WhatsAppAccountStatus Connect() => _client.Connect();

    [Command(Description = "Get the current pairing QR as SVG text when pairing is required.")]
    public string Qr() => _client.Qr();

    [Command(Description = "Disconnect the WhatsApp gateway without deleting the linked-device session.")]
    public WhatsAppAccountStatus Disconnect() => _client.Disconnect();

    [Command(Description = "Log out and delete the local WhatsApp linked-device session.")]
    public WhatsAppAccountStatus DeleteSession() => _client.DeleteSession();

    [Command(Description = "List known WhatsApp chats.")]
    public IReadOnlyList<WhatsAppChat> Chats() => _client.Chats();

    [Command(Description = "Get one WhatsApp message by id. Supply chatId when the id may be ambiguous.", Example = "Get ABC123 37120000000@s.whatsapp.net")]
    public WhatsAppMessage Get(string messageId, string? chatId = null)
    {
        Require(messageId, nameof(messageId));
        if (chatId is not null)
            Require(chatId, nameof(chatId));
        return _client.Get(messageId, chatId);
    }

    [Command(Description = "Get the newest messages from one WhatsApp chat.", Example = "GetLast 37120000000@s.whatsapp.net 10")]
    public IReadOnlyList<WhatsAppMessage> GetLast(string chatId, int count = 10)
    {
        Require(chatId, nameof(chatId));
        ValidateCount(count);
        return _client.GetLast(chatId, count);
    }

    [Command(Description = "Get messages before a known message in one WhatsApp chat.", Example = "GetBefore 37120000000@s.whatsapp.net ABC123 20")]
    public IReadOnlyList<WhatsAppMessage> GetBefore(string chatId, string messageId, int count = 20)
    {
        Require(chatId, nameof(chatId));
        Require(messageId, nameof(messageId));
        ValidateCount(count);
        return _client.GetBefore(chatId, messageId, count);
    }

    [Command(Description = "Get messages after a known message in one WhatsApp chat.", Example = "GetAfter 37120000000@s.whatsapp.net ABC123 20")]
    public IReadOnlyList<WhatsAppMessage> GetAfter(string chatId, string messageId, int count = 20)
    {
        Require(chatId, nameof(chatId));
        Require(messageId, nameof(messageId));
        ValidateCount(count);
        return _client.GetAfter(chatId, messageId, count);
    }

    [Command(Description = "Search text messages within one WhatsApp chat.", Example = "Search 37120000000@s.whatsapp.net \"watering reminder\" 20")]
    public IReadOnlyList<WhatsAppMessage> Search(string chatId, string query, int count = 20)
    {
        Require(chatId, nameof(chatId));
        Require(query, nameof(query));
        ValidateCount(count);
        return _client.Search(chatId, query, count);
    }

    [Command(Description = "Send a text message. MEZHS_WHATSAPP_IDENTITY is prefixed automatically when configured.", Example = "Send 37120000000@s.whatsapp.net \"hello\"")]
    public WhatsAppMessage Send(string chatId, string text)
    {
        Require(chatId, nameof(chatId));
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("text must not be empty or whitespace.", nameof(text));

        var identity = _identityProvider()?.Trim();
        var outgoing = string.IsNullOrEmpty(identity) ? text : $"[{identity}] {text}";
        return _client.Send(chatId, outgoing);
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} must not be empty.", name);
    }

    private static void ValidateCount(int count)
    {
        if (count is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(count), count, "count must be from 1 to 500.");
    }
}
