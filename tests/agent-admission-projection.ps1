$ErrorActionPreference = 'Stop'

# Deterministic real-host test: BrowserIntegrationBase -> MessageService -> AgentWorker
# -> AgentChatView. The test-only plugin never creates an Electron session.
$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([IO.Path]::GetTempPath()) ('mezhs-agent-admission-e2e-' + [Guid]::NewGuid().ToString('N'))
$agentOutput = Join-Path $root 'src\Mezhs.Agent.Api\bin\Release\net10.0'
$pluginInstalled = Join-Path $agentOutput 'Mezhs.Integrations.AdmissionProbe.dll'
$hostDll = Join-Path $agentOutput 'Mezhs.Agent.Api.dll'
$agent = $null
$baseUrl = 'http://127.0.0.1:5397'

function Assert([bool]$condition, [string]$description) {
    if (-not $condition) { throw $description }
}
function Until([scriptblock]$check, [string]$description, [int]$timeout = 12) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($timeout)
    do {
        if (& $check) { return }
        Start-Sleep -Milliseconds 75
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Timed out: $description"
}
function Post([string]$route, [hashtable]$data) {
    Invoke-RestMethod -Method Post -Uri "$baseUrl$route" -ContentType 'application/json' -Body ($data | ConvertTo-Json -Depth 8)
}
function Get([string]$route) { Invoke-RestMethod -Method Get -Uri "$baseUrl$route" }
function ProbeFile([string]$connection, [string]$messageId, [string]$suffix) {
    Join-Path $temp "$connection-$messageId.$suffix"
}
function Release([string]$connection, [string]$messageId) {
    [IO.File]::WriteAllText((ProbeFile $connection $messageId 'release'), 'release')
}
function Get-AgentMessage([string]$chatId) {
    $all = @(Get "/v1/agent-chats/$chatId/messages")
    @($all | Where-Object { $_.role -eq 'user' }) | Select-Object -Last 1
}
function Projection([string]$chatId) { (Get "/v1/agent-chats/$chatId").executionStatus }
function Await-Projection([string]$chatId, [string]$expected, [int]$timeout = 12) {
    Until { (Projection $chatId) -eq $expected } "Agent $chatId effective $expected" $timeout
    $detail = Get "/v1/agent-chats/$chatId"
    $list = @(Get '/v1/agent-chats')
    $matching = @($list | Where-Object { $_.chatId -eq $chatId })
    Assert ($matching.Count -eq 1 -and $matching[0].executionStatus -eq $expected -and
        $matching[0].executionStatusId -eq $detail.executionStatusId) "Agent list and detail disagree at $expected for $chatId."
}
function Await-Execution([string]$id, [string]$expected) {
    Until { (Get "/v1/executions/$id").status -eq $expected } "Execution $id $expected"
}
function Await-Message([string]$id, [string]$expected) {
    Until { (Get "/v1/messages/$id").status -eq $expected } "Message $id $expected"
}
function Await-Submitted([string]$chatId) {
    Until { $null -ne (Get-AgentMessage $chatId) } "Agent message created in $chatId"
    Get-AgentMessage $chatId
}

New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    if (Get-NetTCPConnection -LocalPort 5397 -State Listen -ErrorAction SilentlyContinue) {
        throw 'Port 5397 is in use; do not disturb an existing listener.'
    }
    Assert (Test-Path -LiteralPath $hostDll) 'Agent API Release build is required before this test.'
    Assert (-not (Test-Path -LiteralPath $pluginInstalled)) 'Test plugin already exists; refusing to overwrite.'
    $browserProject = Join-Path $root 'src\Mezhs.Integration.Browser\Mezhs.Integration.Browser.csproj'
    $pluginSource = Join-Path $PSScriptRoot 'AgentAdmissionProbe.cs'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <AssemblyName>Mezhs.Integrations.AdmissionProbe</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$pluginSource" />
    <ProjectReference Include="$browserProject" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $temp 'AdmissionProbe.csproj') -Encoding UTF8

    dotnet build (Join-Path $temp 'AdmissionProbe.csproj') -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Test-only admission plugin build failed.' }
    $built = Join-Path $temp 'bin\Release\net10.0\Mezhs.Integrations.AdmissionProbe.dll'
    Copy-Item -LiteralPath $built -Destination $pluginInstalled -ErrorAction Stop

    # Core and Agent YAML are in distinct roots to check independent resolution.
    $coreFolder = Join-Path $temp 'core-config'
    $agentFolder = Join-Path $temp 'agent-config'
    New-Item -ItemType Directory -Path $coreFolder, $agentFolder -Force | Out-Null
    $portableTemp = $temp.Replace('\', '/')
    @"
version: 1
server:
  listen: $baseUrl
transport:
  type: electron
  electronDirectory: ../../electron
storage:
  root: data/core
connections:
  - id: cap
    name: Capacity Probe
    integration: admission-probe
    workspace: $portableTemp
    limits:
      maxParallel: 1
      minInterval: 0
  - id: spacing
    name: Spacing Probe
    integration: admission-probe
    workspace: $portableTemp
    limits:
      maxParallel: 0
      minInterval: 5000
"@ | Set-Content -LiteralPath (Join-Path $coreFolder 'mezhs.yaml') -Encoding UTF8

    $agentSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'mezhs.agent.test.yaml') -Raw
    $header = [regex]::Split($agentSource, '(?m)^policies:\s*$')[0]
    $header = $header.Replace('http://127.0.0.1:5199', $baseUrl)
    $header = $header.Replace('data-agent-test/agent.sqlite', 'data/agent.sqlite')
    $policies = @'
