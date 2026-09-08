namespace Mezhs.Executor;

public sealed class ExecutorService
{
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(8);
    private readonly ExecutorStore _store;

    public ExecutorService(string? storagePath = null) =>
        _store = new ExecutorStore(ExecutorEnvironment.ResolveStoragePath(storagePath));

    public string StoragePath => _store.Path;

    public int Execute(
        string command,
        string? directory = null,
        int timeoutSeconds = 86400,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("Command cannot be empty.", nameof(command));
        if (timeoutSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Timeout must be zero (no timeout) or a positive number of seconds.");

        var resolvedDirectory = Path.GetFullPath(directory ?? Environment.CurrentDirectory);
        if (!Directory.Exists(resolvedDirectory))
            throw new DirectoryNotFoundException($"Execution directory '{resolvedDirectory}' does not exist.");
        var snapshot = environment is null
            ? ExecutorEnvironment.SnapshotCurrent()
            : environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var created = _store.Create(command, resolvedDirectory, timeoutSeconds, snapshot);
        if (!created.Created)
            return created.Execution.Id;

        try
        {
            ExecutorRuntimeLauncher.Launch(created.Execution.Id, _store.Path);
            return created.Execution.Id;
        }
        catch (Exception ex)
        {
            _store.FailCreated(created.Execution.Id, $"Executor runtime could not be started: {ex.Message}");
            throw;
        }
    }

    public Execution Get(int id)
    {
        Reconcile(id);
        return _store.Get(id)?.Execution
            ?? throw new KeyNotFoundException($"Execution '{id}' was not found.");
    }

    public IReadOnlyList<Execution> List(string? chatId = null, int limit = 200)
    {
        if (limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 1000.");
        var normalizedChatId = string.IsNullOrWhiteSpace(chatId) ? null : chatId;
        var initial = _store.List(normalizedChatId, limit);
        foreach (var execution in initial.Where(execution => !execution.IsTerminal))
            Reconcile(execution.Id);
        return _store.List(normalizedChatId, limit);
    }

    public Execution Wait(int id, int timeoutSeconds = 60)
    {
        if (timeoutSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (true)
        {
            var execution = Get(id);
            if (execution.IsTerminal || DateTimeOffset.UtcNow >= deadline)
                return execution;
            Thread.Sleep(200);
        }
    }

    public async Task<Execution> WaitAsync(int id, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var execution = Get(id);
            if (execution.IsTerminal)
                return execution;
            await Task.Delay(200, cancellationToken);
        }
    }

    public Execution Kill(int id)
    {
        var current = Get(id);
        if (current.IsTerminal)
            return current;
        return _store.RequestKill(id)
            ?? throw new KeyNotFoundException($"Execution '{id}' was not found.");
    }

    public int Restart(int id)
    {
        Get(id);
        var plan = _store.PrepareRestart(id);
        if (!plan.CallerLaunchesReplacement)
            return plan.NewExecution.Id;

        try
        {
            ExecutorRuntimeLauncher.Launch(plan.NewExecution.Id, _store.Path);
        }
        catch (Exception ex)
        {
            _store.FailCreated(plan.NewExecution.Id, $"Restart runtime could not be started: {ex.Message}");
            throw;
        }
        return plan.NewExecution.Id;
    }

    public void Run(int id) => new ExecutorRunner(_store).Run(id);

    private void Reconcile(int id) =>
        _store.ReconcileStale(id, DateTimeOffset.UtcNow - StaleThreshold);
}
