$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot

$requiredFiles = @(
    "src/Mezhs.Api.Contracts/Mezhs.Api.Contracts.csproj",
    "src/Mezhs.Api.Contracts/ApiContracts.cs",
    "src/Mezhs.Agent.Api/Mezhs.Agent.Api.csproj",
    "src/Mezhs.Agent.Api/Program.cs",
    "src/Mezhs.Agent.Api/Commands/AgentCommandParser.cs",
    "src/Mezhs.Agent.Api/Commands/AgentCommandInterpreter.cs",
    "src/Mezhs.Agent.Api/Commands/ShellCommandHandler.cs",
    "src/Mezhs.Agent.Api/Configuration/AgentConfigLoader.cs",
    "src/Mezhs.Agent.Api/Configuration/AgentOptions.cs",
    "src/Mezhs.Agent.Api/Models/AgentModels.cs",
    "src/Mezhs.Agent.Api/Persistence/AgentStore.cs",
    "src/Mezhs.Agent.Api/Policy/PolicyModels.cs",
    "src/Mezhs.Agent.Api/Policy/PolicyContext.cs",
    "src/Mezhs.Agent.Api/Policy/PolicyDecoder.cs",
    "src/Mezhs.Agent.Api/Policy/PolicyEvaluationService.cs",
    "src/Mezhs.Agent.Api/Services/AgentDebugLogBuilder.cs",
    "src/Mezhs.Agent.Api/Services/AgentPromptBuilder.cs",
    "src/Mezhs.Agent.Api/Services/MezhsClient.cs",
    "src/Mezhs.Agent.Api/Services/AgentService.cs",
    "src/Mezhs.Agent.Api/Services/AgentWorker.cs",
    "src/Mezhs.Agent.Api/Services/PolicyRegistry.cs",
    "src/Mezhs.Agent.Web/Mezhs.Agent.Web.csproj",
    "src/Mezhs.Agent.Web/Program.cs",
    "src/Mezhs.Agent.Web/package.json",
    "src/Mezhs.Agent.Web/src/App.tsx"
)
foreach ($file in $requiredFiles) {
    if (-not (Test-Path (Join-Path $root $file))) {
        throw "Missing agent foundation file: $file"
    }
}

$agentProject = Get-Content (Join-Path $root "src/Mezhs.Agent.Api/Mezhs.Agent.Api.csproj") -Raw
if ($agentProject -match 'Mezhs\.Api\\Mezhs\.Api\.csproj' -or
    $agentProject -match 'Mezhs\.Integration' -or
    $agentProject -match 'Mezhs\.Integrations' -or
    $agentProject -match 'Mezhs\.Agent\.Web') {
    throw "Mezhs.Agent.Api must remain API-only and must not reference the generic API implementation, integrations, or Agent Web."
}
if ($agentProject -notmatch 'Mezhs\.Api\.Contracts') {
    throw "Mezhs.Agent.Api must use the shared compiler-checked HTTP contracts."
}
if ($agentProject -notmatch 'Microsoft.Data.Sqlite') {
    throw "Mezhs.Agent.Api does not have durable SQLite storage."
}

$agentWebProject = Get-Content (Join-Path $root "src/Mezhs.Agent.Web/Mezhs.Agent.Web.csproj") -Raw
$agentWebHost = Get-Content (Join-Path $root "src/Mezhs.Agent.Web/Program.cs") -Raw
if ($agentWebProject -notmatch 'Microsoft.NET.Sdk.Web' -or
    $agentWebProject -notmatch 'Mezhs\.Web\.Lib') {
    throw "Mezhs.Agent.Web is not a standalone web host consuming the shared web library."
}
if ($agentWebHost -notmatch 'Agent:BaseUrl' -or
    $agentWebHost -notmatch 'IHttpClientFactory' -or
    $agentWebHost -notmatch '/v1/\{\*\*path\}') {
    throw "Mezhs.Agent.Web is not independently hosting the dashboard and forwarding its API boundary."
}
if ($agentWebHost -notmatch 'ContentDisposition') {
    throw "Agent Web proxy does not preserve downloadable Agent API response metadata."
}

