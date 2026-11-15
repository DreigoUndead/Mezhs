using Mezhs.Integrations.ChatGpt;
using System.Net.Http;
using Mezhs.Browser;
using Mezhs.Integrations.Browser;
using Mezhs.Integrations.Mock;
using Mezhs.Integrations;

var requested = new List<(int? Limit, int? Offset, bool FromStart)>();
var messages = new[]
{
    new ChatMessage("a", "first-connection", "user", "first", true, DateTimeOffset.UtcNow),
    new ChatMessage("b", "other-connection", "assistant", "second", true, DateTimeOffset.UtcNow),
    new ChatMessage("c", "first-connection", "user", "third", false, DateTimeOffset.UtcNow),
    new ChatMessage("d", "other-connection", "assistant", "fourth", true, DateTimeOffset.UtcNow)
};
var chat = new ChatContext(
    "chat-1", "first-connection", "remote-conversation",
    (limit, offset, fromStart, cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        requested.Add((limit, offset, fromStart));
        var slice = fromStart
            ? messages.Skip(offset ?? 0).Take(limit ?? messages.Length).ToArray()
            : messages.Take(Math.Max(0, messages.Length - (offset ?? 0)))
                .TakeLast(limit ?? messages.Length).ToArray();
        return Task.FromResult<IReadOnlyList<ChatMessage>>(slice);
    }, remoteParentMessageId: "remote-parent", remoteChatUrl: "https://example.invalid/chat");
var all = await chat.GetHistoryAsync();
if (all.Count != 4 || all[1].ConnectionId != "other-connection")
    throw new Exception("History must include messages from other connections.");
var newest = await chat.GetHistoryAsync(limit: 2, offset: 1, fromStart: false);
if (newest.Select(m => m.MessageId).SequenceEqual(["b", "c"]) is false)
    throw new Exception("Newest history slice must still be chronological.");
if (requested.Count != 2 || requested[0] != (null, null, true) ||
    requested[1] != (2, 1, false))
    throw new Exception("ChatContext did not forward history paging parameters.");
try { await chat.GetHistoryAsync(limit: -1); throw new Exception("Negative limit accepted."); }
catch (ArgumentOutOfRangeException) { }
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try { await chat.GetHistoryAsync(cancellationToken: cancelled.Token); throw new Exception("Cancelled history accepted."); }
catch (OperationCanceledException) { }
if (requested.Count != 2)
    throw new Exception("History source was invoked after invalid/cancelled request.");

var integration = new Fixture(new IntegrationConnection(
    "fixture", "Fixture", "fixture", new Dictionary<string, string?>()), new ProbeHost());
if (!integration.GetModelsAsync().Result.Any(model => model.Id == "default") ||
    !integration.CreateMessageId().StartsWith("msg_", StringComparison.Ordinal) ||
    integration.AllowFiles)
    throw new Exception("Integration defaults are invalid.");
var context = new ChatContext("x", "fixture", null,
    (_, _, _, _) => Task.FromResult<IReadOnlyList<ChatMessage>>([]));
var result = await integration.PromptAsync(
    context, new PromptRequest("u", "hello", null, []));
if (result.MessageId == result.Model || result.Text != "hello")
    throw new Exception("Typed prompt result is invalid.");
Console.WriteLine("PASS: typed chat history pagination, multi-provider source, cancellation, model/default, IDs.");

var mock = new MockIntegration(new IntegrationConnection(
    "mock", "Mock", "mock", new Dictionary<string, string?>()), new ProbeHost());
await ExpectFailureAsync<NotSupportedException>(() => mock.PromptAsync(
    context, new PromptRequest("u", "hello", "unsupported-model", [])));
var validMock = await mock.PromptAsync(context,
    new PromptRequest("u", "hello", "mock-fast", []));
if (validMock.Text != "Echo: hello")
    throw new Exception("Valid mock model was rejected.");

