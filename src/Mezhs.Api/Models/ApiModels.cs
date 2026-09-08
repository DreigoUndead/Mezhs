using System.Text.Json.Serialization;

namespace Mezhs.Models;

public enum MessageStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

public enum FileSource
{
    User,
    Assistant
}

public sealed class ChatRecord
{
    public required string ChatId { get; init; }
    public List<ChatConnectionState> RemoteStates { get; init; } = [];
    public string? CategoryId { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ChatConnectionState
{
    public required string ConnectionId { get; init; }
    public string? RemoteChatUrl { get; set; }
    public string? RemoteConversationId { get; set; }
    public string? RemoteParentMessageId { get; set; }
    public string? LastLocalMessageId { get; set; }
}

public sealed class CategoryRecord
{
    public required string CategoryId { get; init; }
    public required string Name { get; set; }
    public required string Color { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class StoredMessage
{
    public required string MessageId { get; init; }
    public required string ChatId { get; init; }
    public required string ConnectionId { get; init; }
    public required string Role { get; init; }
    public required string Content { get; init; }
    public string? Model { get; init; }
    public IReadOnlyList<string> FileIds { get; init; } = [];
    public string? ParentMessageId { get; init; }
    public string? ReplayOfMessageId { get; init; }
    public string? ReplyMessageId { get; set; }
    public MessageStatus Status { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class StoredFile
{
    public required string FileId { get; init; }
    public required string ConnectionId { get; init; }
    public required string Name { get; init; }
    public required string ContentType { get; init; }
    public required long Size { get; init; }
    public required FileSource Source { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class PostMessageRequest
{
    private string? _model;

    public string Content { get; init; } = "";
    public string? ConnectionId { get; init; }
    public string? ChatId { get; init; }
    public string? CategoryId { get; init; }
    public IReadOnlyList<string>? FileIds { get; init; }

    public string? Model
    {
        get => _model;
        init
        {
            _model = value;
            ModelSpecified = true;
        }
    }

    [JsonIgnore]
    public bool ModelSpecified { get; private set; }
}

public sealed record CreateCategoryRequest(string Name);
public sealed record CreateChatRequest(string ConnectionId, string? CategoryId = null);
public sealed record DeleteChatsRequest(IReadOnlyList<string>? ChatIds);
public sealed record UpdateCategoryRequest(string Name);
public sealed record UpdateChatRequest(string? CategoryId);

public sealed record ApiFile(
    string FileId,
    string ConnectionId,
    string Name,
    string ContentType,
    long Size,
    FileSource Source,
    DateTimeOffset CreatedAt,
    string ContentUrl,
    string DownloadUrl);

public sealed record ApiMessage(
    string MessageId,
    string ChatId,
    string ConnectionId,
    string Role,
    string Content,
    string? Model,
    IReadOnlyList<ApiFile> Files,
    MessageStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    string? ReplayOfMessageId,
    ApiMessage? Reply);
