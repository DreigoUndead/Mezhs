$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('mezhs-policy-test-' + [Guid]::NewGuid().ToString('N'))
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
using Mezhs.Agent.Services;
using Mezhs.Api.Contracts;
using Mezhs.Executor;
using Microsoft.Data.Sqlite;

var options = AgentConfigLoader.Load(args[0]);
var normal = options.Policies["test"];
var done = options.Policies["test-done"];
var evidencePolicy = options.Policies["test-evidence"];
var timeoutPolicy = options.Policies["test-timeout"];

Assert(options.Runtime.QueueCapacity == 1 && options.Runtime.MaxConcurrentExecutions == 1,
    "Typed runtime capacity settings were not loaded.");
Assert(Directory.Exists(options.Workspace), "Configured Agent workspace was not resolved to an existing directory.");
Assert(normal.Settings.Commands.Allow.SequenceEqual(new[] { "SH" }), "Command allow-list was not compiled.");
Assert(normal.Settings.Environment.Allow.SequenceEqual(new[] { "TEST_AGENT_VALUE" }), "Environment allow-list was not compiled.");
Assert(!normal.Settings.Completion.RequireDone, "requireDone=false was not compiled.");
Assert(evidencePolicy.Settings.Completion.RequiredSuccessfulCommands.SequenceEqual(new[] { "SH" }),
    "Required successful command evidence was not compiled.");
Assert(evidencePolicy.ModelInstructions.Contains("successful execution evidence", StringComparison.OrdinalIgnoreCase),
    "Evidence requirement was not exposed to the model instructions.");
Assert(done.ModelInstructions.Contains("<DONE>", StringComparison.Ordinal), "DONE requirement was not exposed to model instructions.");
Assert(normal.Snapshot.Contains("TEST_AGENT_VALUE", StringComparison.Ordinal), "Policy snapshot lost environment rules.");

var now = DateTimeOffset.UtcNow;
var rootEvidence = new ExecutionEvidence(
    "root", null, "root", AgentExecutionKind.Agent, null, AgentExecutionStatus.Running,
    "task", null, null, null, now, now, null);
var successfulShell = new ExecutionEvidence(
    "shell-ok", "root", "root", AgentExecutionKind.Shell, "SH", AgentExecutionStatus.Completed,
    "echo ok", "stdout: ok", null, 0, now, now, now);
var failedShell = successfulShell with
{
    ExecutionId = "shell-failed",
    Status = AgentExecutionStatus.Failed,
    Result = null,
    Error = "failed",
    ExitCode = 1
};

var incomplete = done.EvaluateCompletion(new PolicyCompletionContext(
    new PolicyEvaluationContext(rootEvidence, new[] { rootEvidence }), false));
Assert(incomplete.State == PolicyCompletionState.Incomplete, "Missing DONE was not treated as incomplete.");
var doneAccepted = done.EvaluateCompletion(new PolicyCompletionContext(
    new PolicyEvaluationContext(rootEvidence, new[] { rootEvidence }), true));
Assert(doneAccepted.State == PolicyCompletionState.Accepted, "Valid DONE claim was rejected without evidence requirements.");
var evidenceMissing = evidencePolicy.EvaluateCompletion(new PolicyCompletionContext(
    new PolicyEvaluationContext(rootEvidence, new[] { rootEvidence }), true));
Assert(evidenceMissing.State == PolicyCompletionState.Rejected && evidenceMissing.Error?.Contains("<SH>", StringComparison.Ordinal) == true,
    "DONE without required shell evidence was not rejected.");
var evidenceFailed = evidencePolicy.EvaluateCompletion(new PolicyCompletionContext(
    new PolicyEvaluationContext(rootEvidence, new[] { rootEvidence, failedShell }), true));
Assert(evidenceFailed.State == PolicyCompletionState.Rejected, "Failed shell evidence incorrectly satisfied completion.");
var evidenceAccepted = evidencePolicy.EvaluateCompletion(new PolicyCompletionContext(
    new PolicyEvaluationContext(rootEvidence, new[] { rootEvidence, successfulShell }), true));
