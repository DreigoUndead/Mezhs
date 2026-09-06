$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

$root = Split-Path -Parent $PSScriptRoot
$mezhsConfig = Join-Path $PSScriptRoot "mezhs.test.yaml"
$agentConfig = Join-Path $PSScriptRoot "mezhs.agent.test.yaml"
$dataPath = Join-Path $PSScriptRoot "data-agent-test"
$genericDataPath = Join-Path $PSScriptRoot "data"
$requestHeaders = @{ "X-MEZHS-Requester" = "resilience-test" }
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

function Start-Execution([string]$policyId, [string]$taskInput) {
    return Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:5199/v1/executions" -Headers $requestHeaders `
        -ContentType "application/json" -Body (ConvertTo-Json @{ policyId = $policyId; input = $taskInput })
}

function Get-Execution([string]$executionId) {
    return Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/executions/$executionId"
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

function Wait-AttachedRunningExecution([string]$executionId, [int]$seconds = 10) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($seconds)
    do {
        $execution = Get-Execution $executionId
        if ($execution.status -eq "Running" -and $execution.chatId) { return $execution }
        if ($execution.status -in @("Completed", "Failed", "Cancelled", "Interrupted")) {
            throw "Execution $executionId became $($execution.status) before a chat was attached."
        }
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            throw "Execution $executionId did not attach a chat while running. Last state: $($execution.status), chatId=$($execution.chatId)"
        }
        Start-Sleep -Milliseconds 100
    } while ($true)
}

function Wait-Shell([string]$chatId, [string[]]$statuses, [int]$seconds = 20) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($seconds)
    do {
        $response = Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/agent-chats/$chatId/executions"
        $executions = @($response | ForEach-Object { $_ })
        $shell = @($executions | Where-Object { $_.kind -eq "Shell" -and $_.status -in $statuses } | Sort-Object createdAt -Descending)[0]
        if ($null -ne $shell) { return $shell }
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            $observed = @($executions | Where-Object { $_.kind -eq "Shell" } | ForEach-Object { "$($_.executionId):$($_.status)" })
            throw "No shell in chat $chatId reached [$($statuses -join ', ')]. Observed: $($observed -join ', ')"
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

    # Running cancellation is a request first, terminal acknowledgement only after the process stops.
    $longTask = @"
<SH>
ping -n 30 127.0.0.1 >nul
</SH>
"@
    $cancel = Start-Execution "test-cancel" $longTask
    $cancelRunning = Wait-AttachedRunningExecution $cancel.executionId
    $null = Wait-Shell $cancelRunning.chatId @("Running")
    $cancelResponse = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:5199/v1/executions/$($cancel.executionId)/cancel"
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
    $blockingRunning = Wait-AttachedRunningExecution $blocking.executionId
    $null = Wait-Shell $blockingRunning.chatId @("Running")
    $queued = Start-Execution "test" "queued after blocking execution"
    $queuedRecord = Wait-ExecutionStatus $queued.executionId @("Queued") 5

    $client = [Net.Http.HttpClient]::new()
    try {
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

    $metrics = Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/metrics"
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

    Write-Host "PASS: CancelRequested acknowledgement, bounded admission/metrics, forced restart recovery, and post-restart execution are correct."
}
finally {
    if ($null -ne $agent -and -not $agent.HasExited) {
        & taskkill /PID $agent.Id /T /F | Out-Null
        $agent.WaitForExit()
    }
    if (-not $api.HasExited) { Stop-Process -Id $api.Id -Force; $api.WaitForExit() }
    Remove-Item -LiteralPath $dataPath -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $genericDataPath -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($path in @($apiOut, $apiErr, $agentOut, $agentErr)) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
}
