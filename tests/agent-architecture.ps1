$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot

$requiredFiles = @(
    "src/Mezhs.Api.Contracts/Mezhs.Api.Contracts.csproj",
    "src/Mezhs.Api.Contracts/ApiContracts.cs",
    "src/Mezhs.Agent.Api/Mezhs.Agent.Api.csproj",
    "src/Mezhs.Agent.Api/Program.cs",
    "src/Mezhs.Agent.Api/Configuration/AgentConfigLoader.cs",
    "src/Mezhs.Agent.Api/Configuration/AgentOptions.cs",
    "src/Mezhs.Agent.Api/Models/AgentModels.cs",
    "src/Mezhs.Agent.Api/Persistence/AgentStore.cs",
    "src/Mezhs.Agent.Api/Policy/PolicyModels.cs",
    "src/Mezhs.Agent.Api/Policy/PolicyContext.cs",
    "src/Mezhs.Agent.Api/Policy/PolicyDecoder.cs",
    "src/Mezhs.Agent.Api/Services/AgentPromptBuilder.cs",
    "src/Mezhs.Agent.Api/Services/MezhsClient.cs",
    "src/Mezhs.Agent.Api/Services/AgentService.cs",
    "src/Mezhs.Agent.Api/Services/AgentWorker.cs",
    "src/Mezhs.Agent.Api/Services/PolicyRegistry.cs"
)
foreach ($file in $requiredFiles) {
    if (-not (Test-Path (Join-Path $root $file))) {
        throw "Missing agent foundation file: $file"
    }
}

$agentProject = Get-Content (Join-Path $root "src/Mezhs.Agent.Api/Mezhs.Agent.Api.csproj") -Raw
if ($agentProject -match 'Mezhs\.Api\\Mezhs\.Api\.csproj' -or
    $agentProject -match 'Mezhs\.Integration' -or
    $agentProject -match 'Mezhs\.Integrations') {
    throw "Mezhs.Agent.Api must not reference the generic API implementation or integrations."
}
if ($agentProject -notmatch 'Mezhs\.Api\.Contracts') {
    throw "Mezhs.Agent.Api must use the shared compiler-checked HTTP contracts."
}
if ($agentProject -notmatch 'Microsoft.Data.Sqlite') {
    throw "Mezhs.Agent.Api does not have durable SQLite storage."
}

$genericProject = Get-Content (Join-Path $root "src/Mezhs.Api/Mezhs.Api.csproj") -Raw
if ($genericProject -match 'Mezhs\.Agent') {
    throw "The generic MEŽS API depends on the agent extension."
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
if ($agentConfig -match 'RequireDone|MaxTurns|ContainsDone') {
    throw "Policy-specific semantics leaked back into AgentConfigLoader."
}

$policyModels = Get-Content (Join-Path $root "src/Mezhs.Agent.Api/Policy/PolicyModels.cs") -Raw
$policyDecoder = Get-Content (Join-Path $root "src/Mezhs.Agent.Api/Policy/PolicyDecoder.cs") -Raw
$policyContext = Get-Content (Join-Path $root "src/Mezhs.Agent.Api/Policy/PolicyContext.cs") -Raw
if ($policyModels -notmatch 'PolicyDefinition' -or
    $policyModels -notmatch 'PolicySettings' -or
    $policyDecoder -notmatch 'Deserialize<Dictionary<string, PolicyDefinition>>') {
    throw "Policy YAML is not mapped through hard typed models."
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

$worker = Get-Content (Join-Path $root "src/Mezhs.Agent.Api/Services/AgentWorker.cs") -Raw
$promptBuilder = Get-Content (Join-Path $root "src/Mezhs.Agent.Api/Services/AgentPromptBuilder.cs") -Raw
if ($worker -notmatch 'BackgroundService' -or $worker -notmatch 'Channel<string>') {
    throw "Agent execution is not owned by a host-managed worker queue."
}
if ($worker -notmatch 'ConcurrentDictionary<string, SemaphoreSlim>') {
    throw "Agent executions are not serialized per chat."
}
if ($worker -match 'ContainsDone|RequireDone|MaxTurns|BuildInitialPrompt|BuildContinuePrompt|BuildPolicyCorrectionPrompt') {
    throw "AgentWorker is interpreting policy/prompt details instead of consuming dedicated components."
}
if ($worker -notmatch 'ValidateTurn' -or $worker -notmatch 'EvaluateCompletion') {
    throw "AgentWorker is bypassing PolicyContext runtime decisions."
}
if ($promptBuilder -notmatch 'BuildInitial' -or
    $promptBuilder -notmatch 'BuildContinue' -or
    $promptBuilder -notmatch 'BuildPolicyCorrection') {
    throw "Agent prompts are not owned by AgentPromptBuilder."
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

$config = Get-Content (Join-Path $root "mezhs.yaml") -Raw
if ($config -notmatch '(?m)^extensions:' -or $config -notmatch '(?m)^\s+agent:') {
    throw "Agent extension is not configured through the shared MEŽS YAML."
}

Write-Host "PASS: typed policy models compile into PolicyContext, prompts are separate, and Agent/API wire contracts are compiler-checked."
