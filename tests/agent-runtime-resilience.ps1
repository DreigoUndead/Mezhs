$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('mezhs-t6-resilience-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $temp 'workspace') -Force | Out-Null
function Free-Port {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}
$corePort = Free-Port
$agentPort = Free-Port
if ($corePort -eq $agentPort) { $agentPort = Free-Port }
$coreUrl = "http://127.0.0.1:$corePort"
$agentUrl = "http://127.0.0.1:$agentPort"
$mezhsConfig = Join-Path $temp 'core.yaml'
$agentConfig = Join-Path $temp 'agent.yaml'
$electron = Join-Path $root 'electron'
$coreYaml = (Get-Content (Join-Path $PSScriptRoot 'mezhs.test.yaml') -Raw).Replace('http://127.0.0.1:5198', $coreUrl).Replace('root: data', "root: $(Join-Path $temp 'core')").Replace('electronDirectory: ../electron', "electronDirectory: $electron")
$agentYaml = (Get-Content (Join-Path $PSScriptRoot 'mezhs.agent.test.yaml') -Raw).Replace('http://127.0.0.1:5199', $agentUrl).Replace('root: data-agent-test/core', "root: $(Join-Path $temp 'agent-core')").Replace('agentStorage: data-agent-test/agent.sqlite', "agentStorage: $(Join-Path $temp 'agent.sqlite')").Replace('workspace: ..', "workspace: $(Join-Path $temp 'workspace')").Replace('electronDirectory: ../electron', "electronDirectory: $electron")
Set-Content -LiteralPath $mezhsConfig -Value $coreYaml -Encoding UTF8
Set-Content -LiteralPath $agentConfig -Value $agentYaml -Encoding UTF8
$apiOut = Join-Path $temp 'core.out'
$apiErr = Join-Path $temp 'core.err'
$agentOut = Join-Path $temp 'agent.out'
$agentErr = Join-Path $temp 'agent.err'
$apiDll = (Resolve-Path (Join-Path $root 'src/Mezhs.Api/bin/Release/net10.0/Mezhs.Api.dll')).Path
$agentDll = (Resolve-Path (Join-Path $root 'src/Mezhs.Agent.Api/bin/Release/net10.0/Mezhs.Agent.Api.dll')).Path

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
        -ArgumentList @($agentDll, "--config", $agentConfig) `
        -WorkingDirectory $root -RedirectStandardOutput $agentOut -RedirectStandardError $agentErr -WindowStyle Hidden -PassThru
}

function Start-Execution([string]$policyId, [string]$taskInput, [string]$chatId = '') {
    $body = @{ policyId = $policyId; input = $taskInput }
    if ($chatId) { $body.chatId = $chatId }
    return Invoke-RestMethod -Method Post -Uri "$agentUrl/v1/executions" `
        -ContentType "application/json" -Body (ConvertTo-Json $body)
}

