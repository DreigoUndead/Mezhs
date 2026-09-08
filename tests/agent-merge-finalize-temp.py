from pathlib import Path


def replace_once(path, old, new):
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    if old not in text:
        raise RuntimeError(f"Expected merge anchor not found in {path}: {old[:80]!r}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


replace_once(
    "src/Mezhs.Api/Configuration/MezhsOptions.cs",
    "    public List<ConnectionOptions> Connections { get; set; } = [];\n}",
    "    public List<ConnectionOptions> Connections { get; set; } = [];\n"
    "    public Dictionary<string, object?> Extensions { get; set; } =\n"
    "        new(StringComparer.OrdinalIgnoreCase);\n}"
)

Path("src/Mezhs.Api/Models/ApiModels.cs").write_text('''using Mezhs.Api.Contracts;

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
    public string Origin { get; init; } = "human";
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

public sealed record CreateCategoryRequest(string Name);
public sealed record DeleteChatsRequest(IReadOnlyList<string>? ChatIds);
public sealed record UpdateCategoryRequest(string Name);
public sealed record UpdateChatRequest(string? CategoryId);
''', encoding="utf-8")

Path("src/Mezhs.Api.Contracts/ApiContracts.cs").write_text('''using System.Text.Json.Serialization;

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
    string? Model,
    IReadOnlyList<ApiFile> Files,
    MessageStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    string? ReplayOfMessageId,
    ApiMessage? Reply);

public sealed record ApiChatHistoryMessage(
    string MessageId,
    string ChatId,
    string ConnectionId,
    string Role,
    string Origin,
    string Content,
    string? Model,
    IReadOnlyList<string> FileIds,
    string? ParentMessageId,
    string? ReplayOfMessageId,
    string? ReplyMessageId,
    MessageStatus Status,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);
''', encoding="utf-8")

program = "src/Mezhs.Api/Program.cs"
replace_once(program, "using Mezhs;\nusing Mezhs.Configuration;", "using Mezhs;\nusing Mezhs.Api.Contracts;\nusing Mezhs.Configuration;")
replace_once(program, '''app.MapGet("/v1/chats", (string? connectionId, ChatStore chats) =>
    Results.Ok(chats.GetChats(connectionId).Select(chat =>
    {
        var messages = chats.GetMessages(chat.ChatId);
        return new
        {
            chat.ChatId,
            ConnectionId = messages.LastOrDefault()?.ConnectionId ?? string.Empty,
            chat.CategoryId,
            chat.CreatedAt,
            chat.UpdatedAt,
            title = messages.FirstOrDefault(message => message.Role == "user")?.Content ?? "New chat"
        };
    })));
''', '''app.MapGet("/v1/chats", (string? connectionId, ChatStore chats) =>
    Results.Ok(chats.GetChats(connectionId)
        .Select(chat => ToApiChat(chat, chats.GetMessages(chat.ChatId)))));
''')
replace_once(program, '''    integrations.Get(request.ConnectionId);
    var chat = chats.CreateChat(request.CategoryId);
    return Results.Created($"/v1/chats/{chat.ChatId}", new
    {
        chat.ChatId,
        request.ConnectionId,
        chat.CategoryId,
        chat.CreatedAt,
        chat.UpdatedAt,
        title = "New chat"
    });
''', '''    integrations.Get(request.ConnectionId);
    var chat = chats.CreateChat(request.CategoryId);
    var response = ToApiChat(chat, [], request.ConnectionId);
    return Results.Created($"/v1/chats/{chat.ChatId}", response);
''')
replace_once(program, '''    var chat = chats.GetChat(chatId);
    if (chat is null)
        return Results.NotFound(new { error = $"Chat '{chatId}' was not found." });
    var messages = chats.GetMessages(chat.ChatId);
    return Results.Ok(new
    {
        chat.ChatId,
        ConnectionId = messages.LastOrDefault()?.ConnectionId ?? string.Empty,
        chat.CategoryId,
        chat.CreatedAt,
        chat.UpdatedAt
    });
''', '''    var chat = chats.GetChat(chatId);
    if (chat is null)
        return Results.NotFound(new { error = $"Chat '{chatId}' was not found." });
    return Results.Ok(ToApiChat(chat, chats.GetMessages(chat.ChatId)));
''')
replace_once(program, '''    if (chats.GetChat(chatId) is null)
        return Results.NotFound(new { error = $"Chat '{chatId}' was not found." });
    return Results.Ok(chats.GetMessages(chatId));
});

Console.WriteLine($"MEŽS config: {configPath}");
''', '''    if (chats.GetChat(chatId) is null)
        return Results.NotFound(new { error = $"Chat '{chatId}' was not found." });
    return Results.Ok(chats.GetMessages(chatId).Select(ToApiHistoryMessage));
});

Console.WriteLine($"MEŽS config: {configPath}");
''')
replace_once(program, '''await app.RunAsync();

static string? GetOption(string[] args, string name)
''', '''await app.RunAsync();

static ApiChat ToApiChat(
    ChatRecord chat,
    IReadOnlyList<StoredMessage> messages,
    string? connectionId = null)
{
    var resolvedConnectionId = connectionId
        ?? messages.LastOrDefault()?.ConnectionId
        ?? string.Empty;
    var title = messages.FirstOrDefault(message => message.Role == "user")?.Content
        ?? "New chat";
    return new ApiChat(
        chat.ChatId,
        resolvedConnectionId,
        chat.CategoryId,
        chat.CreatedAt,
        chat.UpdatedAt,
        title);
}

static ApiChatHistoryMessage ToApiHistoryMessage(StoredMessage message)
{
    var origin = message.Origin;
    if (string.IsNullOrWhiteSpace(origin) ||
        (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
         string.Equals(origin, "human", StringComparison.OrdinalIgnoreCase)))
    {
        origin = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
            ? "assistant"
            : "human";
    }

    return new ApiChatHistoryMessage(
        message.MessageId,
        message.ChatId,
        message.ConnectionId,
        message.Role,
        origin,
        message.Content,
        message.Model,
        message.FileIds,
        message.ParentMessageId,
        message.ReplayOfMessageId,
        message.ReplyMessageId,
        message.Status,
        message.Error,
        message.CreatedAt,
        message.StartedAt,
        message.CompletedAt);
}

static string? GetOption(string[] args, string name)
''')

service = "src/Mezhs.Api/Services/MessageService.cs"
replace_once(service, "using Mezhs;\nusing Mezhs.Integrations;", "using Mezhs;\nusing Mezhs.Api.Contracts;\nusing Mezhs.Integrations;")
replace_once(service, '''            request.Content ?? string.Empty,
            attachedFiles.Select(file => file.FileId).ToArray(),
            model,
            replayOf: null));
''', '''            request.Content ?? string.Empty,
            attachedFiles.Select(file => file.FileId).ToArray(),
            NormalizeOrigin(request.Origin),
            model,
            replayOf: null));
''')
replace_once(service, '''            original.Content,
            original.FileIds,
            original.Model,
            original.MessageId));
''', '''            original.Content,
            original.FileIds,
            NormalizeStoredOrigin(original),
            original.Model,
            original.MessageId));
''')
replace_once(service, '''        string content,
        IReadOnlyList<string> fileIds,
        string? model,
        string? replayOf)
''', '''        string content,
        IReadOnlyList<string> fileIds,
        string origin,
        string? model,
        string? replayOf)
''')
replace_once(service, '''            ConnectionId = connectionId,
            Role = "user",
            Content = content,
            Model = model,
''', '''            ConnectionId = connectionId,
            Role = "user",
            Origin = origin,
            Content = content,
            Model = model,
''')
replace_once(service, '''                ConnectionId = message.ConnectionId,
                Role = "assistant",
                Content = result.Text,
                Model = NormalizeModel(result.Model),
''', '''                ConnectionId = message.ConnectionId,
                Role = "assistant",
                Origin = "assistant",
                Content = result.Text,
                Model = NormalizeModel(result.Model),
''')
replace_once(service, '''            message.ConnectionId,
            message.Role,
            message.Content,
            message.Model,
''', '''            message.ConnectionId,
            message.Role,
            NormalizeStoredOrigin(message),
            message.Content,
            message.Model,
''')
replace_once(service, '''    private static string? NormalizeModel(string? model) =>
        string.IsNullOrWhiteSpace(model) ? null : model.Trim();
''', '''    private static string NormalizeStoredOrigin(StoredMessage message)
    {
        var origin = message.Origin?.Trim();
        if (string.IsNullOrWhiteSpace(origin) ||
            (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
             string.Equals(origin, "human", StringComparison.OrdinalIgnoreCase)))
        {
            return string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? "assistant"
                : "human";
        }
        return origin;
    }

    private static string NormalizeOrigin(string? origin) =>
        string.IsNullOrWhiteSpace(origin) ? "human" : origin.Trim();

    private static string? NormalizeModel(string? model) =>
        string.IsNullOrWhiteSpace(model) ? null : model.Trim();
''')

web_contracts = "src/Mezhs.Web.Lib/src/providers/contracts.ts"
replace_once(web_contracts, '  role: "user" | "assistant";\n  content: string;', '  role: "user" | "assistant";\n  origin: string;\n  content: string;')

app = "src/Mezhs.Web.Lib/src/MezhsChatApp.tsx"
replace_once(app, 'import { ChatProviderRegistry } from "./providers/registry";\n', 'import { ChatProviderRegistry } from "./providers/registry";\nimport { useAutoResizeTextArea } from "./useAutoResizeTextArea";\n')
replace_once(app, '  const fileInputRef = useRef<HTMLInputElement>(null);\n  const providerRegistry', '  const fileInputRef = useRef<HTMLInputElement>(null);\n  const composerRef = useRef<HTMLTextAreaElement>(null);\n  const providerRegistry')
replace_once(app, '''  function handleComposerKey(event: KeyboardEvent<HTMLTextAreaElement>) {
''', '''  useAutoResizeTextArea(composerRef, draft);

  function handleComposerKey(event: KeyboardEvent<HTMLTextAreaElement>) {
''')
replace_once(app, '''            <textarea
              value={draft}
''', '''            <textarea
              ref={composerRef}
              value={draft}
''')
