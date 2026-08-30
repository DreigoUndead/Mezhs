using System.Collections.Concurrent;
using System.Threading.Channels;
using Mezhs.Agent.Commands;
using Mezhs.Agent.Models;
using Mezhs.Agent.Persistence;
using Mezhs.Agent.Policy;
using Mezhs.Api.Contracts;

namespace Mezhs.Agent.Services;

public sealed class AgentWorker(
    AgentStore store,
    PolicyRegistry policies,
    MezhsClient mezhs,
    AgentPromptBuilder prompts,
    PolicyEvaluationService evaluations,
    AgentCommandInterpreter commands) : BackgroundService
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _chatGates =
        new(StringComparer.OrdinalIgnoreCase);

    public void Enqueue(string executionId)
    {
        if (_queue.Writer.TryWrite(executionId))
            return;

        store.Fail(executionId, "MEŽS Agent is shutting down and cannot accept more work.");
        throw new InvalidOperationException("MEŽS Agent is shutting down and cannot accept more work.");
    }

    public ExecutionRecord Cancel(string executionId)
    {
        var (record, changed) = store.Cancel(executionId);
        if (changed && _cancellations.TryGetValue(executionId, out var cancellation))
            cancellation.Cancel();
        return record;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new HashSet<Task>();
        try
        {
            await foreach (var executionId in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                foreach (var completed in running.Where(task => task.IsCompleted).ToArray())
                {
                    await completed;
                    running.Remove(completed);
                }
                running.Add(ProcessAsync(executionId, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
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

    private async Task ProcessAsync(string executionId, CancellationToken stoppingToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _cancellations[executionId] = cancellation;
        SemaphoreSlim? chatGate = null;
        var chatGateAcquired = false;

        try
        {
            if (!store.TryMarkRunning(executionId))
                return;

            var execution = store.GetExecution(executionId)
                ?? throw new ResourceNotFoundException($"Execution '{executionId}' was not found.");
            var policy = policies.Get(execution.PolicyId);

            var chatId = execution.ChatId;
            var previouslyOwnedAgentChat = chatId is null ? null : store.GetAgentChat(chatId);
            if (string.IsNullOrWhiteSpace(chatId))
            {
                chatId = await mezhs.CreateChatAsync(
                    execution.ConnectionId,
                    cancellation.Token);
                store.AttachChat(executionId, chatId);
                execution.ChatId = chatId;
                cancellation.Token.ThrowIfCancellationRequested();
            }
            else if (!await mezhs.ChatExistsAsync(chatId, cancellation.Token))
            {
                throw new ResourceNotFoundException($"Chat '{chatId}' was not found in MEŽS.");
            }

            store.ClaimAgentChat(
                chatId,
                execution.PolicyId,
                execution.Source,
                execution.SourceReference);
            store.ValidateAgentChatRunnable(chatId);

            chatGate = _chatGates.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));
            await chatGate.WaitAsync(cancellation.Token);
            chatGateAcquired = true;

            var existingMessages = await mezhs.GetMessagesAsync(chatId, cancellation.Token);
            var hasCompletedAgentHistory = existingMessages.Any(message =>
                message.Role == "user" && message.Status == MessageStatus.Completed);
            var includePolicyInstructions = previouslyOwnedAgentChat is null || !hasCompletedAgentHistory;
            var nextPrompt = prompts.BuildInitial(execution, policy, includePolicyInstructions);

            for (var turn = 0; ; turn++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                store.ValidateAgentChatRunnable(chatId);

                var turnDecision = policy.ValidateTurn(
                    new PolicyTurnContext(evaluations.Create(execution), turn));
                if (!turnDecision.Allowed)
                {
                    store.Fail(executionId, turnDecision.Error ?? "Policy rejected the next agent turn.");
                    return;
                }

                var reply = await mezhs.SendMessageAsync(
                    chatId,
                    execution.ConnectionId,
                    nextPrompt,
                    cancellation.Token);

                var interpretation = await commands.InterpretAsync(
                    execution,
                    policy,
                    reply,
                    cancellation.Token);
                if (interpretation.Error is { } commandError)
                {
                    nextPrompt = prompts.BuildCommandCorrection(commandError);
                    continue;
                }

                // Command execution is a round trip. The model must see the actual host
                // results before this turn can satisfy completion, even if it also emitted
                // <DONE> or the policy does not require an explicit completion marker.
                if (interpretation.Results.Count > 0)
                {
                    nextPrompt = prompts.BuildCommandResults(interpretation.Results);
                    continue;
                }

                var completion = policy.EvaluateCompletion(
                    new PolicyCompletionContext(
                        evaluations.Create(execution),
                        interpretation.CompletionClaimed));
                if (completion.State == PolicyCompletionState.Accepted)
                {
                    store.Complete(executionId, reply);
                    return;
                }

                nextPrompt = completion.State == PolicyCompletionState.Rejected
                    ? prompts.BuildPolicyCorrection(completion.Error)
                    : prompts.BuildContinue();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            store.Interrupt(executionId);
        }
        catch (OperationCanceledException)
        {
            // User cancellation is persisted before the token is cancelled.
        }
        catch (Exception ex)
        {
            store.Fail(executionId, ex.Message);
        }
        finally
        {
            if (chatGate is not null && chatGateAcquired)
                chatGate.Release();
            _cancellations.TryRemove(executionId, out _);
        }
    }
}