Assert(evidenceAccepted.State == PolicyCompletionState.Accepted, "Successful shell evidence did not satisfy completion.");

var shellDefinition = Registry.Get(CommandBehavior.Shell);
var allowedAction = normal.ValidateAction(new PolicyActionContext(
    new PolicyEvaluationContext(rootEvidence, new[] { rootEvidence }),
    new PolicyAction(shellDefinition, "echo hello")));
Assert(allowedAction.Allowed, "Structured SH action was rejected by an allowing policy.");
var deniedAction = done.ValidateAction(new PolicyActionContext(
    new PolicyEvaluationContext(rootEvidence, new[] { rootEvidence }),
    new PolicyAction(shellDefinition, "echo hello")));
Assert(!deniedAction.Allowed, "Unconfigured SH action was not denied by default.");

var parser = new Parser();
Assert(parser.Parse(normal.ModelInstructions).Commands.Count == 0,
    "Compiled model instructions accidentally contain executable command tags.");
var parsed = parser.Parse("before\n<SH>\necho one\necho two\n</SH>\n<DONE>\nafter");
Assert(parsed.Commands.Count == 2, "Parser did not retain SH and DONE commands.");
Assert(parsed.Commands[0].Name == "SH" && parsed.Commands[0].Body == "echo one\necho two",
    "Shell body was rewritten while parsing.");
Assert(parsed.Commands[1].Name == "DONE" && parsed.Commands[1].Body is null,
    "DONE marker was not parsed as a marker command.");
Assert(parsed.VisibleContent == "before\nafter",
    $"Protocol parser did not remove executable protocol from display content: '{parsed.VisibleContent}'.");

var mappedMessage = AgentApiMapper.ToView(
    new ApiChatHistoryMessage(
        "msg-view", "chat-view", normal.ConnectionId, "assistant", "agent",
        "visible before\n<SH>\necho mapped\n</SH>\n<DONE>\nvisible after",
        [], null, null, null, MessageStatus.Completed, null, now, now, now),
    parser);
Assert(mappedMessage.DisplayContent == "visible before\nvisible after",
    "Agent API did not own protocol stripping for Web display.");
Assert(mappedMessage.Commands.Count == 1 &&
       mappedMessage.Commands[0].Name == "SH" &&
       mappedMessage.Commands[0].Body == "echo mapped" &&
       mappedMessage.Commands[0].CommandIndex == 0 &&
       mappedMessage.CompletionClaimed,
    "Agent API protocol view lost SH body/index or DONE claim.");

var promptBuilder = new AgentPromptBuilder(options);
Assert(!promptBuilder.BuildContinue(normal).Content.Contains("<DONE>", StringComparison.Ordinal),
    "requireDone=false continuation still instructs DONE.");
Assert(promptBuilder.BuildContinue(done).Content.Contains("<DONE>", StringComparison.Ordinal),
    "requireDone=true continuation lost DONE guidance.");

var shellOptions = new AgentOptions
{
    Listen = options.Listen,
    MezhsApi = options.MezhsApi,
    Storage = Path.Combine(Path.GetTempPath(), $"mezhs-agent-policy-{Guid.NewGuid():N}.sqlite"),
    Workspace = options.Workspace,
    Runtime = options.Runtime,
    Messages = options.Messages,
    Policies = options.Policies
};
var executorPath = Path.Combine(Path.GetTempPath(), $"mezhs-executor-policy-{Guid.NewGuid():N}.sqlite");

