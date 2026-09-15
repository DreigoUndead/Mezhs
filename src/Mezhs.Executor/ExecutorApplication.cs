using Mezhs.Console;

namespace Mezhs.Executor;

public sealed class ExecutorApplication : ConsoleApplication
{
    private readonly ExecutorService _executor = new();

    [Command(Description = "Start a durable host shell execution and return its integer ID.")]
    public int Execute(string command, string? directory = null, int timeoutSeconds = 86400) =>
        _executor.Execute(command, directory, timeoutSeconds);

    [Command(Description = "Get one durable execution record.")]
    public Execution Get(int id) => _executor.Get(id);

    [Command(Description = "List durable executions, newest first.")]
    public IReadOnlyList<Execution> List(string? chatId = null, int limit = 200) =>
        _executor.List(chatId, limit);

    [Command(Description = "Wait for terminal state or until this caller's wait timeout expires.")]
    public Execution Wait(int id, int timeoutSeconds = 60) => _executor.Wait(id, timeoutSeconds);

    [Command(Description = "Request termination of an execution.")]
    public Execution Kill(int id) => _executor.Kill(id);

    [Command(Description = "Restart an execution as a new durable execution and return the new ID.")]
    public int Restart(int id) => _executor.Restart(id);

    [Command(Description = "Internal execution-owner entry point. Claims and runs an existing execution ID.")]
    public void Run(int id, string? storage = null) => new ExecutorService(storage).Run(id);

    public override string Test()
    {
        var storage = Path.Combine(
            Path.GetTempPath(),
            $"mezhs-executor-test-{Guid.NewGuid():N}.sqlite");
        try
        {
            var executor = new ExecutorService(storage);
            var id = executor.Execute(
                "echo mezhs-executor-self-test",
                Environment.CurrentDirectory,
                timeoutSeconds: 10);
            var execution = executor.Wait(id, timeoutSeconds: 15);
            if (execution.Status != ExecutionStatus.Completed)
                throw new InvalidOperationException(
                    $"Executor self-test ended as {execution.Status}: {execution.Error}");
            if (execution.ExitCode != 0 ||
                execution.Result?.Contains("mezhs-executor-self-test", StringComparison.Ordinal) != true)
            {
                throw new InvalidOperationException(
                    $"Executor self-test returned unexpected output: {execution.Result}");
            }
            return "PASS: Executor durable shell execution";
        }
        finally
        {
            DeleteIfExists(storage);
            DeleteIfExists(storage + "-wal");
            DeleteIfExists(storage + "-shm");
        }
    }

    private static void DeleteIfExists(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
