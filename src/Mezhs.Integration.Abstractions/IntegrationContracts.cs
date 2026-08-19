namespace Mezhs.Integrations;

public sealed record IntegrationConnection(
    string Id,
    string Name,
    string Type,
    IReadOnlyDictionary<string, string?> Settings)
{
    public string? GetSetting(string name) =>
        Settings.TryGetValue(name, out var value) ? value : null;
}

public sealed record IntegrationCapabilities(
    bool FileInput = false,
    bool ImageInput = false,
    bool FileOutput = false,
    bool ImageOutput = false);

public interface IIntegrationHost
{
    string GetConnectionRoot(string connectionId);
}

public interface ILoginModule
{
    Task LoginAsync(CancellationToken cancellationToken = default);
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class IntegrationAttribute(string type) : Attribute
{
    public string Type { get; } = type;
}

public interface IChatIntegration : IAsyncDisposable
{
    IntegrationConnection Connection { get; }
    IntegrationCapabilities Capabilities { get; }
    ILoginModule? Login { get; }

    Task<IntegrationSendResult> SendMessageAsync(
        IntegrationSendContext context,
        CancellationToken cancellationToken = default);
}

public abstract class ChatIntegrationBase(IntegrationConnection connection) : IChatIntegration
{
    public IntegrationConnection Connection { get; } = connection;
    public virtual IntegrationCapabilities Capabilities => new();
    public virtual ILoginModule? Login => null;

    public abstract Task<IntegrationSendResult> SendMessageAsync(
        IntegrationSendContext context,
        CancellationToken cancellationToken = default);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed record IntegrationChatContext(
    string ChatId,
    string ConnectionId,
    string? RemoteChatUrl = null,
    string? RemoteConversationId = null,
    string? RemoteParentMessageId = null);

public sealed record IntegrationMessageContext(
    string MessageId,
    string Role,
    string Content,
    bool Completed,
    DateTimeOffset CreatedAt);

public sealed record IntegrationSendContext(
    IntegrationChatContext Chat,
    IntegrationMessageContext Message,
    IReadOnlyList<IntegrationMessageContext> History,
    IReadOnlyList<IntegrationInputFile> Files,
    bool RestoreConversation = false)
{
    public string Prompt
    {
        get
        {
            if (!RestoreConversation)
                return Message.Content;

            var history = History
                .Where(message => message.Role == "assistant" || message.Completed)
                .Select(message =>
                    $"[{(message.Role == "assistant" ? "Assistant" : "User")}]\n{message.Content}")
                .ToList();
            if (history.Count == 0)
                return Message.Content;

            history.Add($"[User]\n{Message.Content}");
            return "Continue the conversation below. Reply only to the latest user message.\n\n" +
                   string.Join("\n\n", history);
        }
    }
}

public sealed record IntegrationInputFile(
    string FileId,
    string Path,
    string Name,
    string ContentType,
    long Size);

public sealed record IntegrationOutputFile(
    string Path,
    string Name,
    string ContentType,
    bool DeleteAfterImport = true);

public sealed record IntegrationSendResult(
    string Text,
    string? RemoteChatUrl = null,
    string? RemoteConversationId = null,
    string? RemoteParentMessageId = null,
    IReadOnlyList<IntegrationOutputFile>? Files = null);
