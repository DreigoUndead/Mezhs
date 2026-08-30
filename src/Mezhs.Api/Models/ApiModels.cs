using Mezhs.Api.Contracts;

namespace Mezhs.Models;

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

public sealed record CreateCategoryRequest(string Name);
public sealed record DeleteChatsRequest(IReadOnlyList<string>? ChatIds);
public sealed record UpdateCategoryRequest(string Name);
public sealed record UpdateChatRequest(string? CategoryId);
