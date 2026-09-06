using System.Collections.Concurrent;
using System.Threading.Channels;
using Mezhs.Agent.Commands;
using Mezhs.Agent.Configuration;
using Mezhs.Agent.Models;
using Mezhs.Agent.Persistence;
using Mezhs.Agent.Policy;
using Mezhs.Api.Contracts;
using Mezhs.Api.Client;

namespace Mezhs.Agent.Services;

public sealed class AgentWorker : BackgroundService
{
    private readonly AgentStore _store;
    private readonly PolicyRegistry _policies;
    private readonly MezhsApiClient _mezhs;
    private readonly AgentPromptBuilder _prompts;
    private readonly PolicyEvaluationService _evaluations;
    private readonly Interpreter _commands;
    private readonly Channel<string> _queue;
    private readonly int _maxConcurrentExecutions;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _chatGates =
        new(StringComparer.OrdinalIgnoreCase);
    private int _queuedCount;
    private int _activeCount;

    public AgentWorker(
        AgentStore store,
        PolicyRegistry policies,
        MezhsApiClient mezhs,
        AgentPromptBuilder prompts,
        PolicyEvaluationService evaluations,
        Interpreter commands,
        AgentOptions options)
    {
        _store = store;
        _policies = policies;
        _mezhs = mezhs;
        _prompts = prompts;
        _evaluations = evaluations;
        _commands = commands;
        _maxConcurrentExecutions = options.Runtime.MaxConcurrentExecutions;
        _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(options.Runtime.QueueCapacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public int QueueLength => Volatile.Read(ref _queuedCount);
    public int ActiveExecutions => Volatile.Read(ref _activeCount);

    public bool TryEnqueue(string executionId)
    {
        if (!_queue.Writer.TryWrite(executionId))
            return false;
        Interlocked.Increment(ref _queuedCount);
        return true;
    }

    public ExecutionRecord Cancel(string executionId)
    {
        var (record, changed) = _store.RequestCancel(executionId);
        if (changed && record.Status == AgentExecutionStatus.CancelRequested &&
            _cancellations.TryGetValue(executionId, out var cancellation))
        {
            cancellation.Cancel();
        }
        return record;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumers = Enumerable.Range(0, _maxConcurrentExecutions)
            .Select(_ => ConsumeAsync(stoppingToken))
            .ToArray();
        try
        {
            await Task.WhenAll(consumers);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            while (_queue.Reader.TryRead(out var executionId))
            {
                Interlocked.Decrement(ref _queuedCount);
                _store.Interrupt(executionId);
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        await foreach (var executionId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            Interlocked.Decrement(ref _queuedCount);
            Interlocked.Increment(ref _activeCount);
            try
            {
                await ProcessAsync(executionId, stoppingToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCount);
            }
        }
    }

    private async Task ProcessAsync(string executionId, CancellationToken stoppingToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _cancellations[executionId] = cancellation;
        SemaphoreSlim? chatGate = null;
        var chatGateAcquired = false;

        try
        {
            if (!_store.TryMarkRunning(executionId))
                return;

            var execution = _store.GetExecution(executionId)
                ?? throw new ResourceNotFoundException($"Execution '{executionId}' was not found.");
            var policy = _policies.Get(execution.PolicyId);

            var chatId = execution.ChatId;
            var previouslyOwnedAgentChat = chatId is null ? null : _store.GetAgentChat(chatId);
            if (string.IsNullOrWhiteSpace(chatId))
            {
                chatId = await _mezhs.CreateChatAsync(
                    execution.ConnectionId,
                    cancellation.Token);
                _store.AttachChat(executionId, chatId);
                execution.ChatId = chatId;
                cancellation.Token.ThrowIfCancellationRequested();
            }
            else if (!await _mezhs.ChatExistsAsync(chatId, cancellation.Token))
            {
                throw new ResourceNotFoundException($"Chat '{chatId}' was not found in MEŽS.");
            }

            _store.ClaimAgentChat(
                chatId,
                execution.PolicyId,
                execution.Source,
                execution.SourceReference,
                execution.Environment);
            _store.ValidateAgentChatRunnable(chatId);

            chatGate = _chatGates.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));
            await chatGate.WaitAsync(cancellation.Token);
            chatGateAcquired = true;

            var existingMessages = await _mezhs.GetMessagesAsync(chatId, cancellation.Token);
            var hasCompletedAgentHistory = existingMessages.Any(message =>
                message.Role == "user" && message.Status == MessageStatus.Completed);
            var includePolicyInstructions = previouslyOwnedAgentChat is null || !hasCompletedAgentHistory;
            var nextPrompt = _prompts.BuildInitial(execution, policy, includePolicyInstructions);

            for (var turn = 0; ; turn++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                _store.ValidateAgentChatRunnable(chatId);

                var turnDecision = _evaluations.ValidateTurn(policy, execution, turn);
                if (!turnDecision.Allowed)
                {
                    _store.Fail(executionId, turnDecision.Error ?? "Policy rejected the next agent turn.");
                    return;
                }

                var reply = await _mezhs.SendMessageWithReplyAsync(
                    chatId,
                    execution.ConnectionId,
                    nextPrompt.Content,
                    nextPrompt.Origin,
                    cancellation.Token);

                var interpretation = await _commands.InterpretAsync(
                    execution,
                    policy,
                    reply.MessageId,
                    reply.Content,
                    cancellation.Token);
                if (interpretation.Error is { } commandError)
                {
                    nextPrompt = _prompts.BuildCommandCorrection(commandError);
                    continue;
                }

                if (interpretation.Results.Count > 0)
                {
                    nextPrompt = _prompts.BuildCommandResults(interpretation.Results);
                    continue;
                }

                var completion = _evaluations.EvaluateCompletion(
                    policy,
                    execution,
                    interpretation.CompletionClaimed);
                if (completion.State == PolicyCompletionState.Accepted)
                {
                    if (_store.Complete(executionId, reply.Content))
                        return;
                    if (_store.GetExecution(executionId)?.Status == AgentExecutionStatus.CancelRequested)
                    {
                        _store.CompleteCancellation(executionId);
                        return;
                    }
                    throw new InvalidOperationException("Agent execution changed state before completion could be recorded.");
                }

                nextPrompt = completion.State == PolicyCompletionState.Rejected
                    ? _prompts.BuildPolicyCorrection(completion.Error)
                    : _prompts.BuildContinue();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _store.Interrupt(executionId);
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
            if (chatGate is not null && chatGateAcquired)
                chatGate.Release();
            _cancellations.TryRemove(executionId, out _);
        }
    }
}
