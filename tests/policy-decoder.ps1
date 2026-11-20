$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('mezhs-policy-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null

try {
    $agentProject = Join-Path $root 'src\Mezhs.Agent.Api\Mezhs.Agent.Api.csproj'
    # Standalone publishing must carry all three samples: implicit startup copies
    # instructions.yaml before the Agent config loader can read any policies.
    $publishedAgent = Join-Path $temp 'standalone agent publish'
    dotnet publish $agentProject -c Release --no-build --nologo -o $publishedAgent
    if ($LASTEXITCODE -ne 0) { throw "Agent publish probe failed." }
    foreach ($sample in @('agent.yaml', 'mezhs.yaml', 'instructions.yaml')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishedAgent $sample))) {
            throw "Standalone Agent publish is missing required sample $sample."
        }
    }

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
using Mezhs.Configuration;
using Mezhs.Agent.Models;
using Mezhs.Agent.Persistence;
using Mezhs.Agent.Policy;
using Mezhs.Agent.Services;
using Mezhs.Api.Contracts;
using Mezhs.Executor;
using Mezhs.Common.RichUi;
using Microsoft.Data.Sqlite;

var core = MezhsConfigLoader.Load(args[1]);
var options = AgentConfigLoader.Load(args[0], core.Connections.Select(connection => connection.Id).ToArray());
Assert(core.Connections.Single(connection => connection.Id == "test").DefaultModel == "mock-fast",
    "Core connection default model was not loaded from independent Core YAML.");
try
{
    AgentConfigLoader.Load(args[0], ["unknown"]);
    throw new InvalidOperationException("Invalid policy connection was accepted.");
}
catch (InvalidOperationException ex) when (ex.Message.Contains("references unknown connection", StringComparison.Ordinal))
{
}
var legacyConfig = Path.Combine(Path.GetTempPath(), $"mezhs-legacy-agent-{Guid.NewGuid():N}.yaml");
try
{
    File.WriteAllText(legacyConfig, File.ReadAllText(args[0]) + "\nconnections: []\n");
    try
    {
        AgentConfigLoader.Load(legacyConfig);
        throw new InvalidOperationException("Legacy Agent connection definitions were silently accepted.");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("move Core configuration to mezhs.yaml", StringComparison.Ordinal))
    {
    }
}
finally { File.Delete(legacyConfig); }


var repositoryRoot = Directory.GetParent(Path.GetDirectoryName(args[0])!)!.FullName;
var defaultAgentOptions = AgentConfigLoader.Load(Path.Combine(repositoryRoot, "agent.yaml"));
var os = OperatingSystem.IsWindows() ? "Windows" :
    OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "macOS" : "Unknown OS";
foreach (var name in new[] { "low", "mid", "high" })
{
    var policy = defaultAgentOptions.Policies[name];
    Assert(policy.Settings.Instructions.Contains("You are a MEŽS agent operating", StringComparison.Ordinal),
        $"Default {name} policy did not expand its shared instructions.");
    Assert(!policy.ModelInstructions.Contains("{mezhs_default}", StringComparison.Ordinal),
        $"Default {name} policy retained an unresolved shared instruction reference.");
    Assert(policy.Snapshot.Contains("MEŽS runtime on {OS}", StringComparison.Ordinal),
        $"Default {name} snapshot did not retain compiled instructions before OS substitution.");
    Assert(policy.Settings.Commands.Allow.SequenceEqual(new[] { "SH", "ATTACH" }),
        $"Default {name} command permissions changed.");
    // Policy instructions are not Agent replies, so their illustrative command
    // tags never pass through the Agent Interpreter or Executor.
    Assert(policy.ModelInstructions.Contains("<SH>shell text</SH>", StringComparison.Ordinal),
        $"Default {name} instructions lost inline matched shell syntax.");
}
var exampleExecution = new ExecutionRecord
{
    ExecutionId = "exec-config-probe", CorrelationId = "exec-config-probe",
    Kind = AgentExecutionKind.Agent, PolicyId = "low", ConnectionId = "chatgpt-sub",
    Source = "manual", Status = AgentExecutionStatus.Running, Request = "Test instructions",
    PolicySnapshot = defaultAgentOptions.Policies["low"].Snapshot
};
var initialPrompt = new AgentPromptBuilder(defaultAgentOptions)
    .BuildInitial(exampleExecution, defaultAgentOptions.Policies["low"], true).Content;
