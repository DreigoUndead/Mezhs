using System.Net;
using System.Text;
using System.Text.Json;
using Mezhs.Console;

namespace Mezhs.WhatsApp;

internal sealed partial class WhatsAppApplication
{
    [Command(Description = "Run the WhatsApp CLI regression suite without contacting WhatsApp.")]
    public string Test()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("Status route", TestStatus),
            ("Account mutation routes", TestAccountMutations),
            ("QR route", TestQr),
            ("Chats route", TestChats),
            ("Get route", TestGet),
            ("Message history routes", TestHistory),
            ("Search route", TestSearch),
            ("Send route", TestSend),
            ("Identity prefix", TestIdentityPrefix),
            ("API error", TestApiError),
            ("Invalid JSON", TestInvalidJson),
            ("Count validation", TestCountValidation)
        };

        var failures = new List<string>();
        foreach (var test in tests)
        {
            try
            {
                test.Body();
                global::System.Console.WriteLine($"PASS: {test.Name}");
            }
            catch (Exception ex)
            {
                failures.Add($"FAIL: {test.Name}: {ex.Message}");
                global::System.Console.WriteLine(failures[^1]);
            }
        }

        if (failures.Count > 0)
            throw new InvalidOperationException($"{failures.Count}/{tests.Length} tests failed.");

        return $"PASS: {tests.Length}/{tests.Length} tests";
    }

    private static void TestStatus()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{\"state\":\"connected\",\"connected\":true,\"qrAvailable\":false,\"accountId\":\"me\"}"));
        var result = App(handler).Status();
        ExpectRequest(handler, HttpMethod.Get, "/account/status");
        if (!result.Connected || result.State != "connected" || result.AccountId != "me")
            throw new InvalidOperationException("Status response was not decoded correctly.");
    }

    private static void TestAccountMutations()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{\"state\":\"disconnected\",\"connected\":false,\"qrAvailable\":false,\"accountId\":null}"));
        var application = App(handler);

        application.Connect();
        ExpectRequest(handler, HttpMethod.Post, "/account/connect");
        application.Disconnect();
        ExpectRequest(handler, HttpMethod.Post, "/account/disconnect");
        application.DeleteSession();
        ExpectRequest(handler, HttpMethod.Delete, "/account/session");
    }

    private static void TestQr()
    {
        var handler = new RecordingHandler(_ => Text(HttpStatusCode.OK, "<svg>qr</svg>", "image/svg+xml"));
        var result = App(handler).Qr();
        ExpectRequest(handler, HttpMethod.Get, "/account/qr");
        if (result != "<svg>qr</svg>")
            throw new InvalidOperationException("QR SVG was modified.");
    }

    private static void TestChats()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "[{\"id\":\"group@g.us\",\"name\":\"Home\",\"unreadCount\":2,\"conversationTimestamp\":\"2026-09-08T17:00:00.000Z\"}]"));
        var chats = App(handler).Chats();
        ExpectRequest(handler, HttpMethod.Get, "/chats");
        if (chats.Count != 1 || chats[0].Id != "group@g.us" || chats[0].Name != "Home" || chats[0].UnreadCount != 2)
            throw new InvalidOperationException("Chat response was not decoded correctly.");
    }

    private static void TestGet()
    {
        var handler = new RecordingHandler(_ => MessageResponse("MSG/1", "group@g.us", "hello"));
        var message = App(handler).Get("MSG/1", "group@g.us");
        ExpectRequest(handler, HttpMethod.Get, "/messages/MSG%2F1");
        ExpectQuery(handler, "chatId", "group@g.us");
        if (message.Id != "MSG/1" || message.Text != "hello")
            throw new InvalidOperationException("Message response was not decoded correctly.");
    }

    private static void TestHistory()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "[]"));
        var application = App(handler);

        application.GetLast("group@g.us", 7);
        ExpectRequest(handler, HttpMethod.Get, "/messages");
        ExpectQuery(handler, "chatId", "group@g.us");
        ExpectQuery(handler, "limit", "7");

        application.GetBefore("group@g.us", "OLD/1", 8);
        ExpectQuery(handler, "beforeId", "OLD/1");
        ExpectQuery(handler, "limit", "8");

        application.GetAfter("group@g.us", "NEW/1", 9);
        ExpectQuery(handler, "afterId", "NEW/1");
        ExpectQuery(handler, "limit", "9");
    }

    private static void TestSearch()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "[]"));
        App(handler).Search("group@g.us", "foo bar:baz", 11);
        ExpectRequest(handler, HttpMethod.Get, "/messages");
        ExpectQuery(handler, "chatId", "group@g.us");
        ExpectQuery(handler, "q", "foo bar:baz");
        ExpectQuery(handler, "limit", "11");
    }

    private static void TestSend()
    {
        var handler = new RecordingHandler(_ => MessageResponse("OUT1", "group@g.us", "hello"));
        App(handler).Send("group@g.us", "hello");
        ExpectRequest(handler, HttpMethod.Post, "/messages");
        using var body = JsonDocument.Parse(handler.LastBody ?? throw new InvalidOperationException("Send request had no body."));
        if (body.RootElement.GetProperty("chatId").GetString() != "group@g.us" ||
            body.RootElement.GetProperty("text").GetString() != "hello")
        {
            throw new InvalidOperationException("Send body was incorrect.");
        }
    }

    private static void TestIdentityPrefix()
    {
        var handler = new RecordingHandler(_ => MessageResponse("OUT2", "group@g.us", "[HOME] hello"));
        App(handler, " HOME ").Send("group@g.us", "hello");
        using var body = JsonDocument.Parse(handler.LastBody ?? throw new InvalidOperationException("Send request had no body."));
        if (body.RootElement.GetProperty("text").GetString() != "[HOME] hello")
            throw new InvalidOperationException("Identity was not prefixed by the CLI.");
    }

    private static void TestApiError()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.Conflict, "{\"error\":\"WhatsApp account is not connected.\"}"));
        try
        {
            App(handler).Send("group@g.us", "hello");
            throw new InvalidOperationException("Expected API error was not thrown.");
        }
        catch (InvalidOperationException ex) when (ex.Message == "WhatsApp API returned HTTP 409: WhatsApp account is not connected.")
        {
        }
    }

    private static void TestInvalidJson()
    {
        var handler = new RecordingHandler(_ => Text(HttpStatusCode.OK, "not-json", "application/json"));
        try
        {
            App(handler).Status();
            throw new InvalidOperationException("Expected invalid JSON failure was not thrown.");
        }
        catch (FormatException ex) when (ex.Message.StartsWith("WhatsApp API returned invalid JSON:", StringComparison.Ordinal))
        {
        }
    }

    private static void TestCountValidation()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "[]"));
        try
        {
            App(handler).GetLast("group@g.us", 0);
            throw new InvalidOperationException("Count 0 was accepted.");
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        if (handler.LastRequest is not null)
            throw new InvalidOperationException("Invalid count reached the HTTP API.");
    }

    private static WhatsAppApplication App(RecordingHandler handler, string? identity = null) =>
        new(new WhatsAppApiClient(handler, new Uri("http://127.0.0.1:3217/")), () => identity);

    private static HttpResponseMessage MessageResponse(string id, string chatId, string text) =>
        Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            id,
            chatId,
            participantId = (string?)null,
            fromMe = true,
            timestamp = "2026-09-08T17:00:00.000Z",
            type = "conversation",
            text
        }));

    private static HttpResponseMessage Json(HttpStatusCode status, string content) =>
        Text(status, content, "application/json");

    private static HttpResponseMessage Text(HttpStatusCode status, string content, string mediaType) =>
        new(status)
        {
            Content = new StringContent(content, Encoding.UTF8, mediaType)
        };

    private static void ExpectRequest(RecordingHandler handler, HttpMethod method, string absolutePath)
    {
        var request = handler.LastRequest ?? throw new InvalidOperationException("No HTTP request was recorded.");
        if (request.Method != method)
            throw new InvalidOperationException($"Expected {method} but got {request.Method}.");
        if (request.Uri.AbsolutePath != absolutePath)
            throw new InvalidOperationException($"Expected path '{absolutePath}' but got '{request.Uri.AbsolutePath}'.");
    }

    private static void ExpectQuery(RecordingHandler handler, string name, string value)
    {
        var request = handler.LastRequest ?? throw new InvalidOperationException("No HTTP request was recorded.");
        var query = ParseQuery(request.Uri.Query);
        if (!query.TryGetValue(name, out var actual) || actual != value)
            throw new InvalidOperationException($"Expected query {name}='{value}' but got '{actual}'.");
    }

    private static IReadOnlyDictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                part => Uri.UnescapeDataString(part[0]),
                part => part.Length > 1 ? Uri.UnescapeDataString(part[1]) : string.Empty,
                StringComparer.Ordinal);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public RecordedRequest? LastRequest { get; private set; }
        public string? LastBody => LastRequest?.Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            LastRequest = new RecordedRequest(request.Method, request.RequestUri ?? throw new InvalidOperationException("Request URI is missing."), body);
            return responseFactory(request);
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body);
}
