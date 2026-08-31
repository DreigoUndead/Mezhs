using Mezhs.Agent.Models;
using Mezhs.Agent.Policy;

namespace Mezhs.Agent.Commands;

public sealed record ExecutionContext(
    ExecutionRecord ParentExecution,
    TimeSpan Timeout);

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
    Shell shell)
{
    public async Task<Interpretation> InterpretAsync(
        ExecutionRecord execution,
        PolicyContext policy,
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

        var results = new List<Result>();
        var completionClaimed = false;
        var timeout = TimeSpan.FromSeconds(policy.Settings.Limits.CommandTimeoutSeconds);

        for (var i = 0; i < batch.Commands.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = batch.Commands[i];
            if (!Registry.TryGet(command.Name, out var definition))
                return new Interpretation(completionClaimed, results, $"Unknown agent command <{command.Name}>.");

            var hasBody = command.Body is not null;
            if (definition.Form == CommandForm.Block && !hasBody)
                return new Interpretation(completionClaimed, results, $"<{definition.Name}> requires a closing </{definition.Name}> tag.");
            if (definition.Form == CommandForm.Marker && hasBody)
                return new Interpretation(completionClaimed, results, $"<{definition.Name}> is a marker and cannot have a body.");

            if (definition.Behavior == CommandBehavior.Complete)
            {
                if (completionClaimed)
                    return new Interpretation(true, results, $"<{definition.Name}> may appear only once in an assistant reply.");
                if (i != batch.Commands.Count - 1)
                    return new Interpretation(true, results, $"<{definition.Name}> must be the final agent command in an assistant reply.");
                completionClaimed = true;
                continue;
            }

            var decision = policy.ValidateAction(new PolicyActionContext(
                evaluations.Create(execution),
                new PolicyAction(definition.Name, command.Body ?? string.Empty)));
            if (!decision.Allowed)
                return new Interpretation(completionClaimed, results, decision.Error ?? $"Policy denied {definition.Name} action.");

            switch (definition.Behavior)
            {
                case CommandBehavior.Shell:
                    results.Add(await shell.ExecuteAsync(
                        new ExecutionContext(execution, timeout),
                        command.Body ?? string.Empty,
                        cancellationToken));
                    break;
                default:
                    return new Interpretation(completionClaimed, results, $"Agent command <{definition.Name}> has no executable behavior.");
            }
        }

        return new Interpretation(completionClaimed, results, null);
    }
}
