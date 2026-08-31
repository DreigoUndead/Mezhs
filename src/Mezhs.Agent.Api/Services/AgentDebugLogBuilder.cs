using System.Globalization;
using System.Text;
using Mezhs.Agent.Models;
using Mezhs.Agent.Persistence;
using Mezhs.Api.Client;

namespace Mezhs.Agent.Services;

public sealed class AgentDebugLogBuilder(
    AgentStore store,
    MezhsApiClient mezhs)
{
    public async Task<string> BuildAsync(
        string chatId,
        CancellationToken cancellationToken)
    {
        var chat = store.GetAgentChat(chatId)
            ?? throw new ResourceNotFoundException($"Agent chat '{chatId}' was not found.");
        var executions = store.GetExecutions(chatId)
            .OrderBy(execution => execution.CreatedAt)
            .ThenBy(execution => execution.ExecutionId, StringComparer.Ordinal)
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

        var active = executions
            .Where(execution => execution.Status is AgentExecutionStatus.Queued or AgentExecutionStatus.Running)
            .ToArray();
        log.AppendLine("=== ACTIVE ===");
        if (active.Length == 0)
        {
            log.AppendLine("none");
        }
        else
        {
            foreach (var execution in active)
            {
                AppendExecutionHeader(log, execution, now);
                AppendBlock(log, "request", execution.Request);
                log.AppendLine();
            }
        }

        log.AppendLine("=== EXECUTIONS ===");
        foreach (var execution in executions)
        {
            AppendExecutionHeader(log, execution, now);
            AppendBlock(log, "request", execution.Request);
            AppendBlock(log, "result", execution.Result);
            AppendBlock(log, "error", execution.Error);
            if (!string.IsNullOrWhiteSpace(execution.PolicySnapshot))
                AppendBlock(log, "policySnapshot", execution.PolicySnapshot);
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

    private static void AppendExecutionHeader(
        StringBuilder log,
        ExecutionRecord execution,
        DateTimeOffset now)
    {
        log.AppendLine(
            $"[{Format(execution.CreatedAt)}] execution={execution.ExecutionId} kind={execution.Kind} status={execution.Status}");
        log.AppendLine($"parentExecutionId: {execution.ParentExecutionId ?? "-"}");
        log.AppendLine($"correlationId: {execution.CorrelationId}");
        log.AppendLine($"source: {execution.Source}");
        log.AppendLine($"startedAt: {(execution.StartedAt is { } started ? Format(started) : "-")}");
        log.AppendLine($"completedAt: {(execution.CompletedAt is { } completed ? Format(completed) : "-")}");
        if (execution.StartedAt is { } startedAt && execution.CompletedAt is null)
            log.AppendLine($"elapsedSeconds: {(now - startedAt).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}");
        if (execution.ExitCode is { } exitCode)
            log.AppendLine($"exitCode: {exitCode}");
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
