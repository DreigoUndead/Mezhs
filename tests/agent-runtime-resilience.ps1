$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

$root = Split-Path -Parent $PSScriptRoot
$mezhsConfig = Join-Path $PSScriptRoot "mezhs.test.yaml"
$agentConfig = Join-Path $PSScriptRoot "mezhs.agent.test.yaml"
$dataPath = Join-Path $PSScriptRoot "data-agent-test"
$genericDataPath = Join-Path $PSScriptRoot "data"
$apiKey = "agent-resilience-$([Guid]::NewGuid().ToString('N'))"
$env:MEZHS_AGENT_API_KEY = $apiKey
$auth = @{ Authorization = "Bearer $apiKey"; "X-MEZHS-Requester" = "resilience-test" }
$apiOut = Join-Path $PSScriptRoot "agent-resilience-api.out.log"
$apiErr = Join-Path $PSScriptRoot "agent-resilience-api.err.log"
$agentOut = Join-Path $PSScriptRoot "agent-resilience-agent.out.log"
$agentErr = Join-Path $PSScriptRoot "agent-resilience-agent.err.log"

foreach ($path in @($dataPath, $genericDataPath)) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue }
foreach ($path in @($apiOut, $apiErr, $agentOut, $agentErr)) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }

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

function Start-AgentProcess() {
    return Start-Process -FilePath "dotnet" `
        -ArgumentList @("run", "--project", (Join-Path $root "src\Mezhs.Agent.Api\Mezhs.Agent.Api.csproj"), "-c", "Release", "--no-build", "--", "--config", $agentConfig) `
        -WorkingDirectory $root -RedirectStandardOutput $agentOut -RedirectStandardError $agentErr -WindowStyle Hidden -PassThru
}

function Start-Execution([string]$policyId, [string]$input) {
    return Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:5199/v1/executions" -Headers $auth `
        -ContentType "application/json" -Body (ConvertTo-Json @{ policyId = $policyId; input = $input })
}

function Get-Execution([string]$executionId) {
    return Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/executions/$executionId" -Headers $auth
}

function Wait-ExecutionStatus([string]$executionId, [string[]]$statuses, [int]$seconds = 20) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($seconds)
    do {
        $execution = Get-Execution $executionId
        if ($execution.status -in $statuses) { return $execution }
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            throw "Execution $executionId did not reach [$($statuses -join ', ')]. Last state: $($execution.status)"
        }
        Start-Sleep -Milliseconds 100
    } while ($true)
}

function Wait-Shell([string]$chatId, [string[]]$statuses, [int]$seconds = 20) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($seconds)
    do {
        $executions = @(Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/agent-chats/$chatId/executions" -Headers $auth)
        $shell = @($executions | Where-Object { $_.kind -eq "Shell" } | Sort-Object createdAt -Descending)[0]
        if ($null -ne $shell -and $shell.status -in $statuses) { return $shell }
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            throw "No shell in chat $chatId reached [$($statuses -join ', ')]."
        }
        Start-Sleep -Milliseconds 100
    } while ($true)
}

$api = Start-Process -FilePath "dotnet" `
    -ArgumentList @("run", "--project", (Join-Path $root "src\Mezhs.Api\Mezhs.Api.csproj"), "-c", "Release", "--no-build", "--", "--config", $mezhsConfig) `
    -WorkingDirectory $root -RedirectStandardOutput $apiOut -RedirectStandardError $apiErr -WindowStyle Hidden -PassThru
$agent = $null

