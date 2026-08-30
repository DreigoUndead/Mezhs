using Mezhs.Agent.Models;
using Mezhs.Agent.Policy;

namespace Mezhs.Agent.Commands;

public sealed record AgentCommandExecutionContext(
    ExecutionRecord ParentExecution,
    AgentCommand Command);

public sealed record AgentCommandResult(
    string Name,
    string? ExecutionId,
    bool Succeeded,
    int? ExitCode,
    string? Output,
    string? Error);

public sealed record AgentCommandInterpretation(
    bool CompletionClaimed,
    IReadOnlyList<AgentCommandResult> Results,
    string? Error);

public interface IAgentCommandHandler
{
    string Name { get; }

    Task<AgentCommandResult> ExecuteAsync(
        AgentCommandExecutionContext context,
        CancellationToken cancellationToken);
}

public sealed class AgentCommandInterpreter
{
    private readonly AgentCommandParser _parser;
    private readonly PolicyEvaluationService _evaluations;
    private readonly IReadOnlyDictionary<string, IAgentCommandHandler> _handlers;

    public AgentCommandInterpreter(
        AgentCommandParser parser,
        PolicyEvaluationService evaluations,
        IEnumerable<IAgentCommandHandler> handlers)
    {
        _parser = parser;
        _evaluations = evaluations;
        _handlers = handlers.ToDictionary(handler => handler.Name, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<AgentCommandInterpretation> InterpretAsync(
        ExecutionRecord execution,
        PolicyContext policy,
        string assistantReply,
        CancellationToken cancellationToken)
    {
        AgentCommandBatch batch;
        try
        {
            batch = _parser.Parse(assistantReply);
        }
        catch (AgentCommandParseException ex)
        {
            return new AgentCommandInterpretation(false, [], ex.Message);
        }

        var results = new List<AgentCommandResult>();
        foreach (var command in batch.Commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decision = policy.ValidateAction(new PolicyActionContext(
                _evaluations.Create(execution),
                new PolicyAction(command.Name, command.Body ?? string.Empty)));
            if (!decision.Allowed)
            {
                return new AgentCommandInterpretation(
                    batch.CompletionClaimed,
                    results,
                    decision.Error ?? $"Policy denied {command.Name} action.");
            }

            if (!_handlers.TryGetValue(command.Name, out var handler))
            {
                return new AgentCommandInterpretation(
                    batch.CompletionClaimed,
                    results,
                    $"No agent command handler is registered for {command.Name}.");
            }

            results.Add(await handler.ExecuteAsync(
                new AgentCommandExecutionContext(execution, command),
                cancellationToken));
        }

        return new AgentCommandInterpretation(batch.CompletionClaimed, results, null);
    }
}
