using Mezhs.Agent.Models;
using Mezhs.Agent.Policy;

namespace Mezhs.Agent.Commands;

public sealed record ExecutionContext(
    ExecutionRecord ParentExecution,
    TimeSpan Timeout,
    string TriggerMessageId,
    int CommandIndex);

public sealed record Result(
    string Name,
    string? ExecutionId,
    bool Succeeded,
    int? ExitCode,
    string? Output,
    string? Error);

public sealed record Interpretation(
    bool CompletionClaimed,
    IReadOnlyList<Result> Results,
    string? Error);

public sealed class Interpreter(
    Parser parser,
    PolicyEvaluationService evaluations,
    Shell shell,
    Mezhs.Agent.Services.AgentAttachmentStaging attachments)
{
    public async Task<Interpretation> InterpretAsync(
        ExecutionRecord execution,
        PolicyContext policy,
        string assistantMessageId,
        string assistantReply,
        CancellationToken cancellationToken)
    {
        CommandBatch batch;
        try
        {
            batch = parser.Parse(assistantReply);
        }
        catch (CommandParseException ex)
        {
            return new Interpretation(false, [], ex.Message);
        }

        var executable = new List<(CommandDefinition Definition, string Body)>();
        var completionClaimed = false;

        for (var i = 0; i < batch.Commands.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = batch.Commands[i];
            if (!Registry.TryGet(command.Name, out var definition))
                return new Interpretation(completionClaimed, [], $"Unknown agent command <{command.Name}>.");

            var hasBody = command.Body is not null;
            if (definition.Form == CommandForm.Block && !hasBody)
                return new Interpretation(completionClaimed, [], $"<{definition.Name}> requires a closing </{definition.Name}> tag.");
            if (definition.Form == CommandForm.Marker && hasBody)
                return new Interpretation(completionClaimed, [], $"<{definition.Name}> is a marker and cannot have a body.");

            if (definition.Behavior == CommandBehavior.Complete)
            {
                if (completionClaimed)
                    return new Interpretation(true, [], $"<{definition.Name}> may appear only once in an assistant reply.");
                if (i != batch.Commands.Count - 1)
                    return new Interpretation(true, [], $"<{definition.Name}> must be the final agent command in an assistant reply.");
                completionClaimed = true;
                continue;
            }

            var body = command.Body ?? string.Empty;
            if (definition.Behavior == CommandBehavior.Shell && string.IsNullOrWhiteSpace(body))
                return new Interpretation(completionClaimed, [], "Shell command body cannot be empty.");
            var decision = evaluations.ValidateAction(
                policy,
                execution,
                new PolicyAction(definition, body));
            if (!decision.Allowed)
                return new Interpretation(completionClaimed, [], decision.Error ?? $"Policy denied {definition.Name} action.");

            if (definition.Behavior is not (CommandBehavior.Shell or CommandBehavior.Attach))
                return new Interpretation(completionClaimed, [], $"Agent command <{definition.Name}> has no executable behavior.");
            executable.Add((definition, body));
        }

        var results = new List<Result>();
        var timeout = TimeSpan.FromSeconds(policy.Settings.Limits.CommandTimeoutSeconds);
        var executableIndex = 0;
        foreach (var (definition, body) in executable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = new ExecutionContext(execution, timeout, assistantMessageId, executableIndex);
            try
            {
                results.Add(definition.Behavior == CommandBehavior.Shell
                    ? await shell.ExecuteAsync(context, body, cancellationToken)
                    : await attachments.StageAsync(context, body, cancellationToken));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // A command may already have started before throwing; the outcome is not safe to replay.
                results.Add(new Result(definition.Name, null, false, null, null,
                    $"Execution result unavailable: {ex.Message}. Check durable execution history before retrying."));
                return new Interpretation(false, results, $"Execution of <{definition.Name}> was interrupted.");
            }
            executableIndex++;
        }

        return new Interpretation(completionClaimed, results, null);
    }
}