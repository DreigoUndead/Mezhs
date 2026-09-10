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

        var environment = ExecutorEnvironment.SnapshotCurrent()
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

        var timeoutSeconds = Math.Max(1, checked((int)Math.Ceiling(context.Timeout.TotalSeconds)));
        int id;
        try
        {
            id = executor.Execute(commandText, options.Workspace, timeoutSeconds, environment);
        }
        catch (Exception ex)
        {
            return new Result(definition.Name, null, false, null, null, ex.Message);
        }

        var execution = await executor.WaitAsync(id, cancellationToken);
        return new Result(
            definition.Name,
            execution.Id.ToString(CultureInfo.InvariantCulture),
            execution.Status == ExecutionStatus.Completed,
            execution.ExitCode,
            execution.Result,
            execution.Error);
    }
}