Assert(initialPrompt.Contains($"MEŽS runtime on {os}", StringComparison.Ordinal),
    "Runtime startup did not resolve the host OS in shared instructions.");
Assert(!initialPrompt.Contains("{OS}", StringComparison.Ordinal),
    "Runtime startup left an OS placeholder unresolved.");
Assert(initialPrompt.Replace("\r\n", "\n", StringComparison.Ordinal)
       .Contains("Task:\nTest instructions", StringComparison.Ordinal),
    "Runtime startup lost the task content.");
Assert(initialPrompt.Contains("Host shell:", StringComparison.Ordinal),
    "Runtime startup omitted the configured shell context.");
Assert(initialPrompt.Contains("Closing tags may appear", StringComparison.Ordinal),
    "Default instructions lost position-independent tag guidance.");

var configScratch = Path.Combine(Path.GetTempPath(), $"mezhs config copies {Guid.NewGuid():N}");
Directory.CreateDirectory(configScratch);
var savedDirectory = Environment.CurrentDirectory;
try
{
    var defaultCore = Path.Combine(configScratch, "mezhs.yaml");
    var localCore = Path.Combine(configScratch, "mezhs.local.yaml");
    var defaultAgent = Path.Combine(configScratch, "agent.yaml");
    var localAgent = Path.Combine(configScratch, "agent.local.yaml");
    var defaultTexts = Path.Combine(configScratch, "instructions.yaml");
    var localTexts = Path.Combine(configScratch, "instructions.local.yaml");
    File.WriteAllText(defaultCore, "repo default");
    File.WriteAllText(defaultAgent, File.ReadAllText(args[0]));
    File.WriteAllText(defaultTexts, "texts:\n  mezhs_default: Default text\n");

    Environment.CurrentDirectory = configScratch;
    Assert(ConfigPath.FindLocal([], "mezhs.yaml") == localCore &&
           File.ReadAllText(localCore) == "repo default",
        "Default Core config was not copied alongside its tracked sample.");
    Assert(ConfigPath.FindLocal([], "agent.yaml", companionFileName: "instructions.yaml") == localAgent,
        "Default Agent config was not copied.");
    Assert(File.Exists(localTexts) &&
           File.ReadAllText(localTexts).Contains("Default text", StringComparison.Ordinal),
        "Default startup did not create an instructions copy next to the Agent config.");
    File.WriteAllText(localCore, "my personalized config");
    File.WriteAllText(localTexts, "texts:\n  mezhs_default: |\n    Personal instructions on {OS}.\n    Use <SH>code</SH>.\n    Use <ATTACH>file</ATTACH>.\n");
    File.WriteAllText(defaultCore, "upgraded default");
    Assert(ConfigPath.FindLocal([], "mezhs.yaml") == localCore &&
           File.ReadAllText(localCore) == "my personalized config",
        "Subsequent startup overwrote local settings.");
    var explicitPath = Path.Combine(configScratch, "custom config.yaml");
    File.WriteAllText(explicitPath, "custom");
    Assert(ConfigPath.FindLocal(["--config", explicitPath], "agent.yaml") == explicitPath &&
           ConfigPath.FindLocal(["--mezhs-config", explicitPath], "mezhs.yaml", "--mezhs-config") == explicitPath,
        "Explicit configuration paths were redirected to a local sample.");

    var customDirectory = Path.Combine(configScratch, "separate config");
    Directory.CreateDirectory(customDirectory);
    var explicitLocalAgent = Path.Combine(customDirectory, "agent.local.yaml");
    var explicitInstructions = Path.Combine(customDirectory, "instructions.local.yaml");
    File.WriteAllText(explicitLocalAgent, File.ReadAllText(args[0]));
    Assert(ConfigPath.FindLocal(["--config", explicitLocalAgent], "agent.yaml",
               companionFileName: "instructions.yaml") == explicitLocalAgent &&
           !File.Exists(explicitInstructions),
        "Explicit agent.local.yaml incorrectly created an instructions sidecar.");
    var noSidecar = AgentConfigLoader.Load(explicitLocalAgent, ["test", "test-alt"]);
    Assert(noSidecar.Policies["low"].Settings.Instructions == "Complete the assigned deterministic test task.",
        "Explicit legacy policy should load without an instructions sidecar.");

    File.WriteAllText(explicitInstructions, "texts:\n  mezhs_default: Custom sidecar text\n");
    var referencedExternalAgent = File.ReadAllText(args[0]).Replace(
        "instructions: Complete the assigned deterministic test task.",
        "instructions: \"{mezhs_default}\"", StringComparison.Ordinal);
    File.WriteAllText(explicitLocalAgent, referencedExternalAgent);
    var explicitWithSidecar = AgentConfigLoader.Load(explicitLocalAgent, ["test", "test-alt"]);
    Assert(explicitWithSidecar.Policies["low"].Settings.Instructions == "Custom sidecar text",
        "Explicit agent.local.yaml failed to use its manually supplied instructions sidecar.");

    File.WriteAllText(explicitLocalAgent, File.ReadAllText(args[0]).Replace(
        "instructions: Complete the assigned deterministic test task.",
        "instructions: \"Print {shell} literally.\"", StringComparison.Ordinal));
    var literalPolicy = AgentConfigLoader.Load(explicitLocalAgent, ["test", "test-alt"]);
    Assert(literalPolicy.Policies["low"].Settings.Instructions == "Print {shell} literally.",
        "Literal braces in a non-reference policy instruction were incorrectly expanded.");

    var referencedAgent = File.ReadAllText(defaultAgent).Replace(
        "instructions: Complete the assigned deterministic test task.",
        "instructions: \"{mezhs_default}\"", StringComparison.Ordinal);
    File.WriteAllText(localAgent, referencedAgent);
    File.WriteAllText(defaultAgent, referencedAgent);
    var localPolicies = AgentConfigLoader.Load(localAgent, ["test", "test-alt"]);
    var plainPolicies = AgentConfigLoader.Load(defaultAgent, ["test", "test-alt"]);
    Assert(localPolicies.Policies["low"].Settings.Instructions.Contains("Personal instructions", StringComparison.Ordinal) &&
           plainPolicies.Policies["low"].Settings.Instructions == "Default text",
        "Local and default instruction sidecars were not selected independently.");

    // Simulate upgrade with a contradictory legacy local sidecar. Preserve
    // user-authored text on disk, but append an authoritative runtime protocol.
    var legacyText = "texts:\n  mezhs_default: |\n    Older personalized text.\n    Escape examples never execute.\n    Example: " +
        string.Concat((char)60, "SH", (char)62, "echo EXAMPLE", (char)60, "/SH", (char)62) + "\n";
    File.WriteAllText(localTexts, legacyText);
    var upgraded = AgentConfigLoader.Load(localAgent, ["test", "test-alt"]);
    var upgradedInstructions = upgraded.Policies["low"].ModelInstructions;
    Assert(upgradedInstructions.Contains("Older personalized text", StringComparison.Ordinal) &&
           upgradedInstructions.Contains("Authoritative runtime protocol", StringComparison.Ordinal) &&
           upgradedInstructions.Contains("Complete command pairs inside Markdown", StringComparison.Ordinal) &&
           upgradedInstructions.IndexOf("Authoritative runtime protocol", StringComparison.Ordinal) >
           upgradedInstructions.IndexOf("never execute", StringComparison.OrdinalIgnoreCase),
        "Older local instruction examples suppressed or overrode upgraded command semantics.");
    Assert(File.ReadAllText(localTexts) == legacyText,
        "Upgrading an old installation rewrote personalized instructions.");

    File.WriteAllText(localTexts, "texts:\n  some_other_id: Unrelated\n");
    try
    {
        AgentConfigLoader.Load(localAgent, ["test", "test-alt"]);
        throw new Exception("Unknown instruction reference was accepted.");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("unknown text 'mezhs_default'", StringComparison.Ordinal)) { }

    File.WriteAllText(localTexts, "texts:\n  mezhs_default: \"{nested}\" \n");
    try
    {
        AgentConfigLoader.Load(localAgent, ["test", "test-alt"]);
        throw new Exception("Nested instruction reference was accepted.");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("another reference", StringComparison.Ordinal)) { }

    File.WriteAllText(localTexts, "texts:\n  mezhs_default: one\n  mezhs_default: two\n");
    try
    {
        AgentConfigLoader.Load(localAgent, ["test", "test-alt"]);
        throw new Exception("Duplicate instruction ID was accepted.");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("Invalid instructions file", StringComparison.Ordinal)) { }
}
finally
{
    Environment.CurrentDirectory = savedDirectory;
    Directory.Delete(configScratch, recursive: true);
}