$genericProject = Get-Content (Join-Path $root "src/Mezhs.Api/Mezhs.Api.csproj") -Raw
$genericWebProject = Get-Content (Join-Path $root "src/Mezhs.Web/Mezhs.Web.csproj") -Raw
if ($genericProject -match 'Mezhs\.Agent' -or $genericWebProject -match 'Mezhs\.Agent') {
    throw "Generic MEŽS projects depend on the agent extension."
}
if ($genericProject -notmatch 'Mezhs\.Api\.Contracts') {
    throw "The generic MEŽS API is not using the shared HTTP contracts."
}

$agentOptions = Get-Content (Join-Path $root "src/Mezhs.Agent.Api/Configuration/AgentOptions.cs") -Raw
$agentConfig = Get-Content (Join-Path $root "src/Mezhs.Agent.Api/Configuration/AgentConfigLoader.cs") -Raw
if ($agentOptions -match '127\.0\.0\.1' -or $agentConfig -match '127\.0\.0\.1') {
    throw "Agent service endpoints are hardcoded instead of required from YAML."
}
if ($agentOptions -notmatch 'required Uri Listen' -or
    $agentOptions -notmatch 'required Uri MezhsApi') {
    throw "Agent service endpoints are not strongly typed required configuration."
}
if ($agentConfig -notmatch 'new PolicyDecoder\(\)\.DecodePolicies') {
    throw "AgentConfigLoader is interpreting policy semantics instead of delegating them to PolicyDecoder."
}
if ($agentConfig -match 'RequireDone|MaxTurns|ValidateAction|commands\.allow') {
    throw "Policy-specific semantics leaked back into AgentConfigLoader."
}

$policyModels = Get-Content (Join-Path $root "src/Mezhs.Agent.Api/Policy/PolicyModels.cs") -Raw
$policyDecoder = Get-Content (Join-Path $root "src/Mezhs.Agent.Api/Policy/PolicyDecoder.cs") -Raw
$policyContext = Get-Content (Join-Path $root "src/Mezhs.Agent.Api/Policy/PolicyContext.cs") -Raw
if ($policyModels -notmatch 'PolicyDefinition' -or
    $policyModels -notmatch 'PolicySettings' -or
    $policyModels -notmatch 'PolicyCommandSettings' -or
    $policyDecoder -notmatch 'Deserialize<Dictionary<string, PolicyDefinition>>') {
    throw "Policy YAML is not mapped through hard typed models."
}
if ($policyModels -notmatch 'CommandTimeoutSeconds' -or
    $policyDecoder -notmatch 'commandTimeoutSeconds must be greater than zero') {
    throw "Command timeout is not a typed, validated policy limit."
}
if ($policyDecoder -match 'GeneratedRegex' -or
    $policyDecoder -match 'Dictionary<string, object\?>') {
    throw "Policy decoding still relies on ad-hoc regex/dictionary schema machinery."
}
if ($policyContext -notmatch 'ValidateTurn' -or
    $policyContext -notmatch 'EvaluateCompletion' -or
    $policyContext -notmatch 'ValidateAction') {
    throw "PolicyContext does not own runtime policy decisions."
}
if ($policyContext -notmatch 'does not explicitly allow') {
    throw "PolicyContext no longer defaults executable actions to deny."
}
if ($policyDecoder -notmatch 'PolicyActionRuleDecision\.Deny' -or
    $policyDecoder -notmatch 'PolicyActionRuleDecision\.Allow') {
    throw "Typed command allow/deny rules are not compiled into PolicyContext."
}

