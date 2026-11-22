using System.Globalization;
using Mezhs.Agent.Configuration;
using Mezhs.Executor;

namespace Mezhs.Agent.Commands;

public sealed class Shell(
    ExecutorService executor,
    AgentOptions options)
{
    public async Task<Result> ExecuteAsync(
        ExecutionContext context,
        string commandText,
        CancellationToken cancellationToken)
    {
        var definition = Registry.Get(CommandBehavior.Shell);
        if (string.IsNullOrWhiteSpace(commandText))
            return new Result(definition.Name, null, false, null, null, "Shell command body cannot be empty.");

        var environment = ExecutorEnvironment.SnapshotForChildExecution()
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in context.ParentExecution.Environment)
            environment[name] = value;

        environment[ExecutorEnvironment.ExecutionIdVariable] = context.ParentExecution.ExecutionId;
        environment[ExecutorEnvironment.CorrelationIdVariable] = context.ParentExecution.CorrelationId;
        environment[ExecutorEnvironment.SourceVariable] = context.ParentExecution.Source;
        environment[ExecutorEnvironment.WorkspaceVariable] = options.Workspace;
        environment[ExecutorEnvironment.TriggerMessageIdVariable] = context.TriggerMessageId;
        environment[ExecutorEnvironment.CommandIndexVariable] = context.CommandIndex.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(context.ParentExecution.ChatId))
            environment[ExecutorEnvironment.ChatIdVariable] = context.ParentExecution.ChatId;

        var limits = context.PolicyDefaults;
        var defaults = limits is null ? new ExecutorShellDefaults() : new ExecutorShellDefaults(
            limits.CommandTimeoutSeconds, limits.Limit, limits.LimitStart, limits.LimitEnd, limits.Regex);
        try
        {
            var result = await executor.ExecuteShellAsync(
                commandText, options.Workspace, context.Attributes, defaults,
                environment, cancellationToken);
            return new Result(definition.Name, result.ExecutionId, result.Succeeded,
                result.ExitCode, result.Output, result.Error,
                result.RecoveryCommand, result.RecoveryNote);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The command could have started; let Interpreter mark its outcome uncertain.
            throw new InvalidOperationException(
                $"Executor shell result is unavailable: {ex.Message}", ex);
        }
    }
}
