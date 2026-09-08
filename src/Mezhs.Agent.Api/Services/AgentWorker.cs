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
    private readonly Channel<bool> _wake;
    private readonly int _maxConcurrentExecutions;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations =
        new(StringComparer.OrdinalIgnoreCase);

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
                    nextPrompt = _prompts.BuildCommandResults(interpretation.Results, policy);
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
                    : _prompts.BuildContinue(policy);
            }
        }
        catch (ShellTerminationException ex)
        {
            _store.Fail(executionId, ex.Message);
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
            _cancellations.TryRemove(executionId, out _);
        }
    }
}
