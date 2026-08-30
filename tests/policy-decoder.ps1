$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('mezhs-policy-decoder-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null

try {
    $agentProject = Join-Path $root 'src\Mezhs.Agent.Api\Mezhs.Agent.Api.csproj'
    $configPath = Join-Path $PSScriptRoot 'mezhs.agent.test.yaml'
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
using Mezhs.Agent.Commands;
using Mezhs.Agent.Configuration;
using Mezhs.Agent.Models;
using Mezhs.Agent.Persistence;
using Mezhs.Agent.Policy;
using Microsoft.Data.Sqlite;

var options = AgentConfigLoader.Load(args[0]);
var normal = options.Policies["test"];
var done = options.Policies["test-done"];

Assert(normal.Settings.ConnectionId == "test",
    "Typed PolicyContext lost the mapped connectionId.");
Assert(!normal.Settings.Completion.RequireDone,
    "Typed PolicyContext lost requireDone=false.");
Assert(normal.Settings.Limits.MaxTurns == 3,
    "Typed PolicyContext lost maxTurns=3.");
Assert(normal.Settings.Commands.Allow.SequenceEqual(new[] { "SH" }),
    "Typed PolicyContext lost the configured command allow-list.");
Assert(normal.Snapshot.Contains("requireDone: false", StringComparison.Ordinal),
    "Effective snapshot lost requireDone=false.");
Assert(normal.Snapshot.Contains("maxTurns: 3", StringComparison.Ordinal),
    "Effective snapshot lost maxTurns=3.");
Assert(normal.Snapshot.Contains("SH", StringComparison.Ordinal),
    "Effective snapshot lost command permissions.");
Assert(normal.ModelInstructions.Contains("<SH", StringComparison.Ordinal),
    "Allowed shell command did not compile into model instructions.");
Assert(done.ModelInstructions.Contains("<DONE>", StringComparison.Ordinal),
    "Completion rule did not compile the command protocol into model instructions.");

var execution = new ExecutionRecord
{
    ExecutionId = "exec_test",
    CorrelationId = "exec_test",
    Kind = AgentExecutionKind.Agent,
    ChatId = "chat_test",
    PolicyId = "test-done",
    ConnectionId = "test",
    Source = "manual",
    Status = AgentExecutionStatus.Running,
    Request = "test",
    PolicySnapshot = done.Snapshot
};
var evidence = new[] { execution };
var context = new PolicyEvaluationContext(execution, evidence);

var firstTurn = done.ValidateTurn(new PolicyTurnContext(context, 0));
Assert(firstTurn.Allowed, "First turn was incorrectly denied.");
var secondTurn = done.ValidateTurn(new PolicyTurnContext(context, 1));
Assert(!secondTurn.Allowed && secondTurn.Error?.Contains("limit of 1 turns", StringComparison.Ordinal) == true,
    "Turn limit did not compile into runtime validation.");

var incomplete = done.EvaluateCompletion(new PolicyCompletionContext(context, false));
Assert(incomplete.State == PolicyCompletionState.Incomplete,
    "Missing <DONE> command was not treated as incomplete.");
var accepted = done.EvaluateCompletion(new PolicyCompletionContext(context, true));
Assert(accepted.State == PolicyCompletionState.Accepted,
    "Parsed <DONE> completion claim was not accepted.");

var allowedAction = normal.ValidateAction(new PolicyActionContext(
    context,
    new PolicyAction("SH", "echo hello")));
Assert(allowedAction.Allowed,
    "Explicitly allowed SH action was rejected.");
var deniedAction = done.ValidateAction(new PolicyActionContext(
    context,
    new PolicyAction("SH", "echo hello")));
Assert(!deniedAction.Allowed && deniedAction.Error?.Contains("does not explicitly allow", StringComparison.Ordinal) == true,
    "Unconfigured executable action was not denied by default.");

var parser = new AgentCommandParser();
var batch = parser.Parse("before\n<SH\necho one\necho two\nSH>\n<DONE>\nafter");
Assert(batch.CompletionClaimed, "<DONE> was not parsed as the completion marker.");
Assert(batch.Commands.Count == 1 && batch.Commands[0].Name == "SH",
    "SH block was not parsed as one command.");
Assert(batch.Commands[0].Body == "echo one\necho two",
    "SH body was rewritten while parsing.");

var nestedRejected = false;
try
{
    parser.Parse("<SH\necho one\n<SH\necho two\nSH>\nSH>");
}
catch (AgentCommandParseException)
{
    nestedRejected = true;
}
Assert(nestedRejected, "Nested agent command blocks were not rejected.");

var shellOptions = new AgentOptions
{
    Listen = options.Listen,
    MezhsApi = options.MezhsApi,
    Storage = Path.Combine(Path.GetTempPath(), $"mezhs-shell-test-{Guid.NewGuid():N}.sqlite"),
    Policies = options.Policies
};
try
{
    var store = new AgentStore(shellOptions);
    store.Initialize();
    var root = store.CreateRootExecution(
        normal.Id,
        normal.ConnectionId,
        "chat_shell",
        "manual",
        null,
        "shell test",
        normal.Snapshot);
    Assert(store.TryMarkRunning(root.ExecutionId), "Shell test root execution did not start.");

    var interpreter = new AgentCommandInterpreter(
        parser,
        new PolicyEvaluationService(store),
        new IAgentCommandHandler[] { new ShellCommandHandler(store) });
    var interpreted = await interpreter.InterpretAsync(
        root,
        normal,
        "<SH\necho MEZHS_SHELL_OK\nSH>\n<DONE>",
        CancellationToken.None);

    Assert(interpreted.Error is null, $"Shell command interpreter failed: {interpreted.Error}");
    Assert(interpreted.CompletionClaimed, "Interpreter lost the <DONE> claim.");
    Assert(interpreted.Results.Count == 1 && interpreted.Results[0].Succeeded,
        "Shell command did not execute successfully.");

    var child = store.GetExecutions("chat_shell")
        .Single(record => record.Kind == AgentExecutionKind.Shell);
    Assert(child.ParentExecutionId == root.ExecutionId,
        "Shell execution did not preserve parent execution causality.");
    Assert(child.CorrelationId == root.CorrelationId,
        "Shell execution did not preserve the correlation id.");
    Assert(child.ExitCode == 0 && child.Result?.Contains("MEZHS_SHELL_OK", StringComparison.Ordinal) == true,
        "Shell execution result/exit code was not persisted.");
}
finally
{
    // Microsoft.Data.Sqlite pools disposed connections. Clear the pool before removing
    // the temporary database so this deterministic test also works on Windows.
    SqliteConnection.ClearAllPools();
    File.Delete(shellOptions.Storage);
    File.Delete(shellOptions.Storage + "-shm");
    File.Delete(shellOptions.Storage + "-wal");
}

Console.WriteLine("PASS: typed policies, strict command parsing, <DONE>, fail-closed action authorization, and causal shell execution are working.");

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
'@ | Set-Content -LiteralPath (Join-Path $temp 'Program.cs') -Encoding UTF8

    dotnet run --project (Join-Path $temp 'Test.csproj') -c Release -- $configPath
    if ($LASTEXITCODE -ne 0) { throw "Policy/command test failed with exit code $LASTEXITCODE." }
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}