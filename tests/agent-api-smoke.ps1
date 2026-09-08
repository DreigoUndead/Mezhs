$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

$root = Split-Path -Parent $PSScriptRoot
$mezhsConfig = Join-Path $PSScriptRoot "mezhs.test.yaml"
$agentConfig = Join-Path $PSScriptRoot "mezhs.agent.test.yaml"
$dataPath = Join-Path $PSScriptRoot "data-agent-test"
$genericDataPath = Join-Path $PSScriptRoot "data"

$apiOut = Join-Path $PSScriptRoot "agent-http-api.out.log"
$apiErr = Join-Path $PSScriptRoot "agent-http-api.err.log"
$agentOut = Join-Path $PSScriptRoot "agent-http-agent.out.log"
$agentErr = Join-Path $PSScriptRoot "agent-http-agent.err.log"
$webOut = Join-Path $PSScriptRoot "agent-http-web.out.log"
$webErr = Join-Path $PSScriptRoot "agent-http-web.err.log"
$badConfig = Join-Path $PSScriptRoot "mezhs.agent.nonloopback.tmp.yaml"
$badOut = Join-Path $PSScriptRoot "agent-http-bad.out.log"
$badErr = Join-Path $PSScriptRoot "agent-http-bad.err.log"
$badWebOut = Join-Path $PSScriptRoot "agent-http-bad-web.out.log"
$badWebErr = Join-Path $PSScriptRoot "agent-http-bad-web.err.log"

foreach ($path in @($dataPath, $genericDataPath)) {
    Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
}
foreach ($path in @($apiOut, $apiErr, $agentOut, $agentErr, $webOut, $webErr, $badConfig, $badOut, $badErr, $badWebOut, $badWebErr)) {
    Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
}

function Wait-Health([string]$uri) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(25)
    do {
        try {
            $health = Invoke-RestMethod -Uri $uri
            if ($health.status -eq "ok") { return }
        } catch {
            if ([DateTimeOffset]::UtcNow -ge $deadline) { throw }
        }
        Start-Sleep -Milliseconds 150
    } while ($true)
}

function Start-AgentExecution([string]$policyId, [string]$taskInput, [hashtable]$environment = $null) {
    $body = @{ policyId = $policyId; input = $taskInput }
    if ($null -ne $environment) { $body.environment = $environment }
    return Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:5199/v1/executions" `
        -ContentType "application/json" -Body (ConvertTo-Json $body -Depth 5)
}

function Wait-AgentExecution([string]$executionId) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(25)
    do {
        $execution = Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/executions/$executionId"
        if ($execution.status -in @("Completed", "Failed", "Cancelled", "Interrupted")) { return $execution }
        if ([DateTimeOffset]::UtcNow -ge $deadline) { throw "Execution $executionId did not reach a terminal state." }
        Start-Sleep -Milliseconds 100
    } while ($true)
}

function Get-Status([string]$uri) {
    $client = [Net.Http.HttpClient]::new()
    try {
        $response = $client.GetAsync($uri).GetAwaiter().GetResult()
        try { return [int]$response.StatusCode } finally { $response.Dispose() }
    } finally { $client.Dispose() }
}

# Host-shell Agent API must fail closed if configured for direct network exposure.
(Get-Content -LiteralPath $agentConfig -Raw).Replace(
    "listen: http://127.0.0.1:5199",
    "listen: http://0.0.0.0:5199") | Set-Content -LiteralPath $badConfig -Encoding UTF8
$agentDll = (Resolve-Path (Join-Path $root "src\Mezhs.Agent.Api\bin\Release\net10.0\Mezhs.Agent.Api.dll")).Path
$badAgent = Start-Process -FilePath "dotnet" `
    -ArgumentList @($agentDll, "--config", $badConfig) `
    -WorkingDirectory $root -RedirectStandardOutput $badOut -RedirectStandardError $badErr -WindowStyle Hidden -PassThru
try {
    if (-not $badAgent.WaitForExit(8000)) {
        Stop-Process -Id $badAgent.Id -Force
        $badAgent.WaitForExit()
        throw "Agent API accepted a non-loopback listener for host-shell execution."
    }
    $badText = (Get-Content -LiteralPath $badErr -Raw) + (Get-Content -LiteralPath $badOut -Raw)
    if ($badAgent.ExitCode -eq 0 -or $badText -notmatch "loopback") {
        throw "Non-loopback Agent API configuration did not fail specifically at the exposure boundary. Output: $badText"
    }
} finally {
    if (-not $badAgent.HasExited) { Stop-Process -Id $badAgent.Id -Force; $badAgent.WaitForExit() }
}

