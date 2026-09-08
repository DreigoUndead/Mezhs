using System.Net;
using System.Text;
using System.Text.Json;

namespace Mezhs.WhatsApp;

internal sealed class WhatsAppApiClient
{
    public const string PortVariable = "MEZHS_WHATSAPP_PORT";
    public const int DefaultPort = 3217;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly Uri? _baseAddress;

    public WhatsAppApiClient()
        : this(new HttpClient(), null)
    {
    }

    internal WhatsAppApiClient(HttpMessageHandler handler, Uri baseAddress)
        : this(new HttpClient(handler), baseAddress)
    {
    }

    private WhatsAppApiClient(HttpClient httpClient, Uri? baseAddress)
    {
        _httpClient = httpClient;
        _baseAddress = baseAddress;
    }

    public WhatsAppAccountStatus Status() => GetJson<WhatsAppAccountStatus>("account/status");

    public WhatsAppAccountStatus Connect() => PostJson<WhatsAppAccountStatus>("account/connect");

    public string Qr() => GetText("account/qr");

    public WhatsAppAccountStatus Disconnect() => PostJson<WhatsAppAccountStatus>("account/disconnect");

    public WhatsAppAccountStatus DeleteSession() => DeleteJson<WhatsAppAccountStatus>("account/session");

    public IReadOnlyList<WhatsAppChat> Chats() => GetJson<WhatsAppChat[]>("chats");

    public WhatsAppMessage Get(string messageId, string? chatId = null)
    {
        var path = $"messages/{Encode(messageId)}";
        if (!string.IsNullOrWhiteSpace(chatId))
            path += $"?chatId={Encode(chatId)}";
        return GetJson<WhatsAppMessage>(path);
    }

    public IReadOnlyList<WhatsAppMessage> GetLast(string chatId, int count) =>
        GetJson<WhatsAppMessage[]>($"messages?chatId={Encode(chatId)}&limit={count}");

    public IReadOnlyList<WhatsAppMessage> GetBefore(string chatId, string messageId, int count) =>
        GetJson<WhatsAppMessage[]>($"messages?chatId={Encode(chatId)}&beforeId={Encode(messageId)}&limit={count}");

    public IReadOnlyList<WhatsAppMessage> GetAfter(string chatId, string messageId, int count) =>
        GetJson<WhatsAppMessage[]>($"messages?chatId={Encode(chatId)}&afterId={Encode(messageId)}&limit={count}");

    public IReadOnlyList<WhatsAppMessage> Search(string chatId, string query, int count) =>
        GetJson<WhatsAppMessage[]>($"messages?chatId={Encode(chatId)}&q={Encode(query)}&limit={count}");

    public WhatsAppMessage Send(string chatId, string text) =>
        SendJson<WhatsAppMessage>(HttpMethod.Post, "messages", new { chatId, text });

    private T GetJson<T>(string path) => SendJson<T>(HttpMethod.Get, path);

    private T PostJson<T>(string path) => SendJson<T>(HttpMethod.Post, path);

    private T DeleteJson<T>(string path) => SendJson<T>(HttpMethod.Delete, path);

    private T SendJson<T>(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, Resolve(path));
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

        using var response = _httpClient.SendAsync(request).GetAwaiter().GetResult();
        var content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        EnsureSuccess(response.StatusCode, content);

        try
        {
            return JsonSerializer.Deserialize<T>(content, JsonOptions)
                ?? throw new FormatException("WhatsApp API returned an empty JSON value.");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"WhatsApp API returned invalid JSON: {ex.Message}", ex);
        }
    }

    private string GetText(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Resolve(path));
        using var response = _httpClient.SendAsync(request).GetAwaiter().GetResult();
        var content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        EnsureSuccess(response.StatusCode, content);
        return content;
    }

    private Uri Resolve(string path) => new(_baseAddress ?? ConfiguredBaseAddress(), path);

    private static Uri ConfiguredBaseAddress()
    {
        var rawPort = Environment.GetEnvironmentVariable(PortVariable);
        var port = DefaultPort;
        if (!string.IsNullOrWhiteSpace(rawPort) &&
            (!int.TryParse(rawPort, out port) || port < 1 || port > 65535))
        {
            throw new InvalidOperationException($"{PortVariable} must be an integer from 1 to 65535.");
        }

        return new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
    }

    private static void EnsureSuccess(HttpStatusCode statusCode, string content)
    {
        var numericStatus = (int)statusCode;
        if (numericStatus is >= 200 and <= 299)
            return;

        var error = TryReadError(content);
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(error)
                ? $"WhatsApp API returned HTTP {numericStatus}."
                : $"WhatsApp API returned HTTP {numericStatus}: {error}");
    }

    private static string? TryReadError(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Encode(string value) => Uri.EscapeDataString(value);
}
