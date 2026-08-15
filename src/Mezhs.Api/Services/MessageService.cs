using System.Collections.Concurrent;
using Mezhs.Integrations;
using Mezhs.Models;

namespace Mezhs.Services;

public sealed class MessageService(
    ChatStore store,
    FileStore files,
    IntegrationRegistry integrations)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _chatGates =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<ApiMessage> PostAsync(
        PostMessageRequest request,
        CancellationToken cancellationToken)
    {
        var requestedFileIds = request.FileIds ?? [];
        if (string.IsNullOrWhiteSpace(request.Content) && requestedFileIds.Count == 0)
            throw new ArgumentException("content or at least one file is required.");

        ChatRecord chat;
        if (string.IsNullOrWhiteSpace(request.ChatId))
        {
            if (string.IsNullOrWhiteSpace(request.ConnectionId))
                throw new ArgumentException("connectionId is required when chatId is not provided.");
            integrations.Get(request.ConnectionId);
            chat = await store.CreateChatAsync(
                request.ConnectionId,
                request.CategoryId,
                cancellationToken);
        }
        else
        {
            chat = store.GetChat(request.ChatId)
                ?? throw new KeyNotFoundException($"Chat '{request.ChatId}' was not found.");
            if (!string.IsNullOrWhiteSpace(request.ConnectionId) &&
                !string.Equals(request.ConnectionId, chat.ConnectionId, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("connectionId does not match the existing chat.");
        }

        var integration = integrations.Get(chat.ConnectionId);
        if (requestedFileIds.Count > 0 && !integration.Capabilities.FileInput)
            throw new ArgumentException($"Connection '{chat.ConnectionId}' does not support file input.");
        var attachedFiles = files.GetForConnection(requestedFileIds, chat.ConnectionId);
        if (attachedFiles.Any(file => file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) &&
            !integration.Capabilities.ImageInput)
            throw new ArgumentException($"Connection '{chat.ConnectionId}' does not support image input.");

        var message = await CreateMessageAsync(
            chat,
            request.Content ?? string.Empty,
            attachedFiles.Select(file => file.FileId).ToArray(),
            replayOf: null,
            cancellationToken);
        return ToApi(message);
    }

    public async Task<ApiMessage> ReplayAsync(string messageId, CancellationToken cancellationToken)
    {
        var original = store.GetMessage(messageId)
            ?? throw new KeyNotFoundException($"Message '{messageId}' was not found.");
        if (original.Role != "user")
            throw new KeyNotFoundException("Only user request messages can be replayed.");
        var chat = store.GetChat(original.ChatId)
            ?? throw new KeyNotFoundException($"Chat '{original.ChatId}' was not found.");
        var replay = await CreateMessageAsync(
            chat,
            original.Content,
            original.FileIds,
            original.MessageId,
            cancellationToken);
        return ToApi(replay);
    }

    public ApiMessage? Get(string messageId)
    {
        var message = store.GetMessage(messageId);
        return message is null ? null : ToApi(message);
    }

    private async Task<StoredMessage> CreateMessageAsync(
        ChatRecord chat,
        string content,
        IReadOnlyList<string> fileIds,
        string? replayOf,
        CancellationToken cancellationToken)
    {
        var message = new StoredMessage
        {
            MessageId = ChatStore.NewId("msg"),
            ChatId = chat.ChatId,
            ConnectionId = chat.ConnectionId,
            Role = "user",
            Content = content,
            FileIds = fileIds,
            ReplayOfMessageId = replayOf,
            Status = MessageStatus.Queued
        };
        await store.SaveMessageAsync(message, cancellationToken);
        _ = Task.Run(() => ProcessAsync(message));
        return message;
    }

    private async Task ProcessAsync(StoredMessage message)
    {
        var gate = _chatGates.GetOrAdd(message.ChatId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            message.Status = MessageStatus.Running;
            message.StartedAt = DateTimeOffset.UtcNow;
            await store.SaveMessageAsync(message);

            var chat = store.GetChat(message.ChatId)
                ?? throw new KeyNotFoundException($"Chat '{message.ChatId}' was not found.");
            var history = store.GetMessages(message.ChatId)
                .Where(item => item.MessageId != message.MessageId && item.CreatedAt <= message.CreatedAt)
                .Select(ToIntegrationMessage)
                .ToArray();
            var inputFiles = files.GetForConnection(message.FileIds, chat.ConnectionId)
                .Select(file => new IntegrationInputFile(
                    file.FileId,
                    files.GetContentPath(file),
                    file.Name,
                    file.ContentType,
                    file.Size))
                .ToArray();
            var result = await integrations.Get(chat.ConnectionId).SendMessageAsync(
                new IntegrationSendContext(
                    new IntegrationChatContext(
                        chat.ChatId,
                        chat.ConnectionId,
                        chat.RemoteChatUrl,
                        chat.RemoteConversationId,
                        chat.RemoteParentMessageId),
                    ToIntegrationMessage(message),
                    history,
                    inputFiles),
                CancellationToken.None);

            var replyFileIds = new List<string>();
            foreach (var output in result.Files ?? [])
            {
                try
                {
                    var imported = await files.ImportAsync(
                        chat.ConnectionId,
                        output.Path,
                        output.Name,
                        output.ContentType,
                        FileSource.Assistant);
                    replyFileIds.Add(imported.FileId);
                }
                finally
                {
                    if (output.DeleteAfterImport)
                    {
                        try { File.Delete(output.Path); }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Could not remove integration output '{output.Path}': {ex.Message}");
                        }
                    }
                }
            }

            var reply = new StoredMessage
            {
                MessageId = ChatStore.NewId("msg"),
                ChatId = chat.ChatId,
                ConnectionId = chat.ConnectionId,
                Role = "assistant",
                Content = result.Text,
                FileIds = replyFileIds,
                ParentMessageId = message.MessageId,
                Status = MessageStatus.Completed,
                StartedAt = message.StartedAt,
                CompletedAt = DateTimeOffset.UtcNow
            };
            await store.SaveMessageAsync(reply);

            if (!string.IsNullOrWhiteSpace(result.RemoteChatUrl))
                chat.RemoteChatUrl = result.RemoteChatUrl;
            if (!string.IsNullOrWhiteSpace(result.RemoteConversationId))
                chat.RemoteConversationId = result.RemoteConversationId;
            if (!string.IsNullOrWhiteSpace(result.RemoteParentMessageId))
                chat.RemoteParentMessageId = result.RemoteParentMessageId;
            if (!string.IsNullOrWhiteSpace(result.RemoteChatUrl) ||
                !string.IsNullOrWhiteSpace(result.RemoteConversationId) ||
                !string.IsNullOrWhiteSpace(result.RemoteParentMessageId))
                await store.SaveChatAsync(chat);

            message.ReplyMessageId = reply.MessageId;
            message.Status = MessageStatus.Completed;
            message.CompletedAt = reply.CompletedAt;
            await store.SaveMessageAsync(message);
        }
        catch (Exception ex)
        {
            message.Status = MessageStatus.Failed;
            message.Error = ex.Message;
            message.CompletedAt = DateTimeOffset.UtcNow;
            await store.SaveMessageAsync(message);
        }
        finally
        {
            gate.Release();
        }
    }

    private static IntegrationMessageContext ToIntegrationMessage(StoredMessage message) => new(
        message.MessageId,
        message.Role,
        message.Content,
        message.Status == MessageStatus.Completed,
        message.CreatedAt);

    private ApiMessage ToApi(StoredMessage message)
    {
        ApiMessage? reply = null;
        if (!string.IsNullOrWhiteSpace(message.ReplyMessageId) &&
            store.GetMessage(message.ReplyMessageId) is { } storedReply)
            reply = ToApi(storedReply);

        return new ApiMessage(
            message.MessageId,
            message.ChatId,
            message.ConnectionId,
            message.Role,
            message.Content,
            files.GetForConnection(message.FileIds, message.ConnectionId)
                .Select(FileStore.ToApi)
                .ToArray(),
            message.Status,
            message.CreatedAt,
            message.StartedAt,
            message.CompletedAt,
            message.Error,
            message.ReplayOfMessageId,
            reply);
    }
}