function Get-Execution([string]$executionId) {
    return Invoke-RestMethod -Uri "$agentUrl/v1/executions/$executionId"
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
    $response = Invoke-RestMethod -Uri "$agentUrl/v1/agent-chats/$chatId/executions"
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
    -ArgumentList @($apiDll, "--config", $mezhsConfig) `
    -WorkingDirectory $root -RedirectStandardOutput $apiOut -RedirectStandardError $apiErr -WindowStyle Hidden -PassThru
$agent = $null

try {
    Wait-Health "$coreUrl/health"
    $agent = Start-AgentProcess
    Wait-Health "$agentUrl/health"

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
            "$agentUrl/v1/executions/$($runningShell.executionId)/cancel",
            $null).GetAwaiter().GetResult()
        $childCancelText = $childCancelResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        try {
            if ([int]$childCancelResponse.StatusCode -ne 400 -or $childCancelText -notmatch "use /kill") {
                throw "Shell execution accepted Agent cancellation instead of preserving separate Kill ownership. HTTP $([int]$childCancelResponse.StatusCode): $childCancelText"
            }
        } finally { $childCancelResponse.Dispose() }
    } finally { $childCancelClient.Dispose() }

    $cancelResponse = Invoke-RestMethod -Method Post -Uri "$agentUrl/v1/executions/$($cancel.executionId)/cancel"
    if ($cancelResponse.status -ne "CancelRequested") {
        throw "Running cancellation skipped acknowledgement state: $($cancelResponse.status)"
    }
    $cancelledRoot = Wait-ExecutionStatus $cancel.executionId @("Cancelled") 10
    $killedShell = Wait-Shell $cancelledRoot.chatId @("Killed") 10
    if (-not $cancelledRoot.completedAt -or -not $killedShell.completedAt) {
        throw "Cancellation/kill became terminal without durable completion timestamps."
    }

    # More than four independent shell-backed Agent executions must overlap in Running.
    $parallelTask = @"
<SH>
ping -n 25 127.0.0.1 >nul
</SH>
"@
    $parallel = @()
    for ($i = 1; $i -le 6; $i++) {
        $parallel += Start-Execution "test-cancel" $parallelTask
    }
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    $runningCount = 0
    $runningShellCount = 0
    do {
        $parallelStates = @($parallel | ForEach-Object { Get-Execution $_.executionId })
        $runningCount = @($parallelStates | Where-Object { $_.status -eq "Running" }).Count
        $runningShellCount = 0
        foreach ($state in $parallelStates) {
            if ($state.chatId) {
                $runningShellCount += @(Get-ChatExecutions $state.chatId | Where-Object {
                    $_.kind -eq "Shell" -and $_.status -eq "Running"
                }).Count
            }
        }
        if ($runningCount -eq 6 -and $runningShellCount -eq 6) { break }
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            throw "Six independent roots did not overlap: running=$runningCount, runningShells=$runningShellCount; states=$(@($parallelStates | ForEach-Object { $_.status }) -join ',')"
        }
        Start-Sleep -Milliseconds 150
    } while ($true)
    $dbPath = Join-Path $temp 'agent.sqlite'
    $sqliteCheck = Join-Path $temp 'sqlite-running.py'
    @'
import sqlite3
import sys
with sqlite3.connect(sys.argv[1]) as db:
    print(db.execute("SELECT COUNT(*) FROM Executions WHERE Kind = 'Agent' AND Status = 'Running'").fetchone()[0])
'@ | Set-Content -LiteralPath $sqliteCheck -Encoding UTF8
    $dbRunning = [int](& python $sqliteCheck $dbPath)
    if ($LASTEXITCODE -ne 0 -or $dbRunning -lt 6) {
        throw "SQLite did not durably record six Running Agents: $dbRunning"
    }
    Write-Host "CONCURRENCY api_running=$runningCount sqlite_running=$dbRunning executor_shells_running=$runningShellCount"

    # A failed integration request becomes Failed and must not stall another Agent.
    $failed = Start-Execution "test" "MEZHS_TEST_INTEGRATION_FAILURE"
    $healthy = Start-Execution "test" "independent success alongside failing execution"
    $failedDone = Wait-ExecutionStatus $failed.executionId @("Failed") 12
    $healthyDone = Wait-ExecutionStatus $healthy.executionId @("Completed") 12
    if (-not $failedDone.completedAt -or -not $healthyDone.completedAt) {
        throw "Failure isolation lacks durable terminal timestamps."
    }
    Write-Host "FAILURE_ISOLATION failed=$($failedDone.status) independent=$($healthyDone.status)"

    # Cancelling one root kills only its child; all other independent shells continue.
    $first = $parallel[0]
    $firstCancelled = Invoke-RestMethod -Method Post -Uri "$agentUrl/v1/executions/$($first.executionId)/cancel"
    $firstDone = Wait-ExecutionStatus $first.executionId @("Cancelled") 12
    $firstShell = Wait-Shell $firstDone.chatId @("Killed") 12
    $othersStillRunning = @($parallel[1..5] | Where-Object { (Get-Execution $_.executionId).status -eq "Running" }).Count
    if ($othersStillRunning -ne 5) {
        throw "Cancellation interrupted unrelated roots: stillRunning=$othersStillRunning"
    }
    foreach ($rootToCancel in $parallel[1..5]) {
        Invoke-RestMethod -Method Post -Uri "$agentUrl/v1/executions/$($rootToCancel.executionId)/cancel" | Out-Null
    }
    foreach ($rootToCancel in $parallel[1..5]) {
        $done = Wait-ExecutionStatus $rootToCancel.executionId @("Cancelled") 12
        $shell = Wait-Shell $done.chatId @("Killed") 12
    }
    Write-Host "PARALLEL_CANCELLATION six_roots=Cancelled six_shells=Killed others_unaffected=5"

    # SQLite, not a global worker slot, serializes two roots of the same existing Agent chat.
    $seed = Start-Execution "test-cancel" "seed existing Agent chat"
    $seedDone = Wait-ExecutionStatus $seed.executionId @("Completed") 12
    $blocking = Start-Execution "test-cancel" $longTask $seedDone.chatId
    $blockingRunning = Wait-AttachedRunningExecution $blocking.executionId
    $blockingShell = Wait-Shell $blockingRunning.chatId @("Running")
    # Cancel a queued execution before it is claimed; it must never start later.
    $neverStart = Start-Execution "test-cancel" "cancel before claim" $seedDone.chatId
    $neverStartQueued = Wait-ExecutionStatus $neverStart.executionId @("Queued") 5
    $neverStartCancel = Invoke-RestMethod -Method Post -Uri "$agentUrl/v1/executions/$($neverStart.executionId)/cancel"
    $neverStartDone = Wait-ExecutionStatus $neverStart.executionId @("Cancelled") 5
    if ($neverStartDone.startedAt -or -not $neverStartDone.completedAt) {
        throw "Cancelled queued execution was started or lacks durable cancellation."
    }
    Write-Host "QUEUED_CANCELLATION status=$($neverStartDone.status) startedAt=null"
    $queued = Start-Execution "test-cancel" "queued behind same-chat blocker" $seedDone.chatId
    $queuedRecord = Wait-ExecutionStatus $queued.executionId @("Queued") 5
    if ($queuedRecord.startedAt) { throw "Durably queued same-chat execution already has a started timestamp." }

    # An unrelated execution starts and finishes while the first shell remains active.
    $independent = Start-Execution "test" "unrelated concurrent execution"
    $independentDone = Wait-ExecutionStatus $independent.executionId @("Completed") 12
    if (-not $independentDone.startedAt -or -not $independentDone.completedAt) {
        throw "Independent execution did not run while the blocker was active."
    }
    if ((Get-Execution $blocking.executionId).status -ne "Running") {
        throw "Blocking execution did not overlap independent execution."
    }
    if ((Get-Execution $queued.executionId).status -ne "Queued") {
        throw "Same-chat execution did not stay queued while unrelated work ran."
    }
    $state = @(Invoke-RestMethod -Uri "$agentUrl/v1/executions")
    $runningRoots = @($state | Where-Object { $_.kind -eq "Agent" -and $_.status -eq "Running" })
    $queuedRoots = @($state | Where-Object { $_.kind -eq "Agent" -and $_.status -eq "Queued" })
    if ($runningRoots.Count -lt 1 -or $queuedRoots.Count -lt 1) {
        throw "Persisted same-chat queue state missing: running=$($runningRoots.Count), queued=$($queuedRoots.Count)"
    }
    Write-Host "RESILIENCE persisted_running=$($runningRoots.Count) persisted_queued=$($queuedRoots.Count), independent=$($independentDone.status)"

    # Kill only the actual Agent API process. Detached Executor shell must survive, then Agent must reattach to the same durable shell row.
    Stop-Process -Id $agent.Id -Force
    $agent.WaitForExit()
    $agent = $null

    Start-Sleep -Milliseconds 300
    $agent = Start-AgentProcess
    Wait-Health "$agentUrl/health"

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

    # After the first same-chat execution finishes, its successor must run without another API start.
    $shortTask = @"
<SH>
ping -n 5 127.0.0.1 >nul
</SH>
"@
    $serialOne = Start-Execution "test-cancel" $shortTask $seedDone.chatId
    $serialRunning = Wait-AttachedRunningExecution $serialOne.executionId
    $serialShell = Wait-Shell $serialRunning.chatId @("Running")
    $serialTwo = Start-Execution "test-cancel" "next same-chat execution, no external wake" $seedDone.chatId
    $serialQueued = Wait-ExecutionStatus $serialTwo.executionId @("Queued") 5
    if ($serialQueued.startedAt) { throw "Same-chat successor started before its predecessor completed." }
    $serialFirstDone = Wait-ExecutionStatus $serialOne.executionId @("Completed") 15
    $serialSecondDone = Wait-ExecutionStatus $serialTwo.executionId @("Completed") 15
    if (-not $serialSecondDone.startedAt -or -not $serialSecondDone.completedAt) {
        throw "Same-chat successor did not start from completion notification."
    }
    $neverStartFinal = Get-Execution $neverStart.executionId
    if ($neverStartFinal.status -ne "Cancelled" -or $neverStartFinal.startedAt) {
        throw "Previously cancelled queued execution started after recovery or predecessor completion."
    }
    Write-Host "SAME_CHAT_WAKE first=$($serialFirstDone.status) successor=$($serialSecondDone.status) cancelled_queued=$($neverStartFinal.status)"

    Write-Host "PASS: Six independent roots and Executor shells overlap; cancellation/failure isolation; queued cancellation; automatic same-chat wake; restart/recovery without duplicate shell commands."
}
finally {
    if ($null -ne $agent -and -not $agent.HasExited) {
        Stop-Process -Id $agent.Id -Force
        $agent.WaitForExit()
    }
    if (-not $api.HasExited) { Stop-Process -Id $api.Id -Force; $api.WaitForExit() }
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