policies:
  capacity:
    connectionId: cap
    instructions: Complete the deterministic capacity test.
    commands:
      allow: []
    environment:
      allow: []
    completion:
      requireDone: false
      requiredSuccessfulCommands: []
    limits:
      commandTimeoutSeconds: 2
  spacing:
    connectionId: spacing
    instructions: Complete the deterministic spacing test.
    commands:
      allow: []
    environment:
      allow: []
    completion:
      requireDone: false
      requiredSuccessfulCommands: []
    limits:
      commandTimeoutSeconds: 2
'@
    ($header + $policies) | Set-Content -LiteralPath (Join-Path $agentFolder 'agent.yaml') -Encoding UTF8
    $stdout = Join-Path $temp 'agent.stdout.log'
    $stderr = Join-Path $temp 'agent.stderr.log'
    $agent = Start-Process -FilePath dotnet -ArgumentList @($hostDll, '--config', (Join-Path $agentFolder 'agent.yaml'), '--mezhs-config', (Join-Path $coreFolder 'mezhs.yaml')) -WorkingDirectory $root -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru
    try {
        Until { try { (Get '/health').status -eq 'ok' } catch { $false } } 'isolated Agent API health' 20
    } catch {
        $details = if (Test-Path $stderr) { Get-Content $stderr -Raw } else { '' }
        throw "Agent probe host failed startup: $details"
    }

    # An ordinary chat uses the SAME integration instance as an Agent model turn.
    $ordinary = Post '/v1/messages' @{ connectionId = 'cap'; content = 'ordinary capacity holder' }
    Until { Test-Path (ProbeFile 'cap' $ordinary.messageId 'started') } 'ordinary capacity admission'
    Assert ((Get "/v1/messages/$($ordinary.messageId)").activity -eq 'waiting') 'Probe provider must report waiting independently of admission.'

    $agentOne = Post '/v1/executions' @{ policyId = 'capacity'; input = 'capacity overlap' }
    Until { (Get "/v1/executions/$($agentOne.executionId)").chatId } 'first Agent chat association'
    $chatOne = (Get "/v1/executions/$($agentOne.executionId)").chatId
    $agentMessage = Await-Submitted $chatOne
    Await-Projection $chatOne 'Waiting'
    Assert ((Get "/v1/executions/$($agentOne.executionId)").status -eq 'Running') 'Persisted root must stay Running while effective status is Waiting.'
    Assert ((Get "/v1/messages/$($agentMessage.messageId)").status -eq 'Running') 'Waiting must relate to an active Agent model message.'
    Assert ((Get "/v1/messages/$($agentMessage.messageId)").activity -ne 'waiting') 'Capacity Waiting must not depend on provider activity.'

    # Same-chat unrelated ordinary message is queued behind this Agent message.
    $unrelated = Post '/v1/messages' @{ connectionId = 'cap'; chatId = $chatOne; content = 'unrelated same-chat message' }
    Await-Message $unrelated.messageId 'Queued'
    Release 'cap' $ordinary.messageId
    Await-Message $ordinary.messageId 'Completed'
    Until { Test-Path (ProbeFile 'cap' $agentMessage.messageId 'started') } 'Agent admission after capacity release'
    Await-Projection $chatOne 'Running'
    Assert ((Get "/v1/messages/$($agentMessage.messageId)").activity -eq 'waiting') 'Provider waiting must remain available as message detail despite Agent Running.'
    Assert ((Get "/v1/messages/$($unrelated.messageId)").status -eq 'Queued') 'Unrelated message bypassed per-chat ordering.'
    Release 'cap' $agentMessage.messageId
    Await-Execution $agentOne.executionId 'Completed'
    Await-Projection $chatOne 'Completed'

    Until { Test-Path (ProbeFile 'cap' $unrelated.messageId 'started') } 'unrelated provider admission'
    Assert ((Get "/v1/messages/$($unrelated.messageId)").activity -eq 'waiting') 'Unrelated provider activity missing.'
    # New Agent root is Running; its own message is merely queued behind that ordinary provider turn.
    $agentTwo = Post '/v1/executions' @{ policyId = 'capacity'; input = 'same chat isolation'; chatId = $chatOne }
    Until { (Get "/v1/executions/$($agentTwo.executionId)").status -eq 'Running' } 'next root running'
    Until { (Get-AgentMessage $chatOne).messageId -ne $agentMessage.messageId } 'second Agent message queued'
    $secondMessage = Get-AgentMessage $chatOne
    Assert ((Get "/v1/messages/$($secondMessage.messageId)").status -eq 'Queued') 'Second Agent model should queue behind unrelated same-chat provider.'
    Await-Projection $chatOne 'Running'
    $null = Post "/v1/executions/$($agentTwo.executionId)/cancel" @{}
    Await-Execution $agentTwo.executionId 'Cancelled'
    Await-Projection $chatOne 'Cancelled'
    Release 'cap' $unrelated.messageId
    Await-Message $unrelated.messageId 'Completed'
    Assert ((Projection $chatOne) -eq 'Cancelled') 'Unrelated provider completion changed cancelled Agent status.'

    # Cancellation during genuine capacity Waiting clears the transient flag.
    $other = Post '/v1/messages' @{ connectionId = 'cap'; content = 'second capacity holder' }
    Until { Test-Path (ProbeFile 'cap' $other.messageId 'started') } 'second capacity holder start'
    $cancelled = Post '/v1/executions' @{ policyId = 'capacity'; input = 'cancel blocked model' }
    Until { (Get "/v1/executions/$($cancelled.executionId)").chatId } 'cancel Agent chat association'
    $cancelChat = (Get "/v1/executions/$($cancelled.executionId)").chatId
    Await-Projection $cancelChat 'Waiting'
    $null = Post "/v1/executions/$($cancelled.executionId)/cancel" @{}
    Await-Execution $cancelled.executionId 'Cancelled'
    Await-Projection $cancelChat 'Cancelled'
    Release 'cap' $other.messageId
    Await-Message $other.messageId 'Completed'
    Assert ((Projection $cancelChat) -eq 'Cancelled') 'Cancelled Agent retained stale capacity Waiting.'

    # Unlimited parallel capacity still reports positive configured start spacing.
    $spacingFirst = Post '/v1/messages' @{ connectionId = 'spacing'; content = 'spacing first' }
    Until { Test-Path (ProbeFile 'spacing' $spacingFirst.messageId 'started') } 'spacing first start'
    Release 'spacing' $spacingFirst.messageId
    Await-Message $spacingFirst.messageId 'Completed'
    $spacingAgent = Post '/v1/executions' @{ policyId = 'spacing'; input = 'spacing blocked Agent' }
    Until { (Get "/v1/executions/$($spacingAgent.executionId)").chatId } 'spacing Agent chat'
    $spacingChat = (Get "/v1/executions/$($spacingAgent.executionId)").chatId
    Await-Projection $spacingChat 'Waiting'
    Assert ((Get "/v1/executions/$($spacingAgent.executionId)").status -eq 'Running') 'Spacing Waiting was incorrectly persisted.'
    $spacingMessage = Await-Submitted $spacingChat
    Until { Test-Path (ProbeFile 'spacing' $spacingMessage.messageId 'started') } 'spacing admission after interval' 12
    Await-Projection $spacingChat 'Running'
    Assert ((Get "/v1/messages/$($spacingMessage.messageId)").activity -eq 'waiting') 'Provider waiting after spacing admission must not keep Waiting.'
    Release 'spacing' $spacingMessage.messageId
    Await-Execution $spacingAgent.executionId 'Completed'
    Await-Projection $spacingChat 'Completed'

    Assert (Test-Path (Join-Path $coreFolder 'data\core\chats.sqlite')) 'Core storage did not resolve relative to Core YAML.'
    Assert (Test-Path (Join-Path $agentFolder 'data\agent.sqlite')) 'Agent storage did not resolve relative to Agent YAML.'
    Write-Host 'PASS: real Agent API admission projection, ordinary/Agent shared capacity, provider activity isolation, exact same-chat association, cancellation, terminal cleanup, spacing and separately rooted config.'
} finally {
    if ($null -ne $agent -and -not $agent.HasExited) {
        Stop-Process -Id $agent.Id -Force -ErrorAction SilentlyContinue
        $agent.WaitForExit(5000) | Out-Null
    }
    Remove-Item -LiteralPath $pluginInstalled -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
