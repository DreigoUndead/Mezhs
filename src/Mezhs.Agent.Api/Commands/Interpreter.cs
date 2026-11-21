using Mezhs.Agent.Models;
using Mezhs.Agent.Policy;
using System.Text.RegularExpressions;

namespace Mezhs.Agent.Commands;

public sealed record ExecutionContext(
    ExecutionRecord ParentExecution,
    TimeSpan Timeout,
    string TriggerMessageId,
    int CommandIndex,
    ShellOutputOptions? OutputOptions = null);

public sealed record Result(
    string Name,
    string? ExecutionId,
    bool Succeeded,
    int? ExitCode,
    string? Output,
    string? Error,
    ShellOutputOptions? OutputOptions = null);

public sealed record ShellOutputOptions(int Limit, int LimitStart, int LimitEnd, string? Regex)
{
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    public static void ValidateRegex(string pattern)
    {
        try { _ = new Regex(pattern, RegexOptions.CultureInvariant, MatchTimeout); }
        catch (ArgumentException ex)
        {
            throw new CommandParseException($"Invalid SH regex: {ex.Message}");
        }
    }

    public static ShellOutputOptions Resolve(Command command, PolicyLimitsSettings defaults)
    {
        var limit = command.OutputLimit ?? defaults.Limit ?? 0;
        var start = command.OutputLimitStart ?? defaults.LimitStart ?? 10;
        var end = command.OutputLimitEnd ?? defaults.LimitEnd ?? 10;
        var explicitStart = command.OutputLimitStart.HasValue || defaults.LimitStart.HasValue;
        var explicitEnd = command.OutputLimitEnd.HasValue || defaults.LimitEnd.HasValue;
        if (limit > 0)
        {
            if (explicitStart && start > limit || explicitEnd && end > limit ||
                explicitStart && explicitEnd && (long)start + end > limit)
                throw new CommandParseException(
                    "Explicit SH limitStart/limitEnd values exceed the effective SH limit.");
            if (explicitStart && !explicitEnd)
                end = Math.Min(end, limit - start);
            else if (explicitEnd && !explicitStart)
                start = Math.Min(start, limit - end);
            else if (!explicitStart && !explicitEnd)
            {
                start = Math.Min(start, limit / 2 + limit % 2);
                end = Math.Min(end, limit - start);
            }
        }
        var regex = command.OutputRegex ?? defaults.Regex;
        if (regex is not null)
            ValidateRegex(regex);
        return new ShellOutputOptions(limit, start, end, regex);
    }
}

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

        var executable = new List<(CommandDefinition Definition, string Body,
            int TimeoutSeconds, ShellOutputOptions? OutputOptions)>();
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
            ShellOutputOptions? outputOptions = null;
            if (definition.Behavior == CommandBehavior.Shell)
            {
                try { outputOptions = ShellOutputOptions.Resolve(command, policy.Settings.Limits); }
                catch (CommandParseException ex)
                {
                    return new Interpretation(completionClaimed, [], ex.Message);
                }
            }
            executable.Add((definition, body,
                command.TimeoutSeconds ?? policy.Settings.Limits.CommandTimeoutSeconds,
                outputOptions));
        }

        var results = new List<Result>();
        var executableIndex = 0;
        foreach (var (definition, body, commandTimeout, outputOptions) in executable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = new ExecutionContext(execution, TimeSpan.FromSeconds(commandTimeout),
                assistantMessageId, executableIndex, outputOptions);
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