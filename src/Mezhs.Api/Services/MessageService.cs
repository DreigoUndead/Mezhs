using System.Collections.Concurrent;
using System.Threading.Channels;
using Mezhs;
using Mezhs.Api.Contracts;
using Mezhs.Integrations;
using Mezhs.Models;
using Microsoft.Extensions.Hosting;

namespace Mezhs.Services;

public sealed class MessageService(
    ChatStore store,
    FileStore files,
    IntegrationRegistry integrations) : BackgroundService
{
    private readonly Channel<QueuedMessage> _queue = Channel.CreateUnbounded<QueuedMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _chatGates =
        new(StringComparer.OrdinalIgnoreCase);

    public ApiMessage Post(PostMessageRequest request) =>
        Post(request, CancellationToken.None);

    private ApiMessage Post(PostMessageRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var requestedFileIds = request.FileIds ?? [];
        if (string.IsNullOrWhiteSpace(request.Content) && requestedFileIds.Count == 0)
            throw new RequestValidationException("content or at least one file is required.");

        ChatRecord chat;
        string connectionId;
        if (string.IsNullOrWhiteSpace(request.ChatId))
        {
            if (string.IsNullOrWhiteSpace(request.ConnectionId))
                throw new RequestValidationException("connectionId is required when chatId is not provided.");
            connectionId = request.ConnectionId.Trim();
            chat = store.CreateChat(request.CategoryId);
        }
        else
        {
            chat = store.GetChat(request.ChatId)
                ?? throw new ResourceNotFoundException($"Chat '{request.ChatId}' was not found.");
            connectionId = !string.IsNullOrWhiteSpace(request.ConnectionId)
                ? request.ConnectionId.Trim()
                : store.GetMessages(chat.ChatId).LastOrDefault()?.ConnectionId
                    ?? throw new RequestValidationException("connectionId is required for an empty chat.");
        }

        var integration = integrations.Get(connectionId);
        var model = request.ModelSpecified
            ? NormalizeModel(request.Model)
            : RestoreModel(chat.ChatId, connectionId);
        if (model is not null && integration.Models is null)
            throw new ArgumentException($"Connection '{connectionId}' does not support model selection.");
        if (requestedFileIds.Count > 0 && !integration.Capabilities.FileInput)
            throw new RequestValidationException($"Connection '{connectionId}' does not support file input.");
        var attachedFiles = files.GetMany(requestedFileIds);
        if (attachedFiles.Any(file => file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) &&
            !integration.Capabilities.ImageInput)
            throw new RequestValidationException($"Connection '{connectionId}' does not support image input.");

        return ToApi(CreateMessage(
            chat,
            connectionId,
            request.Content ?? string.Empty,
            attachedFiles.Select(file => file.FileId).ToArray(),
            NormalizeOrigin(request.Origin),
            model,
            replayOf: null,
            cancellationToken));
    }

    public ApiMessage Replay(string messageId)
    {
        var original = store.GetMessage(messageId)
            ?? throw new ResourceNotFoundException($"Message '{messageId}' was not found.");
        if (original.Role != "user")
            throw new ResourceNotFoundException("Only user request messages can be replayed.");
        var chat = store.GetChat(original.ChatId)
            ?? throw new ResourceNotFoundException($"Chat '{original.ChatId}' was not found.");
        return ToApi(CreateMessage(
            chat,
            original.ConnectionId,
            original.Content,
            original.FileIds,
            NormalizeStoredOrigin(original),
            original.Model,
            original.MessageId,
            CancellationToken.None));
    }

    public ApiMessage? Get(string messageId)
    {
        var message = store.GetMessage(messageId);
        return message is null ? null : ToApi(message);
    }

    public async Task<ApiMessage> SendWithReplyAsync(
        PostMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        var created = Post(request, cancellationToken);
        return await WaitForReplyAsync(created.MessageId, cancellationToken);
    }

    public async Task<ApiMessage> WaitForReplyAsync(
        string messageId,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var message = Get(messageId)
                ?? throw new ResourceNotFoundException($"Message '{messageId}' was not found.");

            switch (message.Status)
            {
                case MessageStatus.Completed:
                    return message.Reply
                        ?? throw new InvalidOperationException("MEŽS completed without an assistant reply.");
                case MessageStatus.Failed:
                case MessageStatus.Cancelled:
                    throw new InvalidOperationException(
                        message.Error ?? $"MEŽS message ended with status {message.Status}.");
                default:
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                    break;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = stoppingToken;
        var running = new HashSet<Task>();
        try
        {
            await foreach (var queued in _queue.Reader.ReadAllAsync())
            {
                foreach (var completed in running.Where(task => task.IsCompleted).ToArray())
                {
                    await completed;
                    running.Remove(completed);
                }
                running.Add(ProcessAsync(queued.Message, queued.CancellationToken));
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
        string origin,
        string? model,
        string? replayOf,
        CancellationToken cancellationToken)
    {
        var message = new StoredMessage
        {
            MessageId = ChatStore.NewId("msg"),
            ChatId = chat.ChatId,
            ConnectionId = connectionId,
            Role = "user",
            Origin = origin,
            Content = content,
            Model = model,
            FileIds = fileIds,
            ReplayOfMessageId = replayOf,
            Status = MessageStatus.Queued
        };
        store.SaveMessage(message);
        store.SaveChat(chat);
        if (_queue.Writer.TryWrite(new QueuedMessage(message, cancellationToken)))
            return message;

        message.Status = MessageStatus.Failed;
        message.Error = "MEŽS is shutting down and cannot accept more messages.";
        message.CompletedAt = DateTimeOffset.UtcNow;
        store.SaveMessage(message);
        throw new InvalidOperationException(message.Error);
    }

    private async Task ProcessAsync(StoredMessage message, CancellationToken cancellationToken)
    {
        var gate = _chatGates.GetOrAdd(message.ChatId, _ => new SemaphoreSlim(1, 1));
        var gateTaken = false;
        try
        {
            await gate.WaitAsync(cancellationToken);
            gateTaken = true;
            cancellationToken.ThrowIfCancellationRequested();

            message.Status = MessageStatus.Running;
            message.StartedAt = DateTimeOffset.UtcNow;
            store.SaveMessage(message);

            var chat = store.GetChat(message.ChatId)
                ?? throw new ResourceNotFoundException($"Chat '{message.ChatId}' was not found.");
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
                    inputFiles,
                    RestoreConversation: !continueRemote,
                    ReportActivity: activity => UpdateActivity(message, activity)),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

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

            cancellationToken.ThrowIfCancellationRequested();
            var reply = new StoredMessage
            {
                MessageId = ChatStore.NewId("msg"),
                ChatId = chat.ChatId,
                ConnectionId = message.ConnectionId,
                Role = "assistant",
                Origin = "assistant",
                Content = result.Text,
                Model = NormalizeModel(result.Model),
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (store.GetChat(message.ChatId) is null)
                return;
            message.Status = MessageStatus.Cancelled;
            message.Error = "MEŽS message was cancelled.";
            message.CompletedAt = DateTimeOffset.UtcNow;
            try { store.SaveMessage(message); }
            catch (ResourceNotFoundException) { }
        }
        catch (Exception ex)
        {
            if (store.GetChat(message.ChatId) is null)
                return;
            message.Status = MessageStatus.Failed;
            message.Error = ex.Message;
            message.CompletedAt = DateTimeOffset.UtcNow;
            try { store.SaveMessage(message); }
            catch (ResourceNotFoundException) { }
        }
        finally
        {
            if (gateTaken)
                gate.Release();
        }
    }

    private void UpdateActivity(StoredMessage message, IntegrationActivity activity)
    {
        if (message.Status != MessageStatus.Running)
            return;

        var state = activity.State?.Trim();
        if (string.IsNullOrWhiteSpace(state))
            return;
        var detail = string.IsNullOrWhiteSpace(activity.Detail) ? null : activity.Detail.Trim();
        var analysis = string.IsNullOrWhiteSpace(activity.Analysis)
            ? message.Analysis
            : activity.Analysis.Trim();

        var stateChanged =
            !string.Equals(message.Activity, state, StringComparison.Ordinal) ||
            !string.Equals(message.ActivityDetail, detail, StringComparison.Ordinal);
        var analysisChanged = !string.Equals(message.Analysis, analysis, StringComparison.Ordinal);
        if (!stateChanged && !analysisChanged)
            return;

        var activityAt = DateTimeOffset.UtcNow;
        message.Activity = state;
        message.ActivityDetail = detail;
        message.Analysis = analysis;
        message.ActivityAt = activityAt;

        // StoredMessage is the live in-memory source used by API reads. Persist state
        // transitions, but do not append the entire growing analysis on every poll.
        // The normal terminal SaveMessage persists the latest analysis once more.
        if (stateChanged)
        {
            message.ActivityHistory.Add(new MessageActivity(state, detail, activityAt));
            store.SaveMessage(message);
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

    private string? RestoreModel(string chatId, string connectionId) =>
        store.GetMessages(chatId)
            .LastOrDefault(message =>
                message.Role == "user" &&
                string.Equals(message.ConnectionId, connectionId, StringComparison.OrdinalIgnoreCase))
            ?.Model;

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
        message.CreatedAt,
        message.Model);

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
            message.Model,
            message.Activity,
            message.ActivityDetail,
            message.Analysis,
            message.ActivityAt,
            message.ActivityHistory);
    }

    private static string NormalizeStoredOrigin(StoredMessage message)
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

    private sealed record QueuedMessage(
        StoredMessage Message,
        CancellationToken CancellationToken);
}