var sent = new List<HttpTransportRequest>();
var progressStates = new List<string>();
var typed = new IntegrationHttpClient((req, report, ct) =>
{
    ct.ThrowIfCancellationRequested();
    sent.Add(req);
    report?.Invoke(new HttpTransferProgress("retrieving", "Received 16 bytes."));
    var body = req.Path == "/v1/stream"
        ? "data: {\"part\":1}\r\n\r\ndata: {\"part\":2}\n\ndata: [DONE]\n\n"
        : "{\"id\":\"echo\"}";
    return Task.FromResult(new HttpTransportResponse(200, body, "application/json"));
});
var received = await typed.GetAsync<Dto>("/v1/items");
if (received.Id != "echo" || sent[0].Method != "GET" || sent[0].Body is not null)
    throw new Exception("Typed HTTP GET did not preserve request/response contract.");
received = await typed.PostAsync<Dto, Dto>("/v1/items", new Dto("create"));
if (received.Id != "echo" || sent[1].Method != "POST" ||
    !sent[1].Body!.Contains("\"id\":\"create\"", StringComparison.Ordinal))
    throw new Exception("Typed HTTP POST serialized incorrectly.");
received = await typed.PatchAsync<Dto, Dto>("/v1/items/1", new Dto("edit"));
if (received.Id != "echo" || sent[2].Method != "PATCH" ||
    !sent[2].Body!.Contains("\"id\":\"edit\"", StringComparison.Ordinal))
    throw new Exception("Typed HTTP PATCH serialized incorrectly.");
var sse = await typed.CallAsync(
    new HttpEndpoint<object, string[]>(HttpMethod.Get, "/v1/stream",
        HttpResultFormat.ServerSentEvents, content => HttpStreamData.Sse(content).ToArray(),
        "text/event-stream"), null,
    progress => progressStates.Add(progress.State));
if (sse.Length != 2 || sse[0] != "{\"part\":1}" || sse[1] != "{\"part\":2}" ||
    progressStates.Count != 1)
    throw new Exception("SSE parser/progress did not preserve event contents.");
var multi = HttpStreamData.Sse("data: first\ndata: second\n\n");
if (multi.Count != 1 || multi[0] != "first\nsecond" ||
    HttpStreamData.Ndjson("{\"a\":1}\r\n{\"a\":2}\n").Count != 2)
    throw new Exception("SSE multiline / NDJSON framing is incorrect.");
if (sent.Count != 4)
    throw new Exception("Typed HTTP shared executor performed an unexpected extra request.");

await ExpectFailureAsync<ArgumentException>(() =>
    typed.GetAsync<Dto>("https://external.example/items"));
await ExpectFailureAsync<ArgumentException>(() =>
    typed.CallAsync(new HttpEndpoint<Dto, Dto>(HttpMethod.Get, "/v1/items"),
        new Dto("unexpected")));
if (sent.Count != 4)
    throw new Exception("Invalid HTTP request reached transport.");

var failures = 0;
var failing = new IntegrationHttpClient((req, _, _) =>
{
    failures++;
    throw new IOException("Ambiguous remote POST acceptance.");
});
await ExpectFailureAsync<IOException>(() =>
    failing.PostAsync<Dto, Dto>("/v1/items", new Dto("unsafe")));
if (failures != 1)
    throw new Exception("Ambiguous submitted POST was replayed.");

var cancelledHttp = new CancellationTokenSource();
cancelledHttp.Cancel();
await ExpectFailureAsync<OperationCanceledException>(() =>
    typed.PatchAsync<Dto, Dto>("/v1/items", new Dto("no-send"), cancelledHttp.Token));
if (sent.Count != 4)
    throw new Exception("Cancelled HTTP mutation reached transport.");

var browser = new FakeBrowser();
var browserHttp = new BrowserHttpExecutor(browser);
var fromElectron = await browserHttp.CallAsync(
    new HttpEndpoint<Dto, Dto>(HttpMethod.Patch, "/backend-api/typed"),
    new Dto("test"));
if (fromElectron.Id != "browser" || browser.Calls != 1 ||
    browser.Last?.Method != "PATCH" ||
    !browser.Last.Body!.Contains("\"id\":\"test\"", StringComparison.Ordinal))
    throw new Exception("Browser HTTP composition did not forward typed request.");
await ExpectFailureAsync<InvalidOperationException>(() => browserHttp.CallAsync(
    new HttpEndpoint<Dto, Dto>(HttpMethod.Post, "/backend-api/error"), new Dto("fail")));
if (browser.Calls != 2)
    throw new Exception("Browser HTTP request was retried after error.");