$api = Start-Process -FilePath "dotnet" `
    -ArgumentList @("run", "--project", (Join-Path $root "src\Mezhs.Api\Mezhs.Api.csproj"), "-c", "Release", "--no-build", "--", "--config", $mezhsConfig) `
    -WorkingDirectory $root -RedirectStandardOutput $apiOut -RedirectStandardError $apiErr -WindowStyle Hidden -PassThru
$agent = $null
$web = $null

try {
    Wait-Health "http://127.0.0.1:5198/health"
    $agent = Start-Process -FilePath "dotnet" `
        -ArgumentList @("run", "--project", (Join-Path $root "src\Mezhs.Agent.Api\Mezhs.Agent.Api.csproj"), "-c", "Release", "--no-build", "--", "--config", $agentConfig) `
        -WorkingDirectory $root -RedirectStandardOutput $agentOut -RedirectStandardError $agentErr -WindowStyle Hidden -PassThru
    Wait-Health "http://127.0.0.1:5199/health"

    if ((Get-Status "http://127.0.0.1:5199/v1/runtime") -ne 200) {
        throw "Loopback Agent API unexpectedly requires authentication."
    }
    if ((Get-Status "http://127.0.0.1:5199/v1/metrics") -ne 404) {
        throw "Unused generic metrics endpoint still exists."
    }

    $runtime = Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/runtime"
    if (-not $runtime.mezhsApiHealthy) { throw "Agent API cannot reach generic MEZS API." }

    $originClient = [Net.Http.HttpClient]::new()
    try {
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, "http://127.0.0.1:5199/v1/runtime")
        $request.Headers.Add("Origin", "https://example.invalid")
        $response = $originClient.SendAsync($request).GetAwaiter().GetResult()
        try {
            if ($response.Headers.Contains("Access-Control-Allow-Origin")) {
                throw "Agent API still emits cross-origin access headers."
            }
        } finally { $response.Dispose(); $request.Dispose() }
    } finally { $originClient.Dispose() }

    $created = Start-AgentExecution "test" "hello agent"
    $completed = Wait-AgentExecution $created.executionId
    if ($completed.status -ne "Completed" -or -not $completed.chatId) {
        throw "Execution did not preserve chat/completion state."
    }
    if ($completed.PSObject.Properties.Name -contains "requester") {
        throw "Execution API still exposes unauthenticated requester provenance."
    }

    $environmentTask = @"
