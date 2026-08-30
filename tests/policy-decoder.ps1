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
Assert(normal.Settings.Limits.CommandTimeoutSeconds == 2,
    "Typed PolicyContext lost commandTimeoutSeconds=2.");
Assert(normal.Settings.Commands.Allow.SequenceEqual(new[] { "SH" }),
    "Typed PolicyContext lost the configured command allow-list.");
Assert(normal.Snapshot.Contains("requireDone: false", StringComparison.Ordinal),
    "Effective snapshot lost requireDone=false.");
Assert(normal.Snapshot.Contains("maxTurns: 3", StringComparison.Ordinal),
    "Effective snapshot lost maxTurns=3.");
Assert(normal.Snapshot.Contains("commandTimeoutSeconds: 2", StringComparison.Ordinal),
    "Effective snapshot lost commandTimeoutSeconds=2.");
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
var instructionBatch = parser.Parse(normal.ModelInstructions);
Assert(instructionBatch.Commands.Count == 0 && !instructionBatch.CompletionClaimed,
    "Compiled model instructions contain text that the command parser can execute.");
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

    var simpleChild = store.GetExecutions("chat_shell")
        .Single(record => record.Kind == AgentExecutionKind.Shell && record.Request == "echo MEZHS_SHELL_OK");
    Assert(simpleChild.ParentExecutionId == root.ExecutionId,
        "Shell execution did not preserve parent execution causality.");
    Assert(simpleChild.CorrelationId == root.CorrelationId,
        "Shell execution did not preserve the correlation id.");
    Assert(simpleChild.ExitCode == 0 && simpleChild.Result?.Contains("stdout: MEZHS_SHELL_OK", StringComparison.Ordinal) == true,
        "Shell execution result/exit code was not persisted with an informative stdout preview.");

    var multilineText = OperatingSystem.IsWindows()
        ? "echo MULTILINE_A\necho MULTILINE_B\nexit /b 9"
        : "echo MULTILINE_A\necho MULTILINE_B\nexit 9";
    var multiline = await interpreter.InterpretAsync(
        root,
        normal,
        $"<SH\n{multilineText}\nSH>",
        CancellationToken.None);
    Assert(multiline.Error is null && multiline.Results.Count == 1,
        $"Multiline shell command failed at the interpreter layer: {multiline.Error}");
    Assert(!multiline.Results[0].Succeeded && multiline.Results[0].ExitCode == 9,
        "Multiline shell command was truncated or lost its final nonzero exit code.");

    var multilineChild = store.GetExecutions("chat_shell")
        .Single(record => record.Kind == AgentExecutionKind.Shell && record.Request == multilineText);
    Assert(multilineChild.ExitCode == 9,
        "Persisted multiline shell execution lost exit code 9.");
    Assert(multilineChild.Result?.Contains("stdout: MULTILINE_A", StringComparison.Ordinal) == true &&
           multilineChild.Result.Contains("MULTILINE_B", StringComparison.Ordinal),
        $"Multiline shell execution did not preserve all output. Result: {multilineChild.Result}");

    const string unicodeText = "āčēģīķļņšūž ✓";
    var unicode = await interpreter.InterpretAsync(
        root,
        normal,
        $"<SH\necho {unicodeText}\nSH>",
        CancellationToken.None);
    Assert(unicode.Error is null && unicode.Results.Count == 1 && unicode.Results[0].Succeeded,
        $"Unicode shell command failed: {unicode.Error ?? unicode.Results.FirstOrDefault()?.Error}");
    Assert(unicode.Results[0].Output?.Contains(unicodeText, StringComparison.Ordinal) == true,
        $"Unicode shell output was transcoded or corrupted. Result: {unicode.Results[0].Output}");

    var unicodeChild = store.GetExecutions("chat_shell")
        .Single(record => record.Kind == AgentExecutionKind.Shell && record.Request == $"echo {unicodeText}");
    Assert(unicodeChild.Result?.Contains($"stdout: {unicodeText}", StringComparison.Ordinal) == true,
        $"Persisted Unicode shell output was corrupted. Result: {unicodeChild.Result}");

    var timeoutText = OperatingSystem.IsWindows()
        ? "ping -n 6 127.0.0.1 >nul"
        : "sleep 5";
    var started = DateTimeOffset.UtcNow;
    var timedOut = await interpreter.InterpretAsync(
        root,
        normal,
        $"<SH\n{timeoutText}\nSH>",
        CancellationToken.None);
    var elapsed = DateTimeOffset.UtcNow - started;
    Assert(timedOut.Error is null && timedOut.Results.Count == 1 && !timedOut.Results[0].Succeeded,
        "Timed-out command did not return failed command evidence.");
    Assert(timedOut.Results[0].Error?.Contains("timed out after 2 seconds", StringComparison.Ordinal) == true,
        $"Timed-out command returned the wrong error: {timedOut.Results[0].Error}");
    Assert(elapsed < TimeSpan.FromSeconds(5),
        $"Configured command timeout was not enforced promptly. Elapsed: {elapsed}.");

    var timeoutChild = store.GetExecutions("chat_shell")
        .Single(record => record.Kind == AgentExecutionKind.Shell && record.Request == timeoutText);
    Assert(timeoutChild.Status == AgentExecutionStatus.Failed &&
           timeoutChild.Error?.Contains("timed out after 2 seconds", StringComparison.Ordinal) == true,
        "Timed-out shell execution was not persisted as failed timeout evidence.");
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

Console.WriteLine("PASS: typed policies, strict command parsing, causal shell execution, multiline scripts, Unicode output, command timeouts, and informative command evidence are working.");

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
