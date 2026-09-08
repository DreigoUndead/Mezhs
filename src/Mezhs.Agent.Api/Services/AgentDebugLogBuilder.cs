using System.Globalization;
using System.Text;
using Mezhs.Agent.Models;
using Mezhs.Agent.Persistence;
using Mezhs.Api.Client;
using Mezhs.Executor;

namespace Mezhs.Agent.Services;

public sealed class AgentDebugLogBuilder(
    AgentStore store,
    ExecutorService executor,
    MezhsApiClient mezhs)
{
    public async Task<string> BuildAsync(
        string chatId,
        CancellationToken cancellationToken)
    {
        var chat = store.GetAgentChat(chatId)
            ?? throw new ResourceNotFoundException($"Agent chat '{chatId}' was not found.");
        var agentExecutions = store.GetExecutions(chatId)
            .Where(execution => execution.Kind == AgentExecutionKind.Agent)
            .OrderBy(execution => execution.CreatedAt)
            .ThenBy(execution => execution.ExecutionId, StringComparer.Ordinal)
            .ToArray();
        var shellExecutions = executor.List(chatId, 1000)
            .OrderBy(execution => execution.CreatedAt)
            .ThenBy(execution => execution.Id)
            .ToArray();
        var messages = await mezhs.GetMessagesAsync(chatId, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        var log = new StringBuilder();
        log.AppendLine("MEŽS Agent debug log");
        log.AppendLine($"generatedAt: {Format(now)}");
        log.AppendLine($"chatId: {chat.ChatId}");
        log.AppendLine($"policyId: {chat.PolicyId}");
        log.AppendLine($"originSource: {chat.OriginSource}");
        log.AppendLine($"originReference: {chat.OriginReference ?? "-"}");
        log.AppendLine($"paused: {chat.Paused}");
        if (chat.Environment.Count > 0)
            log.AppendLine($"environment: {string.Join(", ", chat.Environment.Keys.Order(StringComparer.OrdinalIgnoreCase))}");
        log.AppendLine();

        var activeAgents = agentExecutions
            .Where(execution => execution.Status is AgentExecutionStatus.Queued or AgentExecutionStatus.Running or AgentExecutionStatus.CancelRequested)
            .ToArray();
        var activeShells = shellExecutions.Where(execution => !execution.IsTerminal).ToArray();
        log.AppendLine("=== ACTIVE ===");
        if (activeAgents.Length == 0 && activeShells.Length == 0)
        {
            log.AppendLine("none");
        }
        else
        {
            foreach (var execution in activeAgents)
            {
                AppendAgentExecution(log, execution, now);
                log.AppendLine();
            }
            foreach (var execution in activeShells)
            {
                AppendShellExecution(log, execution, now);
                log.AppendLine();
            }
        }

        log.AppendLine("=== AGENT EXECUTIONS ===");
        foreach (var execution in agentExecutions)
        {
            AppendAgentExecution(log, execution, now);
            log.AppendLine();
        }

        log.AppendLine("=== SHELL EXECUTIONS (EXECUTOR) ===");
        foreach (var execution in shellExecutions)
        {
            AppendShellExecution(log, execution, now);
            log.AppendLine();
        }

        log.AppendLine("=== CHAT MESSAGES ===");
        foreach (var message in messages.OrderBy(message => message.CreatedAt))
        {
            log.AppendLine(
                $"[{Format(message.CreatedAt)}] message={message.MessageId} role={message.Role} origin={message.Origin} status={message.Status}");
            if (!string.IsNullOrWhiteSpace(message.ParentMessageId))
                log.AppendLine($"parentMessageId: {message.ParentMessageId}");
            if (!string.IsNullOrWhiteSpace(message.ReplyMessageId))
                log.AppendLine($"replyMessageId: {message.ReplyMessageId}");
            AppendBlock(log, "content", message.Content);
            AppendBlock(log, "error", message.Error);
            log.AppendLine();
        }

        return log.ToString();
    }

    private static void AppendAgentExecution(
        StringBuilder log,
        ExecutionRecord execution,
        DateTimeOffset now)
    {
        log.AppendLine(
            $"[{Format(execution.CreatedAt)}] execution={execution.ExecutionId} kind={execution.Kind} status={execution.Status}");
        log.AppendLine($"parentExecutionId: {execution.ParentExecutionId ?? "-"}");
        log.AppendLine($"correlationId: {execution.CorrelationId}");
        log.AppendLine($"source: {execution.Source}");
        if (!string.IsNullOrWhiteSpace(execution.SourceReference))
            log.AppendLine($"sourceReference: {execution.SourceReference}");
        log.AppendLine($"startedAt: {(execution.StartedAt is { } started ? Format(started) : "-")}");
        log.AppendLine($"completedAt: {(execution.CompletedAt is { } completed ? Format(completed) : "-")}");
        if (execution.StartedAt is { } startedAt && execution.CompletedAt is null)
            log.AppendLine($"elapsedSeconds: {(now - startedAt).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}");
        AppendBlock(log, "request", execution.Request);
        AppendBlock(log, "result", execution.Result);
        AppendBlock(log, "error", execution.Error);
        if (!string.IsNullOrWhiteSpace(execution.PolicySnapshot))
            AppendBlock(log, "policySnapshot", execution.PolicySnapshot);
    }

    private static void AppendShellExecution(
        StringBuilder log,
        Execution execution,
        DateTimeOffset now)
    {
        log.AppendLine(
            $"[{Format(execution.CreatedAt)}] execution={execution.Id} kind=Shell status={execution.Status}");
        log.AppendLine($"parentExecutionId: {execution.ParentExecutionId ?? "-"}");
        log.AppendLine($"correlationId: {execution.CorrelationId ?? "-"}");
        log.AppendLine($"source: {execution.Source ?? "-"}");
        log.AppendLine($"triggerMessageId: {execution.TriggerMessageId ?? "-"}");
        log.AppendLine($"commandIndex: {(execution.CommandIndex?.ToString(CultureInfo.InvariantCulture) ?? "-")}");
        log.AppendLine($"processId: {(execution.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "-")}");
        log.AppendLine($"ownerProcessId: {(execution.OwnerProcessId?.ToString(CultureInfo.InvariantCulture) ?? "-")}");
        log.AppendLine($"startedAt: {(execution.StartedAt is { } started ? Format(started) : "-")}");
        log.AppendLine($"heartbeatAt: {(execution.HeartbeatAt is { } heartbeat ? Format(heartbeat) : "-")}");
        log.AppendLine($"completedAt: {(execution.CompletedAt is { } completed ? Format(completed) : "-")}");
        if (execution.StartedAt is { } startedAt && execution.CompletedAt is null)
            log.AppendLine($"elapsedSeconds: {(now - startedAt).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}");
        if (execution.ExitCode is { } exitCode)
            log.AppendLine($"exitCode: {exitCode}");
        if (execution.RestartedFromId is { } restartedFrom)
            log.AppendLine($"restartedFromId: {restartedFrom}");
        if (execution.RestartedAsId is { } restartedAs)
            log.AppendLine($"restartedAsId: {restartedAs}");
        AppendBlock(log, "command", execution.Command);
        AppendBlock(log, "result", execution.Result);
        AppendBlock(log, "error", execution.Error);
    }

    private static void AppendBlock(
        StringBuilder log,
        string label,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        log.AppendLine($"--- {label} ---");
        log.AppendLine(value.TrimEnd());
        log.AppendLine($"--- /{label} ---");
    }

    private static string Format(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);
}