$worker = Get-Content (Join-Path $root "src/Mezhs.Agent.Api/Services/AgentWorker.cs") -Raw
$promptBuilder = Get-Content (Join-Path $root "src/Mezhs.Agent.Api/Services/AgentPromptBuilder.cs") -Raw
$commandParser = Get-Content (Join-Path $root "src/Mezhs.Agent.Api/Commands/AgentCommandParser.cs") -Raw
$commandInterpreter = Get-Content (Join-Path $root "src/Mezhs.Agent.Api/Commands/AgentCommandInterpreter.cs") -Raw
$shellHandler = Get-Content (Join-Path $root "src/Mezhs.Agent.Api/Commands/ShellCommandHandler.cs") -Raw
$debugLogBuilder = Get-Content (Join-Path $root "src/Mezhs.Agent.Api/Services/AgentDebugLogBuilder.cs") -Raw
$agentProgram = Get-Content (Join-Path $root "src/Mezhs.Agent.Api/Program.cs") -Raw
if ($worker -notmatch 'BackgroundService' -or $worker -notmatch 'Channel<string>') {
    throw "Agent execution is not owned by a host-managed worker queue."
}
if ($worker -notmatch 'ConcurrentDictionary<string, SemaphoreSlim>') {
    throw "Agent executions are not serialized per chat."
}
if ($worker -match 'ContainsDone|RequireDone|MaxTurns|TryBlockStart|<SH|ProcessStartInfo') {
    throw "AgentWorker is interpreting policy/command details instead of consuming dedicated components."
}
if ($worker -notmatch 'ValidateTurn' -or
    $worker -notmatch 'EvaluateCompletion' -or
    $worker -notmatch 'InterpretAsync') {
    throw "AgentWorker is bypassing PolicyContext or AgentCommandInterpreter decisions."
}
if ($promptBuilder -notmatch 'BuildInitial' -or
    $promptBuilder -notmatch 'BuildContinue' -or
    $promptBuilder -notmatch 'BuildPolicyCorrection' -or
    $promptBuilder -notmatch 'BuildCommandResults') {
    throw "Agent prompts are not owned by AgentPromptBuilder."
}
if ($commandParser -notmatch 'AgentCommandBatch' -or
    $commandParser -notmatch 'Nested or mismatched' -or
    $commandParser -notmatch 'CompletionClaimed') {
    throw "Agent commands are not parsed through the strict shared command parser."
}
if ($commandInterpreter -notmatch 'IEnumerable<IAgentCommandHandler>' -or
    $commandInterpreter -notmatch 'ValidateAction' -or
    $commandInterpreter -notmatch '_handlers' -or
    $commandInterpreter -notmatch 'CommandTimeoutSeconds') {
    throw "Agent command dispatch is not extensible, policy-governed, and timeout-aware."
}
if ($shellHandler -notmatch 'ProcessStartInfo' -or
    $shellHandler -notmatch 'MEZHS_EXECUTION_ID' -or
    $shellHandler -notmatch 'MEZHS_PARENT_EXECUTION_ID' -or
    $shellHandler -notmatch 'MEZHS_CORRELATION_ID') {
    throw "Shell execution does not propagate MEŽS execution context out of band."
}
if ($shellHandler -notmatch 'CancelAfter\(context\.Timeout\)' -or
    $shellHandler -notmatch 'timed out after') {
    throw "Shell execution is not bounded by the policy command timeout."
}
if ($shellHandler -match 'Environment\.SetEnvironmentVariable') {
    throw "Shell execution mutates the Agent process environment."
}
if ($debugLogBuilder -notmatch '=== ACTIVE ===' -or
    $debugLogBuilder -notmatch '=== EXECUTIONS ===' -or
    $debugLogBuilder -notmatch '=== CHAT MESSAGES ===' -or
    $agentProgram -notmatch '/v1/agent-chats/\{chatId\}/debug-log') {
    throw "Agent API does not expose a durable chat/execution debug log."
}

