using System.Text.Json.Serialization;

namespace Mezhs.Api.Contracts;

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

public sealed record CreateChatRequest(
    string ConnectionId,
    string? CategoryId = null);

public sealed record ApiChat(
    string ChatId,
    string ConnectionId,
    string? CategoryId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Title);

public sealed class PostMessageRequest
{
    private string? _model;

    public PostMessageRequest() { }

    public PostMessageRequest(
        string Content,
        string? ConnectionId = null,
        string? ChatId = null,
        string? CategoryId = null,
        IReadOnlyList<string>? FileIds = null,
        string? Origin = null)
    {
        this.Content = Content;
        this.ConnectionId = ConnectionId;
        this.ChatId = ChatId;
        this.CategoryId = CategoryId;
        this.FileIds = FileIds;
        this.Origin = Origin;
    }

    public string Content { get; init; } = "";
    public string? ConnectionId { get; init; }
    public string? ChatId { get; init; }
    public string? CategoryId { get; init; }
    public IReadOnlyList<string>? FileIds { get; init; }
    public string? Origin { get; init; }

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
    string Origin,
    string Content,
    IReadOnlyList<ApiFile> Files,
    MessageStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    string? ReplayOfMessageId,
    ApiMessage? Reply,
    string? Model = null);

public sealed record ApiChatHistoryMessage(
    string MessageId,
    string ChatId,
    string ConnectionId,
    string Role,
    string Origin,
    string Content,
    IReadOnlyList<string> FileIds,
    string? ParentMessageId,
    string? ReplayOfMessageId,
    string? ReplyMessageId,
    MessageStatus Status,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Model = null);