try {
    Wait-Health "http://127.0.0.1:5198/health"
    $agent = Start-AgentProcess
    Wait-Health "http://127.0.0.1:5199/health"

    # Timeout must terminate the actual shell promptly and persist failed child evidence.
    $timeoutTask = @"
<SH>
ping -n 6 127.0.0.1 >nul
</SH>
"@
    $timeoutStarted = [DateTimeOffset]::UtcNow
    $timeout = Start-Execution "test-timeout" $timeoutTask
    $timeoutRoot = Wait-ExecutionStatus $timeout.executionId @("Completed", "Failed") 12
    $timeoutElapsed = [DateTimeOffset]::UtcNow - $timeoutStarted
    if ($timeoutElapsed -gt [TimeSpan]::FromSeconds(8)) {
        throw "Timed-out shell kept the Agent lifecycle busy for $timeoutElapsed."
    }
    $timeoutShell = Wait-Shell $timeoutRoot.chatId @("Failed")
    if ($timeoutShell.error -notmatch "timed out after 1 seconds") {
        throw "Shell timeout did not persist explicit timeout evidence: $($timeoutShell.error)"
    }

    # Running cancellation is a request first, terminal acknowledgement only after the process stops.
    $longTask = @"
<SH>
ping -n 30 127.0.0.1 >nul
</SH>
"@
    $cancel = Start-Execution "test-cancel" $longTask
    $cancelRunning = Wait-ExecutionStatus $cancel.executionId @("Running")
    if (-not $cancelRunning.chatId) { throw "Cancellation test root never attached a chat." }
    $null = Wait-Shell $cancelRunning.chatId @("Running")
    $cancelResponse = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:5199/v1/executions/$($cancel.executionId)/cancel" -Headers $auth
    if ($cancelResponse.status -ne "CancelRequested") {
        throw "Running cancellation skipped acknowledgement state: $($cancelResponse.status)"
    }
    $cancelledRoot = Wait-ExecutionStatus $cancel.executionId @("Cancelled") 10
    $cancelledShell = Wait-Shell $cancelledRoot.chatId @("Cancelled") 10
    if (-not $cancelledRoot.completedAt -or -not $cancelledShell.completedAt) {
        throw "Cancellation became terminal without durable completion timestamps."
    }

    # One active + one queued fills configured capacity. A third request must be rejected, not accumulated.
    $blocking = Start-Execution "test-cancel" $longTask
    $blockingRunning = Wait-ExecutionStatus $blocking.executionId @("Running")
    $null = Wait-Shell $blockingRunning.chatId @("Running")
    $queued = Start-Execution "test" "queued after blocking execution"
    $queuedRecord = Wait-ExecutionStatus $queued.executionId @("Queued") 5

    $client = [Net.Http.HttpClient]::new()
    try {
        $client.DefaultRequestHeaders.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $apiKey)
        $client.DefaultRequestHeaders.Add("X-MEZHS-Requester", "resilience-test")
        $body = [Net.Http.StringContent]::new(
            (ConvertTo-Json @{ policyId = "test"; input = "must be admission rejected" }),
            [Text.Encoding]::UTF8,
            "application/json")
        try {
            $response = $client.PostAsync("http://127.0.0.1:5199/v1/executions", $body).GetAwaiter().GetResult()
            $responseText = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ([int]$response.StatusCode -ne 429 -or $responseText -notmatch "queue is full") {
                throw "Bounded admission did not reject excess work. HTTP $([int]$response.StatusCode): $responseText"
            }
            $response.Dispose()
        } finally { $body.Dispose() }
    } finally { $client.Dispose() }

    $metrics = Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/metrics" -Headers $auth
    if ($metrics.activeExecutions -ne 1 -or $metrics.queueLength -ne 1) {
        throw "Runtime metrics disagree with configured one-active/one-queued state: active=$($metrics.activeExecutions), queue=$($metrics.queueLength)"
    }

    # Force-kill Agent and its shell tree. Startup recovery must resolve both active and queued persisted rows.
    & taskkill /PID $agent.Id /T /F | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not terminate Agent process tree for restart recovery test." }
    $agent.WaitForExit()
    $agent = $null

    Start-Sleep -Milliseconds 300
    $agent = Start-AgentProcess
    Wait-Health "http://127.0.0.1:5199/health"

    $recoveredBlocking = Wait-ExecutionStatus $blocking.executionId @("Interrupted") 8
    $recoveredQueued = Wait-ExecutionStatus $queued.executionId @("Interrupted") 8
    $recoveredShell = Wait-Shell $recoveredBlocking.chatId @("Interrupted") 8
    if ($recoveredBlocking.error -notmatch "restarted" -or $recoveredQueued.error -notmatch "restarted" -or $recoveredShell.error -notmatch "restarted") {
        throw "Restart recovery did not persist explicit interruption evidence."
    }

    $afterRestart = Start-Execution "test" "execution after restart"
    $afterRestartDone = Wait-ExecutionStatus $afterRestart.executionId @("Completed") 15
    if ($afterRestartDone.status -ne "Completed") { throw "Agent did not accept work after restart recovery." }

    Write-Host "PASS: shell timeout, CancelRequested acknowledgement, bounded admission/metrics, forced restart recovery, and post-restart execution are correct."
}
finally {
    if ($null -ne $agent -and -not $agent.HasExited) {
        & taskkill /PID $agent.Id /T /F | Out-Null
        $agent.WaitForExit()
    }
    if (-not $api.HasExited) { Stop-Process -Id $api.Id -Force; $api.WaitForExit() }
    Remove-Item Env:MEZHS_AGENT_API_KEY -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $dataPath -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $genericDataPath -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($path in @($apiOut, $apiErr, $agentOut, $agentErr)) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
}
