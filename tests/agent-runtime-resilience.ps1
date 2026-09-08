$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

$root = Split-Path -Parent $PSScriptRoot
$mezhsConfig = Join-Path $PSScriptRoot "mezhs.test.yaml"
$agentConfig = Join-Path $PSScriptRoot "mezhs.agent.test.yaml"
$dataPath = Join-Path $PSScriptRoot "data-agent-test"
$genericDataPath = Join-Path $PSScriptRoot "data"
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
    return Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:5199/v1/executions" `
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

function Get-ChatExecutions([string]$chatId) {
    $response = Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/agent-chats/$chatId/executions"
    return @($response | ForEach-Object { $_ })
}

function Wait-Shell([string]$chatId, [string[]]$statuses, [int]$seconds = 20) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($seconds)
    do {
        $executions = Get-ChatExecutions $chatId
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

    # Root Agent cancellation owns reasoning cancellation; shell execution has a separate Kill endpoint.
    $longTask = @"
<SH>
ping -n 30 127.0.0.1 >nul
</SH>
"@
    $cancel = Start-Execution "test-cancel" $longTask
    $cancelRunning = Wait-AttachedRunningExecution $cancel.executionId
    $runningShell = Wait-Shell $cancelRunning.chatId @("Running")

    $childCancelClient = [Net.Http.HttpClient]::new()
    try {
        $childCancelResponse = $childCancelClient.PostAsync(
            "http://127.0.0.1:5199/v1/executions/$($runningShell.executionId)/cancel",
            $null).GetAwaiter().GetResult()
        $childCancelText = $childCancelResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        try {
            if ([int]$childCancelResponse.StatusCode -ne 400 -or $childCancelText -notmatch "use /kill") {
                throw "Shell execution accepted Agent cancellation instead of preserving separate Kill ownership. HTTP $([int]$childCancelResponse.StatusCode): $childCancelText"
            }
        } finally { $childCancelResponse.Dispose() }
    } finally { $childCancelClient.Dispose() }

    $cancelResponse = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:5199/v1/executions/$($cancel.executionId)/cancel"
    if ($cancelResponse.status -ne "CancelRequested") {
        throw "Running cancellation skipped acknowledgement state: $($cancelResponse.status)"
    }
    $cancelledRoot = Wait-ExecutionStatus $cancel.executionId @("Cancelled") 10
    $killedShell = Wait-Shell $cancelledRoot.chatId @("Killed") 10
    if (-not $cancelledRoot.completedAt -or -not $killedShell.completedAt) {
        throw "Cancellation/kill became terminal without durable completion timestamps."
    }

    # With one worker and queueCapacity=1, one running + one queued fills durable admission capacity.
    $blocking = Start-Execution "test-cancel" $longTask
    $blockingRunning = Wait-AttachedRunningExecution $blocking.executionId
    $blockingShell = Wait-Shell $blockingRunning.chatId @("Running")
    $queued = Start-Execution "test" "queued after blocking execution"
    $queuedRecord = Wait-ExecutionStatus $queued.executionId @("Queued") 5
    if ($queuedRecord.startedAt) { throw "Durably queued execution already has a started timestamp." }

    $stateResponse = Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/executions"
    $state = @($stateResponse | ForEach-Object { $_ })
    $runningRoots = @($state | Where-Object { $_.kind -eq "Agent" -and $_.status -eq "Running" })
    $queuedRoots = @($state | Where-Object { $_.kind -eq "Agent" -and $_.status -eq "Queued" })
    if ($runningRoots.Count -ne 1 -or $queuedRoots.Count -ne 1) {
        throw "Persisted queue state is not one-running/one-queued: running=$($runningRoots.Count), queued=$($queuedRoots.Count)"
    }

    $client = [Net.Http.HttpClient]::new()
    try {
        $body = [Net.Http.StringContent]::new(
            (ConvertTo-Json @{ policyId = "test"; input = "must be admission rejected" }),
            [Text.Encoding]::UTF8,
            "application/json")
        try {
            $response = $client.PostAsync("http://127.0.0.1:5199/v1/executions", $body).GetAwaiter().GetResult()
            $responseText = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ([int]$response.StatusCode -ne 429 -or $responseText -notmatch "queue is full") {
                throw "Durable admission did not reject excess work. HTTP $([int]$response.StatusCode): $responseText"
            }
            $response.Dispose()
        } finally { $body.Dispose() }
    } finally { $client.Dispose() }

    $afterRejectedResponse = Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/executions"
    $afterRejected = @($afterRejectedResponse | ForEach-Object { $_ })
    if (@($afterRejected | Where-Object { $_.request -eq "must be admission rejected" }).Count -ne 0) {
        throw "Rejected admission still created a failed execution record."
    }

    # Kill only Agent API. Detached Executor shell must survive, then Agent must reattach to the same durable shell row.
    Stop-Process -Id $agent.Id -Force
    $agent.WaitForExit()
    $agent = $null

    Start-Sleep -Milliseconds 300
    $agent = Start-AgentProcess
    Wait-Health "http://127.0.0.1:5199/health"

    $recoveredBlocking = Wait-ExecutionStatus $blocking.executionId @("Completed") 45
    $recoveredShell = Wait-Shell $recoveredBlocking.chatId @("Completed") 45
    if ($recoveredShell.executionId -ne $blockingShell.executionId) {
        throw "Agent restart launched a duplicate shell row. Before=$($blockingShell.executionId), after=$($recoveredShell.executionId)"
    }
    $shellRows = @(Get-ChatExecutions $recoveredBlocking.chatId | Where-Object { $_.kind -eq "Shell" })
    if ($shellRows.Count -ne 1) {
        throw "Recovered Agent chat contains $($shellRows.Count) shell rows instead of reconnecting to one durable execution."
    }

    $recoveredQueued = Wait-ExecutionStatus $queued.executionId @("Completed") 20
    if (-not $recoveredQueued.startedAt -or -not $recoveredQueued.completedAt) {
        throw "Queued execution did not survive restart and run from durable storage."
    }

    $afterRestart = Start-Execution "test" "execution after restart"
    $afterRestartDone = Wait-ExecutionStatus $afterRestart.executionId @("Completed") 15
    if ($afterRestartDone.status -ne "Completed") { throw "Agent did not accept work after restart recovery." }

    Write-Host "PASS: Root cancellation kills Executor work, durable admission remains bounded, detached shell survives Agent death, recovery reconnects without duplicate shell execution, queued work survives restart, and post-restart work completes."
}
finally {
    if ($null -ne $agent -and -not $agent.HasExited) {
        Stop-Process -Id $agent.Id -Force
        $agent.WaitForExit()
    }
    if (-not $api.HasExited) { Stop-Process -Id $api.Id -Force; $api.WaitForExit() }
    Remove-Item -LiteralPath $dataPath -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $genericDataPath -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($path in @($apiOut, $apiErr, $agentOut, $agentErr)) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
}