$client = Get-Content (Join-Path $root "src/Mezhs.Agent.Api/Services/MezhsClient.cs") -Raw
if ($client -notmatch 'new CreateChatRequest' -or
    $client -notmatch 'new PostMessageRequest' -or
    $client -notmatch 'ReadFromJsonAsync<ApiChat>' -or
    $client -notmatch 'ReadFromJsonAsync<ApiMessage>') {
    throw "MezhsClient is not using the shared compiler-checked HTTP contracts."
}
if ($client -match 'new\s*\{') {
    throw "MezhsClient still creates anonymous HTTP contract payloads."
}

$store = Get-Content (Join-Path $root "src/Mezhs.Agent.Api/Persistence/AgentStore.cs") -Raw
foreach ($required in @(
    'CREATE TABLE IF NOT EXISTS AgentChats',
    'CREATE TABLE IF NOT EXISTS Executions',
    'ParentExecutionId',
    'CorrelationId',
    'PolicySnapshot',
    'Paused',
    'Interrupted'
)) {
    if ($store -notmatch [regex]::Escape($required)) {
        throw "AgentStore is missing required audit concept: $required"
    }
}
if ($store -match 'PolicyId = excluded\.PolicyId') {
    throw "Agent chat ownership can still be silently reassigned to another policy."
}
if ($store -notmatch 'ON CONFLICT\(ChatId\) DO NOTHING' -or
    $store -notmatch 'EnsurePolicyMatches') {
    throw "Agent chat policy ownership is not claimed atomically and enforced by the store."
}
if ($store -notmatch 'CreateChildExecution' -or
    $store -notmatch 'SetAgentChatPaused') {
    throw "Agent child execution causality or persisted pause state is missing from AgentStore."
}

$service = Get-Content (Join-Path $root "src/Mezhs.Agent.Api/Services/AgentService.cs") -Raw
if ($service -match 'request\.ConnectionId' -or $service -notmatch 'policy\.ConnectionId') {
    throw "Manual execution can override the policy-owned connection."
}
if ($service -notmatch 'source: "manual"') {
    throw "Manual Agent Web execution no longer records a truthful manual source."
}
if ($service -notmatch 'SetPaused' -or $service -notmatch 'worker\.Cancel') {
    throw "Agent chat pause does not persist and stop active root executions."
}

$agentWeb = Get-Content (Join-Path $root "src/Mezhs.Agent.Web/src/App.tsx") -Raw
$agentWebPackage = Get-Content (Join-Path $root "src/Mezhs.Agent.Web/package.json") -Raw
if ($agentWebPackage -notmatch '@mezhs/web-lib') {
    throw "Agent Web is not consuming the shared MEŽS web library."
}
if ($agentWeb -notmatch '/v1/agent-chats' -or
    $agentWeb -notmatch '/v1/executions' -or
    $agentWeb -notmatch 'New agent chat' -or
    $agentWeb -notmatch 'Pause' -or
    $agentWeb -match 'connectionId:\s*effective') {
    throw "Agent Web does not provide the intended manual-first chat and pause workflow."
}
if ($agentWeb -notmatch 'Stop agent execution' -or
    $agentWeb -notmatch '/cancel' -or
    $agentWeb -notmatch '/debug-log') {
    throw "Agent Web does not expose live command stop/debug controls."
}

$config = Get-Content (Join-Path $root "mezhs.yaml") -Raw
if ($config -notmatch '(?m)^extensions:' -or $config -notmatch '(?m)^\s+agent:') {
    throw "Agent extension is not configured through the shared MEŽS YAML."
}
if ($config -notmatch '(?ms)^\s+commands:\s*\r?\n\s+allow:\s*\r?\n\s+- SH') {
    throw "Default policy does not explicitly allow shell execution."
}
if ($config -notmatch '(?m)^\s+commandTimeoutSeconds:\s+120\s*$') {
    throw "Default policy does not configure the expected 120-second command timeout."
}

Write-Host "PASS: Agent API/Web are separate projects, policy owns behavior/connection/timeouts, command execution is auditable/stoppable, shell context is isolated, and pause state is durable."
