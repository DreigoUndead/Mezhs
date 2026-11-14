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
