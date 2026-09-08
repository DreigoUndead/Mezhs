from pathlib import Path


def replace_once(path, old, new):
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    if old not in text:
        raise RuntimeError(f"Expected compatibility anchor not found in {path}: {old[:80]!r}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


contracts = "src/Mezhs.Api.Contracts/ApiContracts.cs"
replace_once(contracts, '''public sealed record ApiMessage(
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
''', '''public sealed record ApiMessage(
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
''')
replace_once(contracts, '''public sealed record ApiChatHistoryMessage(
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
''', '''public sealed record ApiChatHistoryMessage(
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
''')

program = "src/Mezhs.Api/Program.cs"
replace_once(program, '''        origin,
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
''', '''        origin,
        message.Content,
        message.FileIds,
        message.ParentMessageId,
        message.ReplayOfMessageId,
        message.ReplyMessageId,
        message.Status,
        message.Error,
        message.CreatedAt,
        message.StartedAt,
        message.CompletedAt,
        message.Model);
''')

service = "src/Mezhs.Api/Services/MessageService.cs"
replace_once(service, '''            message.Role,
            NormalizeStoredOrigin(message),
            message.Content,
            message.Model,
            files.GetMany(message.FileIds)
                .Select(FileStore.ToApi)
                .ToArray(),
            message.Status,
            message.CreatedAt,
            message.StartedAt,
            message.CompletedAt,
            message.Error,
            message.ReplayOfMessageId,
            reply);
''', '''            message.Role,
            NormalizeStoredOrigin(message),
            message.Content,
            files.GetMany(message.FileIds)
                .Select(FileStore.ToApi)
                .ToArray(),
            message.Status,
            message.CreatedAt,
            message.StartedAt,
            message.CompletedAt,
            message.Error,
            message.ReplayOfMessageId,
            reply,
            message.Model);
''')
