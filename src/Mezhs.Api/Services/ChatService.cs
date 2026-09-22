using Mezhs.Api.Contracts;
using Mezhs.Integrations;
using Mezhs.Models;

namespace Mezhs.Services;

public sealed class ChatService(
    ChatStore store,
    IntegrationRegistry integrations)
{
    public IReadOnlyList<ApiChat> GetChats(string? connectionId = null) =>
        store.GetChats(connectionId)
            .Select(chat => ToApi(chat, store.GetMessages(chat.ChatId)))
            .ToArray();

    public ApiChat Create(CreateChatRequest request)
    {
        integrations.Get(request.ConnectionId);
        var chat = store.CreateChat(request.CategoryId);
        return ToApi(chat, [], request.ConnectionId);
    }

    public bool Exists(string chatId) => store.GetChat(chatId) is not null;

    public ApiChat? TryGet(string chatId)
    {
        var chat = store.GetChat(chatId);
        return chat is null ? null : ToApi(chat, store.GetMessages(chat.ChatId));
    }

    public ApiChat Get(string chatId) =>
        TryGet(chatId)
        ?? throw new ResourceNotFoundException($"Chat '{chatId}' was not found.");

    public IReadOnlyList<ApiChatHistoryMessage> GetMessages(string chatId)
    {
        if (store.GetChat(chatId) is null)
            throw new ResourceNotFoundException($"Chat '{chatId}' was not found.");
        return store.GetMessages(chatId).Select(ToApiHistoryMessage).ToArray();
    }

    private static ApiChat ToApi(
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

    private static ApiChatHistoryMessage ToApiHistoryMessage(StoredMessage message)
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
            message.FileIds,
            message.ParentMessageId,
            message.ReplayOfMessageId,
            message.ReplyMessageId,
            message.Status,
            message.Error,
            message.CreatedAt,
            message.StartedAt,
            message.CompletedAt,
            message.Model,
            message.Activity,
            message.ActivityDetail,
            message.Analysis,
            message.ActivityAt,
            message.ActivityHistory);
    }
}
