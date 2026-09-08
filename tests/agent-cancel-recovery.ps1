$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('mezhs-cancel-recovery-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null

try {
    $agentProject = Join-Path $root 'src\Mezhs.Agent.Api\Mezhs.Agent.Api.csproj'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$agentProject" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $temp 'Test.csproj') -Encoding UTF8

    @'
using Mezhs.Agent.Models;
using Mezhs.Agent.Services;
using Mezhs.Executor;
using Mezhs.Sqlite;

var root = args[0];
var agentPath = Path.Combine(root, "agent.sqlite");
var executorPath = Path.Combine(root, "executor.sqlite");
var workspace = Path.Combine(root, "work");
Directory.CreateDirectory(workspace);

var agentDatabase = new SqliteDatabase(agentPath);
agentDatabase.Initialize("""
    CREATE TABLE Executions (
        ExecutionId TEXT PRIMARY KEY,
        ParentExecutionId TEXT NULL,
        Kind TEXT NOT NULL,
        ChatId TEXT NULL,
        Status TEXT NOT NULL,
        StartedAt TEXT NULL,
        Error TEXT NULL,
        CompletedAt TEXT NULL
    );
    """);

const string rootId = "exec_pending_cancel";
const string chatId = "chat_pending_cancel";
using (var connection = agentDatabase.Open())
using (var command = connection.CreateCommand())
{
    command.CommandText = """
        INSERT INTO Executions (ExecutionId, ParentExecutionId, Kind, ChatId, Status, StartedAt)
        VALUES ($id, NULL, $kind, $chat, $status, $started);
        """;
    command.Parameters.AddWithValue("$id", rootId);
    command.Parameters.AddWithValue("$kind", AgentExecutionKind.Agent.ToString());
    command.Parameters.AddWithValue("$chat", chatId);
    command.Parameters.AddWithValue("$status", AgentExecutionStatus.CancelRequested.ToString());
    command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O"));
    command.ExecuteNonQuery();
}

var executor = new ExecutorService(executorPath);
var environment = ExecutorEnvironment.SnapshotCurrent()
    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
environment[ExecutorEnvironment.ChatIdVariable] = chatId;
environment[ExecutorEnvironment.ExecutionIdVariable] = rootId;
environment[ExecutorEnvironment.CorrelationIdVariable] = rootId;
environment[ExecutorEnvironment.SourceVariable] = "cancel-recovery-test";
environment[ExecutorEnvironment.WorkspaceVariable] = workspace;
environment[ExecutorEnvironment.TriggerMessageIdVariable] = "message_pending_cancel";
environment[ExecutorEnvironment.CommandIndexVariable] = "0";

var shellId = executor.Execute("ping -n 30 127.0.0.1 >nul", workspace, 60, environment);
try
{
    var runningDeadline = DateTimeOffset.UtcNow.AddSeconds(8);
    while (executor.Get(shellId).Status == ExecutionStatus.Created && DateTimeOffset.UtcNow < runningDeadline)
        Thread.Sleep(100);
    if (executor.Get(shellId).Status != ExecutionStatus.Running)
        throw new InvalidOperationException($"Shell did not reach Running before recovery: {executor.Get(shellId).Status}");

    var recovery = AgentRecoveryState.Prepare(agentPath);
    using (var connection = agentDatabase.Open())
    using (var command = connection.CreateCommand())
    {
        command.CommandText = "SELECT Status FROM Executions WHERE ExecutionId = $id;";
        command.Parameters.AddWithValue("$id", rootId);
        var status = Convert.ToString(command.ExecuteScalar());
        if (!string.Equals(status, AgentExecutionStatus.Cancelled.ToString(), StringComparison.Ordinal))
            throw new InvalidOperationException($"Pending Agent cancellation did not become Cancelled: {status}");
    }

    recovery.ReconcilePendingCancellations(executor);
    var terminal = executor.Wait(shellId, 12);
    if (terminal.Status != ExecutionStatus.Killed)
        throw new InvalidOperationException($"Pending cancellation did not kill detached Executor child: {terminal.Status} - {terminal.Error}");
    if (recovery.TryTake(rootId))
        throw new InvalidOperationException("Cancelled root was incorrectly queued for reasoning recovery.");

    Console.WriteLine("PASS: Pending Agent cancellation is finalized and its detached Executor child is killed during restart recovery.");
}
finally
{
    var current = executor.Get(shellId);
    if (!current.IsTerminal)
    {
        executor.Kill(shellId);
        executor.Wait(shellId, 12);
    }
}
'@ | Set-Content -LiteralPath (Join-Path $temp 'Program.cs') -Encoding UTF8

    dotnet run --project (Join-Path $temp 'Test.csproj') -c Release -- $temp
    if ($LASTEXITCODE -ne 0) { throw "Pending cancellation recovery test failed." }
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
