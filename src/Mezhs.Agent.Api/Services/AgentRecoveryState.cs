using Mezhs.Agent.Persistence;
using Mezhs.Executor;

namespace Mezhs.Agent.Services;

public sealed class AgentRecoveryState(
    AgentStore store,
    ExecutorService executor)
{
    private readonly HashSet<string> _executionIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private bool _prepared;

    public void Prepare()
    {
        var recovery = store.RecoverAfterRestart();

        foreach (var pending in recovery.PendingCancellations)
        {
            if (string.IsNullOrWhiteSpace(pending.ChatId))
                continue;

            foreach (var shell in executor.List(pending.ChatId, 1000)
                         .Where(shell => !shell.IsTerminal && string.Equals(
                             shell.ParentExecutionId,
                             pending.ExecutionId,
                             StringComparison.OrdinalIgnoreCase)))
            {
                executor.Kill(shell.Id);
            }
        }

        lock (_sync)
        {
            if (_prepared)
                throw new InvalidOperationException("Agent recovery has already been prepared.");

            _executionIds.UnionWith(recovery.ExecutionIds);
            _prepared = true;
        }
    }

    public bool TryTake(string executionId)
    {
        lock (_sync)
            return _executionIds.Remove(executionId);
    }
}