var normal = options.Policies["test"];
var done = options.Policies["test-done"];
var evidencePolicy = options.Policies["test-evidence"];
var timeoutPolicy = options.Policies["test-timeout"];

Assert(Directory.Exists(options.Workspace), "Configured Agent workspace was not resolved to an existing directory.");
Assert(options.Policies["low"].Name == "Test Instant" &&
       options.Policies["low"].ConnectionId == "test" &&
       options.Policies["low"].DefaultModel == "mock-fast" &&
       options.Policies["mid"].Name == "Test Mid" &&
       options.Policies["mid"].ConnectionId == "test" &&
       options.Policies["mid"].DefaultModel == "mock-deep" &&
       options.Policies["high"].Name == "Test High" &&
       options.Policies["high"].ConnectionId == "test-alt" &&
       options.Policies["high"].DefaultModel == "mock-deep",
    "Low/mid/high Agent policy names and connection/model defaults were not loaded and normalized.");
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
// Instructions are not Agent replies. Their inline syntax examples are never
// dispatched as host actions, even if they are recognizable in isolation.
Assert(normal.ModelInstructions.Contains("shell text", StringComparison.Ordinal),
    "Compiled instructions must teach inline tag matching.");
var inlineReply = parser.Parse("before <SH>echo INLINE</SH> after");
Assert(inlineReply.Commands.SequenceEqual([new Command("SH", "echo INLINE")]) &&
       inlineReply.VisibleContent == "before  after",
    "Inline SH syntax or exact display subtraction failed.");