try
{
    var store = new AgentStore(shellOptions);
    store.Initialize();
    var executor = new ExecutorService(executorPath);
    var evaluations = new PolicyEvaluationService(store, executor);
    var interpreter = new Interpreter(parser, evaluations, new Shell(executor, shellOptions));
    var emptyEnvironment = new Dictionary<string, string>();

    var serialA1 = store.TryCreateRootExecution(
        normal.Id, normal.ConnectionId, "chat_serial_a", "manual", null,
        "serial a1", emptyEnvironment, normal.Snapshot, 100)
        ?? throw new InvalidOperationException("Could not admit serial execution A1.");
    Thread.Sleep(5);
    var serialA2 = store.TryCreateRootExecution(
        normal.Id, normal.ConnectionId, "chat_serial_a", "manual", null,
        "serial a2", emptyEnvironment, normal.Snapshot, 100)
        ?? throw new InvalidOperationException("Could not admit serial execution A2.");
    Thread.Sleep(5);
    var parallelB = store.TryCreateRootExecution(
        normal.Id, normal.ConnectionId, "chat_parallel_b", "manual", null,
        "parallel b", emptyEnvironment, normal.Snapshot, 100)
        ?? throw new InvalidOperationException("Could not admit parallel execution B.");

    var firstClaim = store.TryClaimNextQueuedExecution();
    Assert(firstClaim?.ExecutionId == serialA1.ExecutionId,
        "Durable queue did not claim the oldest eligible execution first.");
    var secondClaim = store.TryClaimNextQueuedExecution();
    Assert(secondClaim?.ExecutionId == parallelB.ExecutionId,
        "Same-chat work became Running instead of leaving capacity for another chat.");
    Assert(store.GetExecution(serialA2.ExecutionId)?.Status == AgentExecutionStatus.Queued,
        "Second execution for one chat did not remain durably queued while the first was active.");
    Assert(store.Complete(firstClaim!.ExecutionId, "done") && store.Complete(secondClaim!.ExecutionId, "done"),
        "Claimed queue test executions did not complete.");
    var thirdClaim = store.TryClaimNextQueuedExecution();
    Assert(thirdClaim?.ExecutionId == serialA2.ExecutionId,
        "Queued same-chat work was not claimable after its predecessor completed.");
    Assert(store.Complete(thirdClaim!.ExecutionId, "done"), "Final queue test execution did not complete.");

    var root = store.TryCreateRootExecution(
        normal.Id, normal.ConnectionId, "chat_shell", "manual", null,
        "shell test", emptyEnvironment, normal.Snapshot, 100)
        ?? throw new InvalidOperationException("Shell test root execution was not admitted.");
    root = store.TryClaimNextQueuedExecution()
        ?? throw new InvalidOperationException("Shell test root execution did not start.");

    var simple = await interpreter.InterpretAsync(
        root, normal, "msg-simple", "<SH>\necho MEZHS_SHELL_OK\n</SH>", CancellationToken.None);
    Assert(simple.Error is null && simple.Results.Count == 1 && simple.Results[0].Succeeded,
        $"Simple shell command failed: {simple.Error ?? simple.Results.FirstOrDefault()?.Error}");
    var simpleChild = executor.List("chat_shell").Single(record =>
        record.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == simple.Results[0].ExecutionId);
    Assert(simpleChild.TriggerMessageId == "msg-simple" && simpleChild.CommandIndex == 0,
        "Executor shell execution did not persist message/index identity.");
    Assert(simpleChild.Command == "echo MEZHS_SHELL_OK", "Shell command text was rewritten before persistence/execution.");
    Assert(simpleChild.ParentExecutionId == root.ExecutionId && simpleChild.CorrelationId == root.CorrelationId,
        "Executor shell execution lost Agent causal identity.");

    var duplicate = await interpreter.InterpretAsync(
        root, normal, "msg-duplicate",
        "<SH>\necho DUPLICATE_OK\n</SH>\n<SH>\necho DUPLICATE_OK\n</SH>",
        CancellationToken.None);
    Assert(duplicate.Error is null && duplicate.Results.Count == 2 && duplicate.Results.All(result => result.Succeeded),
        "Duplicate shell commands did not both execute.");
    var duplicateChildren = executor.List("chat_shell")
        .Where(record => record.TriggerMessageId == "msg-duplicate")
        .OrderBy(record => record.CommandIndex)
        .ToArray();
    Assert(duplicateChildren.Length == 2 && duplicateChildren[0].CommandIndex == 0 && duplicateChildren[1].CommandIndex == 1,
        "Identical commands are not durably distinguishable by command index.");
    Assert(duplicateChildren.All(record => record.Command == "echo DUPLICATE_OK"),
        "Duplicate command identity depends on rewriting command text.");

    var multilineText = OperatingSystem.IsWindows()
        ? "echo MULTILINE_A\necho MULTILINE_B\nexit /b 9"
        : "echo MULTILINE_A\necho MULTILINE_B\nexit 9";
    var multiline = await interpreter.InterpretAsync(
        root, normal, "msg-multiline", $"<SH>\n{multilineText}\n</SH>", CancellationToken.None);
    Assert(multiline.Error is null && multiline.Results.Count == 1 && !multiline.Results[0].Succeeded && multiline.Results[0].ExitCode == 9,
        "Multiline shell command lost its final nonzero exit code.");
    Assert(multiline.Results[0].Output?.Contains("MULTILINE_A", StringComparison.Ordinal) == true &&
           multiline.Results[0].Output?.Contains("MULTILINE_B", StringComparison.Ordinal) == true,
        "Multiline shell output was truncated.");

    const string unicodeText = "āčēģīķļņšūž ✓";
    var unicode = await interpreter.InterpretAsync(
        root, normal, "msg-unicode", $"<SH>\necho {unicodeText}\n</SH>", CancellationToken.None);
    Assert(unicode.Error is null && unicode.Results.Single().Succeeded &&
           unicode.Results.Single().Output?.Contains(unicodeText, StringComparison.Ordinal) == true,
        $"Unicode shell output was corrupted: {unicode.Results.FirstOrDefault()?.Output}");

    var timeoutRoot = store.TryCreateRootExecution(
        timeoutPolicy.Id, timeoutPolicy.ConnectionId, "chat_timeout", "manual", null,
        "timeout test", emptyEnvironment, timeoutPolicy.Snapshot, 100)
        ?? throw new InvalidOperationException("Timeout root execution was not admitted.");
    timeoutRoot = store.TryClaimNextQueuedExecution()
        ?? throw new InvalidOperationException("Timeout root execution did not start.");
    var timeoutText = OperatingSystem.IsWindows()
        ? "echo BEFORE_TIMEOUT & ping -n 6 127.0.0.1 >nul"
        : "echo BEFORE_TIMEOUT; sleep 5";
    var started = DateTimeOffset.UtcNow;
    var timedOut = await interpreter.InterpretAsync(
        timeoutRoot, timeoutPolicy, "msg-timeout", $"<SH>\n{timeoutText}\n</SH>", CancellationToken.None);
    var elapsed = DateTimeOffset.UtcNow - started;
    Assert(timedOut.Error is null && timedOut.Results.Count == 1 && !timedOut.Results[0].Succeeded,
        "Timed-out command did not return failed command evidence.");
    Assert(timedOut.Results[0].Error?.Contains("timed out after 1 seconds", StringComparison.Ordinal) == true,
        $"Timed-out command returned the wrong error: {timedOut.Results[0].Error}");
    Assert(elapsed < TimeSpan.FromSeconds(4), $"Configured timeout was not enforced promptly: {elapsed}.");
    var timeoutChild = executor.List("chat_timeout").Single();
    Assert(timeoutChild.Status == ExecutionStatus.TimedOut, "Timed-out shell was not persisted by Executor as TimedOut.");
    Assert(timeoutChild.Result?.Contains("BEFORE_TIMEOUT", StringComparison.Ordinal) == true,
        "Partial shell output was returned transiently but not persisted with timeout evidence.");
}
finally
{
    SqliteConnection.ClearAllPools();
    DeleteDatabase(shellOptions.Storage);
    DeleteDatabase(executorPath);
}

Console.WriteLine("PASS: typed policy rules, structured actions, immutable completion evidence, durable Agent queue serialization, Executor command identity, shell fidelity, Unicode, and timeout behavior are correct.");

static void DeleteDatabase(string path)
{
    File.Delete(path);
    File.Delete(path + "-shm");
    File.Delete(path + "-wal");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
'@ | Set-Content -LiteralPath (Join-Path $temp 'Program.cs') -Encoding UTF8

    dotnet run --project (Join-Path $temp 'Test.csproj') -c Release -- $configPath
    if ($LASTEXITCODE -ne 0) { throw "Policy/shell behavior test failed with exit code $LASTEXITCODE." }
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
