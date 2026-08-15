using System.Collections.Concurrent;
using System.Threading.Channels;
using Mezhs.Integrations;
using Mezhs.Models;
using Microsoft.Extensions.Hosting;

namespace Mezhs.Services;

public sealed class MessageService(
    ChatStore store,
    FileStore files,
    IntegrationRegistry integrations) : BackgroundService
{
    private readonly Channel<StoredMessage> _queue = Channel.CreateUnbounded<StoredMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _chatGates =
        new(StringComparer.OrdinalIgnoreCase);

    public ApiMessage Post(PostMessageRequest request)
    {
        var requestedFileIds = request.FileIds ?? [];
        if (string.IsNullOrWhiteSpace(request.Content) && requestedFileIds.Count == 0)
            throw new ArgumentException("content or at least one file is required.");

        ChatRecord chat;
        string connectionId;
        if (string.IsNullOrWhiteSpace(request.ChatId))
        {
            if (string.IsNullOrWhiteSpace(request.ConnectionId))
                throw new ArgumentException("connectionId is required when chatId is not provided.");
            connectionId = request.ConnectionId.Trim();
            chat = store.CreateChat(request.CategoryId);
        }
        else
        {
            chat = store.GetChat(request.ChatId)
                ?? throw new KeyNotFoundException($"Chat '{request.ChatId}' was not found.");
            connectionId = !string.IsNullOrWhiteSpace(request.ConnectionId)
                ? request.ConnectionId.Trim()
                : store.GetMessages(chat.ChatId).LastOrDefault()?.ConnectionId
                    ?? throw new ArgumentException("connectionId is required for an empty chat.");
        }

        var integration = integrations.Get(connectionId);
        if (requestedFileIds.Count > 0 && !integration.Capabilities.FileInput)
            throw new ArgumentException($"Connection '{connectionId}' does not support file input.");
        var attachedFiles = files.GetMany(requestedFileIds);
        if (attachedFiles.Any(file => file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) &&
            !integration.Capabilities.ImageInput)
            throw new ArgumentException($"Connection '{connectionId}' does not support image input.");

        return ToApi(CreateMessage(
            chat,
            connectionId,
            request.Content ?? string.Empty,
            attachedFiles.Select(file => file.FileId).ToArray(),
            replayOf: null));
    }

    public ApiMessage Replay(string messageId)
    {
        var original = store.GetMessage(messageId)
            ?? throw new KeyNotFoundException($"Message '{messageId}' was not found.");
        if (original.Role != "user")
            throw new KeyNotFoundException("Only user request messages can be replayed.");
        var chat = store.GetChat(original.ChatId)
            ?? throw new KeyNotFoundException($"Chat '{original.ChatId}' was not found.");
        return ToApi(CreateMessage(
            chat,
            original.ConnectionId,
            original.Content,
            original.FileIds,
            original.MessageId));
    }

    public ApiMessage? Get(string messageId)
    {
        var message = store.GetMessage(messageId);
        return message is null ? null : ToApi(message);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = stoppingToken;
        var running = new HashSet<Task>();
        try
        {
            await foreach (var message in _queue.Reader.ReadAllAsync())
            {
                foreach (var completed in running.Where(task => task.IsCompleted).ToArray())
                {
                    await completed;
                    running.Remove(completed);
                }
                running.Add(ProcessAsync(message));
            }
        }
        finally
        {
            if (running.Count > 0)
                await Task.WhenAll(running);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    private StoredMessage CreateMessage(
        ChatRecord chat,
        string connectionId,
        string content,
        IReadOnlyList<string> fileIds,
        string? replayOf)
    {
        var message = new StoredMessage
        {
            MessageId = ChatStore.NewId("msg"),
            ChatId = chat.ChatId,
            ConnectionId = connectionId,
            Role = "user",
            Content = content,
            FileIds = fileIds,
            ReplayOfMessageId = replayOf,
            Status = MessageStatus.Queued
        };
        store.SaveMessage(message);
        store.SaveChat(chat);
        if (_queue.Writer.TryWrite(message))
            return message;

        message.Status = MessageStatus.Failed;
        message.Error = "MEŽS is shutting down and cannot accept more messages.";
        message.CompletedAt = DateTimeOffset.UtcNow;
        store.SaveMessage(message);
        throw new InvalidOperationException(message.Error);
    }

    private async Task ProcessAsync(StoredMessage message)
    {
        var gate = _chatGates.GetOrAdd(message.ChatId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            message.Status = MessageStatus.Running;
            message.StartedAt = DateTimeOffset.UtcNow;
            store.SaveMessage(message);

            var chat = store.GetChat(message.ChatId)
                ?? throw new KeyNotFoundException($"Chat '{message.ChatId}' was not found.");
            var historyMessages = BuildHistory(message);
            var remoteState = chat.RemoteStates.FirstOrDefault(state =>
                string.Equals(state.ConnectionId, message.ConnectionId, StringComparison.OrdinalIgnoreCase));
            var lastHistoryMessageId = historyMessages.LastOrDefault()?.MessageId;
            var continueRemote = remoteState is not null &&
                !string.IsNullOrWhiteSpace(remoteState.LastLocalMessageId) &&
                string.Equals(remoteState.LastLocalMessageId, lastHistoryMessageId, StringComparison.OrdinalIgnoreCase);
            var inputFiles = files.GetMany(message.FileIds)
                .Select(file => new IntegrationInputFile(
                    file.FileId,
                    files.GetContentPath(file),
                    file.Name,
                    file.ContentType,
                    file.Size))
                .ToArray();
            var result = await integrations.Get(message.ConnectionId).SendMessageAsync(
                new IntegrationSendContext(
                    new IntegrationChatContext(
                        chat.ChatId,
                        message.ConnectionId,
                        continueRemote ? remoteState!.RemoteChatUrl : null,
                        continueRemote ? remoteState!.RemoteConversationId : null,
                        continueRemote ? remoteState!.RemoteParentMessageId : null),
                    ToIntegrationMessage(message),
                    historyMessages.Select(ToIntegrationMessage).ToArray(),
                    inputFiles),
                CancellationToken.None);

            var replyFileIds = new List<string>();
            foreach (var output in result.Files ?? [])
            {
                try
                {
                    var imported = await files.ImportAsync(
                        message.ConnectionId,
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
                ConnectionId = message.ConnectionId,
                Role = "assistant",
                Content = result.Text,
                FileIds = replyFileIds,
                ParentMessageId = message.MessageId,
                Status = MessageStatus.Completed,
                StartedAt = message.StartedAt,
                CompletedAt = DateTimeOffset.UtcNow
            };
            store.SaveMessage(reply);

            UpdateRemoteState(chat, message.ConnectionId, remoteState, continueRemote, result, reply.MessageId);
            store.SaveChat(chat);

            message.ReplyMessageId = reply.MessageId;
            message.Status = MessageStatus.Completed;
            message.CompletedAt = reply.CompletedAt;
            store.SaveMessage(message);
        }
        catch (Exception ex)
        {
            message.Status = MessageStatus.Failed;
            message.Error = ex.Message;
            message.CompletedAt = DateTimeOffset.UtcNow;
            store.SaveMessage(message);
        }
        finally
        {
            gate.Release();
        }
    }

    private IReadOnlyList<StoredMessage> BuildHistory(StoredMessage current)
    {
        var messages = store.GetMessages(current.ChatId);
        var byId = messages.ToDictionary(message => message.MessageId, StringComparer.OrdinalIgnoreCase);
        var history = new List<StoredMessage>();
        foreach (var request in messages.Where(message =>
                     message.Role == "user" &&
                     message.Status == MessageStatus.Completed &&
                     ComesBefore(message, current)))
        {
            history.Add(request);
            if (!string.IsNullOrWhiteSpace(request.ReplyMessageId) &&
                byId.TryGetValue(request.ReplyMessageId, out var reply) &&
                reply.Status == MessageStatus.Completed)
                history.Add(reply);
        }
        return history;
    }

    private static bool ComesBefore(StoredMessage candidate, StoredMessage current)
    {
        var time = candidate.CreatedAt.CompareTo(current.CreatedAt);
        return time < 0 ||
               (time == 0 && string.CompareOrdinal(candidate.MessageId, current.MessageId) < 0);
    }

    private static void UpdateRemoteState(
        ChatRecord chat,
        string connectionId,
        ChatConnectionState? state,
        bool continued,
        IntegrationSendResult result,
        string lastLocalMessageId)
    {
        var hasRemoteState = !string.IsNullOrWhiteSpace(result.RemoteChatUrl) ||
                             !string.IsNullOrWhiteSpace(result.RemoteConversationId) ||
                             !string.IsNullOrWhiteSpace(result.RemoteParentMessageId);
        if (!hasRemoteState)
        {
            if (!continued && state is not null)
                chat.RemoteStates.Remove(state);
            return;
        }

        if (state is null)
        {
            state = new ChatConnectionState { ConnectionId = connectionId };
            chat.RemoteStates.Add(state);
        }

        if (continued)
        {
            if (!string.IsNullOrWhiteSpace(result.RemoteChatUrl))
                state.RemoteChatUrl = result.RemoteChatUrl;
            if (!string.IsNullOrWhiteSpace(result.RemoteConversationId))
                state.RemoteConversationId = result.RemoteConversationId;
            if (!string.IsNullOrWhiteSpace(result.RemoteParentMessageId))
                state.RemoteParentMessageId = result.RemoteParentMessageId;
        }
        else
        {
            state.RemoteChatUrl = result.RemoteChatUrl;
            state.RemoteConversationId = result.RemoteConversationId;
            state.RemoteParentMessageId = result.RemoteParentMessageId;
        }
        state.LastLocalMessageId = lastLocalMessageId;
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
    }
}
