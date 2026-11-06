using System.Threading.Channels;
using Mezhs.Agent.Commands;
using Mezhs.Agent.Models;
using Mezhs.Agent.Persistence;
using Mezhs.Agent.Policy;
using Mezhs.Api.Contracts;
using Mezhs.Executor;
using Mezhs.Services;

namespace Mezhs.Agent.Services;

public sealed class AgentWorker : BackgroundService
{
    private readonly AgentStore _store;
    private readonly PolicyRegistry _policies;
    private readonly ChatService _chats;
    private readonly ChatStore _chatStore;
    private readonly MessageService _messages;
    private readonly AgentPromptBuilder _prompts;
    private readonly PolicyEvaluationService _evaluations;
    private readonly Parser _parser;
    private readonly Interpreter _commands;
    private readonly ExecutorService _executor;
    private readonly AgentRecoveryState _recovery;
    private readonly AgentAttachmentStaging _attachments;
    private readonly Channel<bool> _wake;
    private readonly Dictionary<string, Task> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cancellationSync = new();
    private readonly object _inFlightSync = new();
    // Held only under ChatStore.DeletionSync; no persistent deletion guards.
    private readonly HashSet<string> _deletingChats = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<AgentWorker> _logger;
    private readonly Dictionary<string, CancellationTokenSource> _cancellations =
        new(StringComparer.OrdinalIgnoreCase);

