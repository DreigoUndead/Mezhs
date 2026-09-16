using System.Collections.Concurrent;
using System.Threading.Channels;
using Mezhs.Agent.Commands;
using Mezhs.Agent.Configuration;
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
    private readonly MessageService _messages;
    private readonly AgentPromptBuilder _prompts;
    private readonly PolicyEvaluationService _evaluations;
    private readonly Interpreter _commands;
    private readonly ExecutorService _executor;
    private readonly AgentRecoveryState _recovery;
    private readonly Channel<bool> _wake;
    private readonly int _maxConcurrentExecutions;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations =
        new(StringComparer.OrdinalIgnoreCase);

    public AgentWorker(
        AgentStore store,
        PolicyRegistry policies,
        ChatService chats,
        MessageService messages,
        AgentPromptBuilder prompts,
        PolicyEvaluationService evaluations,
        Interpreter commands,
        ExecutorService executor,
        AgentRecoveryState recovery,
        AgentOptions options)
    {
        _store = store;
        _policies = policies;
        _chats = chats;
        _messages = messages;
        _prompts = prompts;
        _evaluations = evaluations;
        _commands = commands;
        _executor = executor;
        _recovery = recovery;
        _maxConcurrentExecutions = options.Runtime.MaxConcurrentExecutions;
        _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });
    }

    public void SignalWork() => _wake.Writer.TryWrite(true);

    public ExecutionRecord Cancel(string executionId)
    {
        var current = _store.GetExecution(executionId)
            ?? throw new ResourceNotFoundException($"Execution '{executionId}' was not found.");
        if (current.Kind != AgentExecutionKind.Agent || current.ParentExecutionId is not null)
            throw new RequestValidationException("Only root Agent executions can be cancelled directly.");

        var (record, changed) = _store.RequestCancel(executionId);
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

        if (_cancellations.TryGetValue(executionId, out var cancellation))
            cancellation.Cancel();
        return record;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumers = Enumerable.Range(0, _maxConcurrentExecutions)
            .Select(_ => ConsumeAsync(stoppingToken))
            .ToArray();
        SignalWork();

        try
        {
            await Task.WhenAll(consumers);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _wake.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        await foreach (var _ in _wake.Reader.ReadAllAsync(stoppingToken))
        {
            var execution = _store.TryClaimNextQueuedExecution();
            if (execution is null)
                continue;

            SignalWork();
            try
            {
                await ProcessAsync(execution, stoppingToken);
            }
            finally
            {
                SignalWork();
            }
        }
    }

    private async Task ProcessAsync(ExecutionRecord execution, CancellationToken stoppingToken)
    {
        var executionId = execution.ExecutionId;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _cancellations[executionId] = cancellation;
        if (_store.GetExecution(executionId)?.Status == AgentExecutionStatus.CancelRequested)
            cancellation.Cancel();

        try
        {
            var policy = _policies.Get(execution.PolicyId);
            var recovering = _recovery.TryTake(executionId);

            var chatId = execution.ChatId;
            var previouslyOwnedAgentChat = chatId is null ? null : _store.GetAgentChat(chatId);
            if (string.IsNullOrWhiteSpace(chatId))
            {
                chatId = _chats.Create(new CreateChatRequest(execution.ConnectionId)).ChatId;
                cancellation.Token.ThrowIfCancellationRequested();
            }
            else if (!_chats.Exists(chatId))
            {
                throw new ResourceNotFoundException($"Chat '{chatId}' was not found in MEŽS.");
            }

            _store.ClaimAgentChat(
                executionId,
                chatId,
                execution.PolicyId,
                execution.Source,
                execution.SourceReference,
                execution.Environment);
            execution.ChatId = chatId;
            _store.ValidateAgentChatRunnable(chatId);

            var existingMessages = _chats.GetMessages(chatId);
            var hasCompletedAgentHistory = existingMessages.Any(message =>
                message.Role == "user" && message.Status == MessageStatus.Completed);
            var includePolicyInstructions = previouslyOwnedAgentChat is null || !hasCompletedAgentHistory;
            AgentPrompt nextPrompt;
            var nextTurn = 0;

            if (recovering && await RecoverReplyAsync(existingMessages, cancellation.Token) is { } recoveredReply)
            {
                nextTurn = CountExecutionTurns(existingMessages, executionId);
                if (!existingMessages.Any(message => string.Equals(message.MessageId, recoveredReply.MessageId, StringComparison.Ordinal)))
                    nextTurn++;

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

            for (var turn = nextTurn; ; turn++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                _store.ValidateAgentChatRunnable(chatId);

                var turnDecision = _evaluations.ValidateTurn(policy, execution, turn);
                if (!turnDecision.Allowed)
                {
                    _store.Fail(executionId, turnDecision.Error ?? "Policy rejected the next agent turn.");
                    return;
                }

                var reply = await _messages.SendWithReplyAsync(
                    new PostMessageRequest(
                        Content: nextPrompt.Content,
                        ConnectionId: execution.ConnectionId,
                        ChatId: chatId,
                        Origin: nextPrompt.Origin),
                    cancellation.Token);

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
            _cancellations.TryRemove(executionId, out _);
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
            return new ReplyProcessing(false, _prompts.BuildCommandCorrection(commandError));

        if (interpretation.Results.Count > 0)
        {
            if (interpretation.CompletionClaimed && interpretation.Results.All(result => result.Succeeded))
            {
                var sameTurnCompletion = _evaluations.EvaluateCompletion(policy, execution, completionClaimed: true);
                if (sameTurnCompletion.State == PolicyCompletionState.Accepted)
                    return CompleteExecution(execution, interpretation.VisibleContent);
            }

            return new ReplyProcessing(false, _prompts.BuildCommandResults(interpretation.Results, policy));
        }

        var completion = _evaluations.EvaluateCompletion(
            policy,
            execution,
            interpretation.CompletionClaimed);
        if (completion.State == PolicyCompletionState.Accepted)
            return CompleteExecution(execution, interpretation.VisibleContent);

        return new ReplyProcessing(
            false,
            completion.State == PolicyCompletionState.Rejected
                ? _prompts.BuildPolicyCorrection(completion.Error)
                : _prompts.BuildContinue(policy));
    }

    private ReplyProcessing CompleteExecution(ExecutionRecord execution, string visibleContent)
    {
        var result = string.IsNullOrWhiteSpace(visibleContent) ? null : visibleContent;
        if (_store.Complete(execution.ExecutionId, result))
            return new ReplyProcessing(true, null);
        if (_store.GetExecution(execution.ExecutionId)?.Status == AgentExecutionStatus.CancelRequested)
        {
            _store.CompleteCancellation(execution.ExecutionId);
            return new ReplyProcessing(true, null);
        }
        throw new InvalidOperationException("Agent execution changed state before completion could be recorded.");
    }

    private static int CountExecutionTurns(
        IReadOnlyList<ApiChatHistoryMessage> messages,
        string executionId)
    {
        var envelope = $"[MEŽS AGENT EXECUTION {executionId}]";
        var inExecution = false;
        var count = 0;
        foreach (var message in messages
                     .OrderBy(message => message.CreatedAt)
                     .ThenBy(message => message.MessageId, StringComparer.Ordinal))
        {
            if (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) &&
                message.Content.StartsWith(envelope, StringComparison.Ordinal))
            {
                inExecution = true;
                continue;
            }
            if (inExecution &&
                string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                message.Status == MessageStatus.Completed)
            {
                count++;
            }
        }
        return count;
    }

    private sealed record RecoveredReply(string MessageId, string Content);
    private sealed record ReplyProcessing(bool Completed, AgentPrompt? NextPrompt);
}