using var native = new HttpClient(new FakeHandler(async request =>
{
    if (request.Method != HttpMethod.Patch || request.RequestUri?.AbsoluteUri != "https://api.example.test/v1/items")
        throw new InvalidOperationException("HttpClient transport method/path mismatch.");
    var body = await request.Content!.ReadAsStringAsync();
    if (!body.Contains("\"id\":\"via-http-client\"", StringComparison.Ordinal))
        throw new Exception("HttpClient request body mismatch.");
    return new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
        Content = new StringContent("{\"id\":\"native\"}")
    };
})) { BaseAddress = new Uri("https://api.example.test/") };
var nativeHttp = IntegrationHttpClient.ForHttpClient(native);
var viaNative = await nativeHttp.PatchAsync<Dto, Dto>(
    "/v1/items", new Dto("via-http-client"));
if (viaNative.Id != "native")
    throw new Exception("Normal HttpClient adapter failed.");
Console.WriteLine("PASS: typed HTTP GET/POST/PATCH, HttpClient/Electron adapters, SSE/NDJSON, no-replay, cancellation and negative model validation.");

var catalog = new ChatGptModelCatalogProbe(
    [new ChatGptModelVersionProbe("v1", "Reasoning", IntelligencePresets:
        [new ChatGptModelPresetProbe("available", true, "gpt-sample", "extended", "Extended", null),
         new ChatGptModelPresetProbe("available", false, "disabled", "fast", "Disabled", null)])],
    null);
var picker = catalog.ToModels();
if (picker.Count != 1 || picker[0].Id != "gpt-sample::thinking-effort=extended" ||
    picker[0].Name != "Reasoning · Extended")
    throw new Exception("Native model preset/effort mapping changed.");
var legacy = new ChatGptModelCatalogProbe(null, [
    new ChatGptModelProbe("", "", Id: "fallback", DisplayName: "Fallback"),
    new ChatGptModelProbe("fallback", "Duplicate")]);
var fallback = legacy.ToModels();
if (fallback.Count != 1 || fallback[0].Id != "fallback" || fallback[0].Name != "Fallback")
    throw new Exception("Legacy model fallback or id deduplication changed.");
Console.WriteLine("PASS: ChatGPT native model catalog, effort identifiers, disabled presets and legacy fallback.");



static async Task ExpectFailureAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try { await action(); }
    catch (TException) { return; }
    throw new Exception("Expected " + typeof(TException).Name + " was not thrown.");
}

sealed class ProbeHost : IIntegrationHost
{
    public string GetConnectionRoot(string connectionId) => "";
}
sealed class Fixture(IntegrationConnection connection, IIntegrationHost host) :
    IntegrationBase(connection, host)
{
    public override Task<PromptResult> PromptAsync(
        ChatContext chat, PromptRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new PromptResult(request.Text, "reply", request.Model ?? "default"));
}


sealed record Dto(string Id);

sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
}

sealed class FakeBrowser : IChatBrowserTransport
{
    public string Name => "Simulated browser";
    public int Calls { get; private set; }
    public HttpTransportRequest? Last { get; private set; }

    public Task InitializeAsync(BrowserTransportOptions options, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Simulated transport must not open an Electron host.");

    public Task<TResult> InvokeAsync<TResult>(
        string operation, object? arguments = null, CancellationToken cancellationToken = default) =>
        InvokeWithProgressAsync<TResult>(operation, arguments, null, cancellationToken);

    public Task<TResult> InvokeWithProgressAsync<TResult>(
        string operation, object? arguments,
        Action<BrowserOperationProgress>? reportProgress,
        CancellationToken cancellationToken = default)
    {
        if (operation != "typedHttp" || arguments is not HttpTransportRequest request)
            throw new InvalidOperationException("Expected only typed HTTP browser requests.");
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        Last = request;
        reportProgress?.Invoke(new BrowserOperationProgress("retrieving", "Simulated transport."));
        if (request.Path == "/backend-api/error")
            throw new InvalidOperationException("Remote mutation outcome unknown.");
        return Task.FromResult((TResult)(object)new HttpTransportResponse(
            200, "{\"id\":\"browser\"}", "application/json"));
    }

    public Task ShowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