<SH>
echo %TEST_AGENT_VALUE%
</SH>
"@
    $environmentExecution = Start-AgentExecution "test" $environmentTask @{ TEST_AGENT_VALUE = "ENVIRONMENT_OK" }
    $environmentRoot = Wait-AgentExecution $environmentExecution.executionId
    $environmentExecutions = @(Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/agent-chats/$($environmentRoot.chatId)/executions")
    $environmentShell = @($environmentExecutions | Where-Object { $_.kind -eq "Shell" })[0]
    if ($environmentShell.result -notmatch "ENVIRONMENT_OK") { throw "Policy-approved environment variable did not reach the child shell." }

    $environmentMessages = Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/agent-chats/$($environmentRoot.chatId)/messages"
    $protocolMessage = $null
    foreach ($candidate in $environmentMessages) {
        if ($candidate.role -eq "assistant" -and $null -ne $candidate.commands -and $candidate.commands.Count -gt 0 -and $candidate.commands[0].name -eq "SH") {
            $protocolMessage = $candidate
            break
        }
    }
    if ($null -eq $protocolMessage -or $protocolMessage.commands[0].name -ne "SH" -or
        $protocolMessage.commands[0].body -notmatch "TEST_AGENT_VALUE" -or
        $protocolMessage.displayContent -match 'TEST_AGENT_VALUE') {
        throw "Agent message API did not expose canonical SH protocol metadata/display content."
    }

    $client = [Net.Http.HttpClient]::new()
    try {
        $invalidBody = ConvertTo-Json @{ policyId = "test"; input = "invalid env"; environment = @{ PATH = "malicious" } }
        $content = [Net.Http.StringContent]::new($invalidBody, [Text.Encoding]::UTF8, "application/json")
        try {
            $response = $client.PostAsync("http://127.0.0.1:5199/v1/executions", $content).GetAwaiter().GetResult()
            $responseBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ([int]$response.StatusCode -ne 400 -or $responseBody -notmatch "does not allow environment variable") {
                throw "Unapproved environment mutation was not rejected by policy. HTTP $([int]$response.StatusCode): $responseBody"
            }
            $response.Dispose()
        } finally { $content.Dispose() }
    } finally { $client.Dispose() }

    $debug = Invoke-WebRequest -Uri "http://127.0.0.1:5199/v1/agent-chats/$($completed.chatId)/debug-log"
    if ($debug.StatusCode -ne 200 -or $debug.Headers["Content-Disposition"] -notmatch "attachment" -or $debug.Content -notmatch $completed.executionId) {
        throw "Local debug log is not downloadable/auditable."
    }
    if ($debug.Content -match '(?m)^requester:') {
        throw "Debug log still exposes obsolete requester metadata."
    }

    # Agent Web is the browser-facing half of the same local trust boundary and must also reject network exposure.
    $badWeb = Start-Process -FilePath "dotnet" `
        -ArgumentList @("run", "--project", (Join-Path $root "src\Mezhs.Agent.Web\Mezhs.Agent.Web.csproj"), "-c", "Release", "--no-build", "--no-launch-profile", "--", "--urls", "http://0.0.0.0:5201", "--Agent:BaseUrl", "http://127.0.0.1:5199") `
        -WorkingDirectory $root -RedirectStandardOutput $badWebOut -RedirectStandardError $badWebErr -WindowStyle Hidden -PassThru
    try {
        if (-not $badWeb.WaitForExit(8000)) {
            Stop-Process -Id $badWeb.Id -Force
            $badWeb.WaitForExit()
            throw "Agent Web accepted a non-loopback listener."
        }
        $badWebText = (Get-Content -LiteralPath $badWebErr -Raw) + (Get-Content -LiteralPath $badWebOut -Raw)
        if ($badWeb.ExitCode -eq 0 -or $badWebText -notmatch "loopback") {
            throw "Non-loopback Agent Web configuration did not fail specifically at the exposure boundary. Output: $badWebText"
        }
    } finally {
        if (-not $badWeb.HasExited) { Stop-Process -Id $badWeb.Id -Force; $badWeb.WaitForExit() }
    }

    $web = Start-Process -FilePath "dotnet" `
        -ArgumentList @("run", "--project", (Join-Path $root "src\Mezhs.Agent.Web\Mezhs.Agent.Web.csproj"), "-c", "Release", "--no-build", "--no-launch-profile", "--", "--urls", "http://127.0.0.1:5200", "--Agent:BaseUrl", "http://127.0.0.1:5199") `
        -WorkingDirectory $root -RedirectStandardOutput $webOut -RedirectStandardError $webErr -WindowStyle Hidden -PassThru
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    do {
        try {
            $proxied = Invoke-RestMethod -Uri "http://127.0.0.1:5200/v1/runtime"
            if ($proxied.status -eq "ok") { break }
        } catch {
            if ([DateTimeOffset]::UtcNow -ge $deadline) { throw }
        }
        Start-Sleep -Milliseconds 150
    } while ($true)

    $proxiedDebug = Invoke-WebRequest -Uri "http://127.0.0.1:5200/v1/agent-chats/$($completed.chatId)/debug-log"
    if ($proxiedDebug.StatusCode -ne 200) { throw "Agent Web did not proxy the local debug log." }

    $webExecution = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:5200/v1/executions" `
        -ContentType "application/json" -Body (ConvertTo-Json @{ policyId = "test"; input = "web proxy execution" })
    $webCompleted = Wait-AgentExecution $webExecution.executionId
    if ($webCompleted.status -ne "Completed" -or -not $webCompleted.chatId) {
        throw "Agent Web proxy did not create a normal durable execution."
    }

    Write-Host "PASS: Agent API/Web are loopback-only, CORS-closed, environment-scoped, DTO-backed, free of requester/metrics ceremony, and proxy locally."
}
finally {
    if ($null -ne $web -and -not $web.HasExited) { Stop-Process -Id $web.Id -Force; $web.WaitForExit() }
    if ($null -ne $agent -and -not $agent.HasExited) { Stop-Process -Id $agent.Id -Force; $agent.WaitForExit() }
    if (-not $api.HasExited) { Stop-Process -Id $api.Id -Force; $api.WaitForExit() }
    Remove-Item -LiteralPath $dataPath -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $genericDataPath -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($path in @($apiOut, $apiErr, $agentOut, $agentErr, $webOut, $webErr, $badConfig, $badOut, $badErr, $badWebOut, $badWebErr)) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}