const string standaloneSource = "before\n<SH>\necho one\necho two\n</SH>\n<DONE>\nafter";
var standaloneDocument = new RichDocumentParser().Parse(standaloneSource);
Assert(standaloneDocument.Document.Valid,
    "Standalone SH/DONE fixture failed canonical parsing: " +
    string.Join("; ", standaloneDocument.Document.Diagnostics.Select(diagnostic => diagnostic.Code)));
var parsed = parser.Parse(standaloneSource);
Assert(parsed.Commands.Count == 2, "Parser did not retain SH and DONE commands.");
Assert(parsed.Commands[0].Name == "SH" && parsed.Commands[0].Body == "\necho one\necho two\n",
    "Shell body was rewritten while parsing.");
Assert(parsed.Commands[1].Name == "DONE" && parsed.Commands[1].Body is null,
    "DONE marker was not parsed as a marker command.");
Assert(parsed.VisibleContent == "before\n\n\nafter",
    $"Protocol parser did not remove executable protocol from display content: '{parsed.VisibleContent}'.");

const string mappedSource = "visible before\n<SH>\necho mapped\n</SH>\n<DONE>\nvisible after";
var mappedDocument = new RichDocumentParser().Parse(mappedSource);
Assert(mappedDocument.Document.Valid,
    "Mapped SH/DONE fixture failed canonical parsing: " +
    string.Join("; ", mappedDocument.Document.Diagnostics.Select(diagnostic => diagnostic.Code)));
var mappedMessage = AgentApiMapper.ToView(
    new ApiChatHistoryMessage(
        "msg-view", "chat-view", normal.ConnectionId, "assistant", "agent",
        mappedSource,
        [], null, null, null, MessageStatus.Completed, null, now, now, now),
    parser);
Assert(mappedMessage.DisplayContent == "visible before\n\n\nvisible after",
    "Agent API did not own protocol stripping for Web display.");