    public AgentWorker(
        AgentStore store,
        PolicyRegistry policies,
        ChatService chats,
        ChatStore chatStore,
        MessageService messages,
        AgentPromptBuilder prompts,
        PolicyEvaluationService evaluations,
        Parser parser,
        Interpreter commands,
        ExecutorService executor,
        AgentRecoveryState recovery,
        AgentAttachmentStaging attachments,
        ILogger<AgentWorker> logger)
    {
        _store = store;
        _policies = policies;
        _chats = chats;
        _chatStore = chatStore;
        _messages = messages;
        _prompts = prompts;
        _evaluations = evaluations;
        _parser = parser;
        _commands = commands;
        _executor = executor;
        _recovery = recovery;
        _attachments = attachments;
        _logger = logger;
        _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });
    }

    public void SignalWork() => _wake.Writer.TryWrite(true);

    // Callers must hold ChatStore.DeletionSync when consulting this gate.
    public bool IsChatDeleting(string chatId) => _deletingChats.Contains(chatId);

    public void BeginChatDeletion(IReadOnlyList<string> ids)
    {
        lock (_chatStore.DeletionSync)
        {
            if (ids.Any(_deletingChats.Contains))
                throw new RequestValidationException("A requested chat is already being deleted.");
            foreach (var id in ids)
                _deletingChats.Add(id);

            try
            {
                // Stop only Agent reasoning. No Executor.Kill and no shell join.
                foreach (var execution in ids.SelectMany(_store.GetExecutions)
                             .Where(item => item.ParentExecutionId is null &&
                                 item.Status is AgentExecutionStatus.Queued or AgentExecutionStatus.Running
                                     or AgentExecutionStatus.CancelRequested))
                {
                    if (execution.Status is AgentExecutionStatus.Queued or AgentExecutionStatus.Running)
                        _store.RequestCancel(execution.ExecutionId);
                    lock (_cancellationSync)
                    {
                        if (_cancellations.TryGetValue(execution.ExecutionId, out var cancellation))
                            cancellation.Cancel();
                    }
                }
                SignalWork();
            }
            catch
            {
                // A failed stop must not leave a permanent admission gate.
                foreach (var id in ids)
                    _deletingChats.Remove(id);
                throw;
            }
        }
    }

    public void EndChatDeletion(IReadOnlyList<string> ids)
    {
        lock (_chatStore.DeletionSync)
        {
            foreach (var id in ids)
                _deletingChats.Remove(id);
        }
    }

    public async Task WaitForChatStopAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var roots = ids.SelectMany(_store.GetExecutions)
                .Where(record => record.ParentExecutionId is null).ToArray();
            var rootIds = roots.Select(record => record.ExecutionId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Task[] inFlight;
            lock (_inFlightSync)
                inFlight = _inFlight.Where(pair => rootIds.Contains(pair.Key))
                    .Select(pair => pair.Value).ToArray();
            var running = roots.Any(record => record.Status is AgentExecutionStatus.Queued
                or AgentExecutionStatus.Running or AgentExecutionStatus.CancelRequested);
            if (!running && inFlight.All(task => task.IsCompleted))
                return;
            if (inFlight.Length > 0)
                await Task.WhenAll(inFlight).WaitAsync(cancellationToken);
            else
                await Task.Delay(50, cancellationToken);
        }
    }

    public ExecutionRecord Cancel(string executionId)
    {
        var current = _store.GetExecution(executionId)
            ?? throw new ResourceNotFoundException($"Execution '{executionId}' was not found.");
        if (current.Kind != AgentExecutionKind.Agent || current.ParentExecutionId is not null)
            throw new RequestValidationException("Only root Agent executions can be cancelled directly.");

        var (record, changed) = _store.RequestCancel(executionId);
        if (changed)
            SignalWork();
        if (!changed || record.Status != AgentExecutionStatus.CancelRequested)
            return record;

        if (!string.IsNullOrWhiteSpace(record.ChatId))
        {
            foreach (var shell in _executor.List(record.ChatId)
                         .Where(shell => !shell.IsTerminal &&
                                         string.Equals(shell.ParentExecutionId, executionId, StringComparison.OrdinalIgnoreCase)))
            {
                _executor.Kill(shell.Id);
            }
        }

        lock (_cancellationSync)
        {
            if (_cancellations.TryGetValue(executionId, out var cancellation))
                cancellation.Cancel();
        }
        return record;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SignalWork();
        try
        {
            await foreach (var _ in _wake.Reader.ReadAllAsync(stoppingToken))
            {
                // Only the dispatcher reads or removes in-flight tasks. SQLite owns runnable order.
                KeyValuePair<string, Task>[] completed;
                lock (_inFlightSync)
                    completed = _inFlight.Where(pair => pair.Value.IsCompleted).ToArray();
                foreach (var (id, task) in completed)
                {
                    await task;
                    lock (_inFlightSync)
                        _inFlight.Remove(id);
                }

                while (!stoppingToken.IsCancellationRequested)
                {
                    var execution = _store.TryClaimNextQueuedExecution();
                    if (execution is null)
                        break;

                    // Schedule independently; a long-running execution never occupies a dispatcher slot.
                    var task = Task.Run(() => RunExecutionAsync(execution, stoppingToken));
                    lock (_inFlightSync)
                        _inFlight.Add(execution.ExecutionId, task);
                    // Notify only once the task itself has completed, so it is always collectible.
                    task.GetAwaiter().OnCompleted(SignalWork);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Stop claiming before waiting. All started executions retain their cancellation lifetime.
            Task[] remaining;
            lock (_inFlightSync)
                remaining = _inFlight.Values.ToArray();
            await Task.WhenAll(remaining);
            lock (_inFlightSync)
                _inFlight.Clear();
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _wake.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    private async Task RunExecutionAsync(ExecutionRecord execution, CancellationToken stoppingToken)
    {
        try
        {
            await ProcessAsync(execution, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure in Agent execution {ExecutionId}", execution.ExecutionId);
            try
            {
                if (_store.GetExecution(execution.ExecutionId)?.Status == AgentExecutionStatus.CancelRequested)
                    _store.CompleteCancellation(execution.ExecutionId);
                else
                    _store.Fail(execution.ExecutionId, ex.Message);
            }
            catch (Exception persistenceError)
            {
                _logger.LogError(persistenceError, "Could not persist failure for {ExecutionId}", execution.ExecutionId);
            }
        }
    }

    private async Task ProcessAsync(ExecutionRecord execution, CancellationToken stoppingToken)
    {
        var executionId = execution.ExecutionId;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_cancellationSync)
            _cancellations[executionId] = cancellation;
        try
        {
            lock (_chatStore.DeletionSync)
            {
                if (execution.ChatId is { } existing && _deletingChats.Contains(existing))
                    cancellation.Cancel();
            }
            // Cancellation may have reached SQLite before the token was registered.
            if (_store.GetExecution(executionId)?.Status == AgentExecutionStatus.CancelRequested)
                cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            var policy = _policies.Get(execution.PolicyId);
            var recovering = _recovery.TryTake(executionId);

            var chatId = execution.ChatId;
            AgentChatRecord? previouslyOwnedAgentChat;
            // A new core chat is not visible to deletion before its Agent claim.
            lock (_chatStore.DeletionSync)
            {
                if (chatId is not null && _deletingChats.Contains(chatId))
                    throw new OperationCanceledException(cancellation.Token);
                previouslyOwnedAgentChat = chatId is null ? null : _store.GetAgentChat(chatId);
                if (string.IsNullOrWhiteSpace(chatId))
                {
                    chatId = _chats.Create(new CreateChatRequest(execution.ConnectionId)).ChatId;
                    cancellation.Token.ThrowIfCancellationRequested();
                }
                else
                {
                    chatId = _chatStore.GetChat(chatId)?.ChatId
                        ?? throw new ResourceNotFoundException($"Chat '{chatId}' was not found in MEŽS.");
                }

                _store.ClaimAgentChat(
                    executionId,
                    chatId,
                    execution.PolicyId,
                    execution.Source,
                    execution.SourceReference,
                    execution.Environment);
            }
            execution.ChatId = chatId;
            _store.ValidateAgentChatRunnable(chatId);

            var existingMessages = _chats.GetMessages(chatId);
            _attachments.Reconcile(executionId, existingMessages);
            var hasCompletedAgentHistory = existingMessages.Any(message =>
                message.Role == "user" && message.Status == MessageStatus.Completed);
            var includePolicyInstructions = previouslyOwnedAgentChat is null || !hasCompletedAgentHistory;
            AgentPrompt nextPrompt;

            if (recovering && await RecoverReplyAsync(existingMessages, cancellation.Token) is { } recoveredReply)
            {
                var recovered = await ProcessReplyAsync(
                    execution,
                    policy,
                    recoveredReply.MessageId,
                    recoveredReply.Content,
                    cancellation.Token);
                if (recovered.Completed)
                    return;
                nextPrompt = recovered.NextPrompt!;
            }
            else
            {
                nextPrompt = _prompts.BuildInitial(execution, policy, includePolicyInstructions);
            }

            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                _store.ValidateAgentChatRunnable(chatId);

                var reply = await _messages.SendWithReplyAsync(
                    new PostMessageRequest(
                        Content: nextPrompt.Content,
                        ConnectionId: execution.ConnectionId,
                        ChatId: chatId,
                        Origin: nextPrompt.Origin,
                        Model: execution.Model,
                        FileIds: _attachments.Pending(executionId)),
                    cancellation.Token,
                    posted => _attachments.MarkSubmitted(executionId, posted));

                var processed = await ProcessReplyAsync(
                    execution,
                    policy,
                    reply.MessageId,
                    reply.Content,
                    cancellation.Token);
                if (processed.Completed)
                    return;
                nextPrompt = processed.NextPrompt!;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Executor work remains detached. AgentRecoveryState requeues this root on the next boot.
        }
        catch (OperationCanceledException)
        {
            _store.CompleteCancellation(executionId);
        }
        catch (Exception ex)
        {
            if (_store.GetExecution(executionId)?.Status == AgentExecutionStatus.CancelRequested)
                _store.CompleteCancellation(executionId);
            else
                _store.Fail(executionId, ex.Message);
        }
        finally
        {
            // Serialize disposal against concurrent Cancel(), including its synchronous callbacks.
            lock (_cancellationSync)
            {
                _cancellations.Remove(executionId);
                cancellation.Dispose();
            }
        }
    }

    private async Task<RecoveredReply?> RecoverReplyAsync(
        IReadOnlyList<ApiChatHistoryMessage> messages,
        CancellationToken cancellationToken)
    {
        var latest = messages
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.MessageId, StringComparer.Ordinal)
            .LastOrDefault();
        if (latest is null)
            return null;

        if (string.Equals(latest.Role, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            if (latest.Status is MessageStatus.Failed or MessageStatus.Cancelled)
                throw new InvalidOperationException(latest.Error ?? $"Latest assistant message ended as {latest.Status}.");
            return new RecoveredReply(latest.MessageId, latest.Content);
        }

        if (!string.Equals(latest.Role, "user", StringComparison.OrdinalIgnoreCase))
            return null;

        var reply = await _messages.WaitForReplyAsync(latest.MessageId, cancellationToken);
        return new RecoveredReply(reply.MessageId, reply.Content);
    }

    private async Task<ReplyProcessing> ProcessReplyAsync(
        ExecutionRecord execution,
        PolicyContext policy,
        string replyMessageId,
        string replyContent,
        CancellationToken cancellationToken)
    {
        var interpretation = await _commands.InterpretAsync(
            execution,
            policy,
            replyMessageId,
            replyContent,
            cancellationToken);
        if (interpretation.Error is { } commandError)
        {
            if (interpretation.Results.Count == 0)
                return new ReplyProcessing(false, _prompts.BuildCommandCorrection(commandError));
            var resultsPrompt = _prompts.BuildCommandResults(interpretation.Results, policy);
            return new ReplyProcessing(false, resultsPrompt with
            {
                Content = $"Command execution stopped: {commandError}\nPreviously executed command results follow; do not replay them.\n{resultsPrompt.Content}"
            });
        }

        if (interpretation.Results.Count > 0)
        {
            if (interpretation.CompletionClaimed &&
                _attachments.Pending(execution.ExecutionId).Count == 0 &&
                interpretation.Results.All(result => result.Succeeded))
            {
                var sameTurnCompletion = _evaluations.EvaluateCompletion(policy, execution, completionClaimed: true);
                if (sameTurnCompletion.State == PolicyCompletionState.Accepted)
                    return CompleteExecution(execution);
            }

            return new ReplyProcessing(false, _prompts.BuildCommandResults(interpretation.Results, policy));
        }

        var completion = _evaluations.EvaluateCompletion(
            policy,
            execution,
            interpretation.CompletionClaimed);
        if (completion.State == PolicyCompletionState.Accepted &&
            _attachments.Pending(execution.ExecutionId).Count == 0)
            return CompleteExecution(execution);

        return new ReplyProcessing(
            false,
            completion.State == PolicyCompletionState.Rejected
                ? _prompts.BuildPolicyCorrection(completion.Error)
                : _prompts.BuildContinue(policy));
    }

    private ReplyProcessing CompleteExecution(ExecutionRecord execution)
    {
        if (_store.Complete(execution.ExecutionId, BuildExecutionResult(execution)))
            return new ReplyProcessing(true, null);
        if (_store.GetExecution(execution.ExecutionId)?.Status == AgentExecutionStatus.CancelRequested)
        {
            _store.CompleteCancellation(execution.ExecutionId);
            return new ReplyProcessing(true, null);
        }
        throw new InvalidOperationException("Agent execution changed state before completion could be recorded.");
    }

    private string? BuildExecutionResult(ExecutionRecord execution)
    {
        if (string.IsNullOrWhiteSpace(execution.ChatId))
            return null;

        var visible = new List<string>();
        foreach (var message in GetExecutionMessages(_chats.GetMessages(execution.ChatId), execution.ExecutionId))
        {
            if (!string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ||
                message.Status != MessageStatus.Completed)
            {
                continue;
            }

            try
            {
                var content = _parser.Parse(message.Content).VisibleContent;
                if (!string.IsNullOrWhiteSpace(content))
                    visible.Add(content.Trim());
            }
            catch (CommandParseException)
            {
                // Malformed protocol turns were rejected by runtime validation and are not execution results.
            }
        }

        return visible.Count == 0 ? null : string.Join("\n\n", visible);
    }

    private static IEnumerable<ApiChatHistoryMessage> GetExecutionMessages(
        IReadOnlyList<ApiChatHistoryMessage> messages,
        string executionId)
    {
        var envelope = $"[MEŽS AGENT EXECUTION {executionId}]";
        var inExecution = false;
        foreach (var message in messages
                     .OrderBy(message => message.CreatedAt)
                     .ThenBy(message => message.MessageId, StringComparer.Ordinal))
        {
            if (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) &&
                message.Content.StartsWith("[MEŽS AGENT EXECUTION ", StringComparison.Ordinal))
            {
                inExecution = message.Content.StartsWith(envelope, StringComparison.Ordinal);
                continue;
            }

            if (inExecution)
                yield return message;
        }
    }

    private sealed record RecoveredReply(string MessageId, string Content);
    private sealed record ReplyProcessing(bool Completed, AgentPrompt? NextPrompt);
}