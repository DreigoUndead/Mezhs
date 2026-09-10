$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$mezhsConfig = Join-Path $PSScriptRoot "mezhs.test.yaml"
$agentConfig = Join-Path $PSScriptRoot "mezhs.agent.test.yaml"
$dataPath = Join-Path $PSScriptRoot "data-agent-test"
$genericDataPath = Join-Path $PSScriptRoot "data"
$apiOut = Join-Path $PSScriptRoot "agent-evidence-api.out.log"
$apiErr = Join-Path $PSScriptRoot "agent-evidence-api.err.log"
$agentOut = Join-Path $PSScriptRoot "agent-evidence-agent.out.log"
$agentErr = Join-Path $PSScriptRoot "agent-evidence-agent.err.log"

foreach ($path in @($dataPath, $genericDataPath)) {
    Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
}
foreach ($path in @($apiOut, $apiErr, $agentOut, $agentErr)) {
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

function Wait-Execution([string]$executionId, [int]$seconds = 20) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($seconds)
    do {
        $execution = Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/executions/$executionId"
        if ($execution.status -in @("Completed", "Failed", "Cancelled", "Interrupted")) { return $execution }
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            throw "Execution $executionId did not reach a terminal state. Last state: $($execution.status)"
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
    $agent = Start-Process -FilePath "dotnet" `
        -ArgumentList @("run", "--project", (Join-Path $root "src\Mezhs.Agent.Api\Mezhs.Agent.Api.csproj"), "-c", "Release", "--no-build", "--", "--config", $agentConfig) `
        -WorkingDirectory $root -RedirectStandardOutput $agentOut -RedirectStandardError $agentErr -WindowStyle Hidden -PassThru
    Wait-Health "http://127.0.0.1:5199/health"

    $task = @"
<SH>
echo EXECUTOR_EVIDENCE_OK
</SH>
"@
    $created = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:5199/v1/executions" `
        -ContentType "application/json" -Body (ConvertTo-Json @{ policyId = "test-evidence-auto"; input = $task })
    $completed = Wait-Execution $created.executionId 25
    if ($completed.status -ne "Completed") {
        throw "Executor shell evidence did not satisfy Agent completion policy. Status=$($completed.status), error=$($completed.error)"
    }

    $history = @(Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/agent-chats/$($completed.chatId)/executions")
    $shells = @($history | Where-Object { $_.kind -eq "Shell" })
    if ($shells.Count -ne 1 -or $shells[0].status -ne "Completed" -or $shells[0].result -notmatch "EXECUTOR_EVIDENCE_OK") {
        throw "Combined execution history does not contain one successful Executor shell row."
    }
    if ($shells[0].parentExecutionId -ne $created.executionId -or $shells[0].correlationId -ne $created.executionId) {
        throw "Executor shell evidence lost Agent parent/correlation identity."
    }

    Write-Host "PASS: Executor shell execution is the durable SH evidence source for Agent completion policy."
}
finally {
    if ($null -ne $agent -and -not $agent.HasExited) { Stop-Process -Id $agent.Id -Force; $agent.WaitForExit() }
    if (-not $api.HasExited) { Stop-Process -Id $api.Id -Force; $api.WaitForExit() }
    Remove-Item -LiteralPath $dataPath -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $genericDataPath -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($path in @($apiOut, $apiErr, $agentOut, $agentErr)) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}