Assert(mappedMessage.Commands.Count == 1 &&
       mappedMessage.Commands[0].Name == "SH" &&
       mappedMessage.Commands[0].Body == "\necho mapped\n" &&
       mappedMessage.Commands[0].CommandIndex == 0 &&
       mappedMessage.CompletionClaimed,
    "Agent API protocol view lost SH body/index or DONE claim.");

var promptBuilder = new AgentPromptBuilder(options);
Assert(options.Messages.ProtocolIntro!.Contains("Separate observed facts from hypotheses", StringComparison.Ordinal),
    "Initial Agent protocol lost evidence-calibration guidance.");
var commandResultPrompt = promptBuilder.BuildCommandResults(
    new[] { new Result("SH", "1", true, 0, "stdout: changed", null) },
    normal).Content;
Assert(commandResultPrompt.Contains("does not by itself prove the original cause", StringComparison.Ordinal),
    "Command-result prompt lost causality-calibration guidance.");
var limitedPrompt = promptBuilder.BuildCommandResults(
    new[] { new Result("SH", "2", true, 0, "123456789", null, 4) }, normal).Content;
Assert(limitedPrompt.Contains("1234", StringComparison.Ordinal) &&
       !limitedPrompt.Contains("12345", StringComparison.Ordinal) &&
       limitedPrompt.Contains("full result remains in execution history", StringComparison.Ordinal),
    "Per-command SH output limit did not bound the agent follow-up.");
var defaultPrompt = promptBuilder.BuildCommandResults(
    new[] { new Result("SH", "3", true, 0, "123456789", null) }, normal).Content;
Assert(defaultPrompt.Contains("123456789", StringComparison.Ordinal),
    "Unparameterized SH result unexpectedly truncated.");
Assert(!promptBuilder.BuildContinue(normal).Content.Contains("<DONE>", StringComparison.Ordinal),
    "requireDone=false continuation still instructs DONE.");
Assert(promptBuilder.BuildContinue(done).Content.Contains("<DONE>", StringComparison.Ordinal),
    "requireDone=true continuation lost DONE guidance.");

var shellOptions = new AgentOptions
{
    Version = options.Version,
    AgentStorage = Path.Combine(Path.GetTempPath(), $"mezhs-agent-policy-{Guid.NewGuid():N}.sqlite"),
    Workspace = options.Workspace,
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
    // This test exercises SH only; no ATTACH operation is dispatched.
    var interpreter = new Interpreter(parser, evaluations, new Shell(executor, shellOptions), null!);
    var emptyEnvironment = new Dictionary<string, string>();

    var serialA1 = store.TryCreateRootExecution(
        normal.Id, normal.ConnectionId, "chat_serial_a", "manual", null,
        "serial a1", emptyEnvironment, normal.Snapshot)
        ?? throw new InvalidOperationException("Could not admit serial execution A1.");
    Thread.Sleep(5);
    var serialA2 = store.TryCreateRootExecution(
        normal.Id, normal.ConnectionId, "chat_serial_a", "manual", null,
        "serial a2", emptyEnvironment, normal.Snapshot)
        ?? throw new InvalidOperationException("Could not admit serial execution A2.");
    Thread.Sleep(5);
    var parallelB = store.TryCreateRootExecution(
        normal.Id, normal.ConnectionId, "chat_parallel_b", "manual", null,
        "parallel b", emptyEnvironment, normal.Snapshot)
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
        "shell test", emptyEnvironment, normal.Snapshot)
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
    Assert(simpleChild.Command == "\necho MEZHS_SHELL_OK\n", "Shell command text was rewritten before persistence/execution.");
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
    Assert(duplicateChildren.All(record => record.Command == "\necho DUPLICATE_OK\n"),
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
        "timeout test", emptyEnvironment, timeoutPolicy.Snapshot)
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
    DeleteDatabase(shellOptions.AgentStorage);
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

    dotnet run --project (Join-Path $temp 'Test.csproj') -c Release -- $configPath (Join-Path $PSScriptRoot "mezhs.agent.core.test.yaml")
    if ($LASTEXITCODE -ne 0) { throw "Policy/shell behavior test failed with exit code $LASTEXITCODE." }
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
