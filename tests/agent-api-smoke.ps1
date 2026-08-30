$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

$root = Split-Path -Parent $PSScriptRoot
$configPath = Join-Path $PSScriptRoot "mezhs.agent.test.yaml"
$invalidConfigPath = Join-Path $PSScriptRoot "mezhs.agent.invalid.test.yaml"
$dataPath = Join-Path $PSScriptRoot "data-agent-test"
$apiOut = Join-Path $PSScriptRoot "agent-smoke-api.out.log"
$apiErr = Join-Path $PSScriptRoot "agent-smoke-api.err.log"
$agentOut = Join-Path $PSScriptRoot "agent-smoke-agent.out.log"
$agentErr = Join-Path $PSScriptRoot "agent-smoke-agent.err.log"
$webOut = Join-Path $PSScriptRoot "agent-smoke-web.out.log"
$webErr = Join-Path $PSScriptRoot "agent-smoke-web.err.log"
$invalidOut = Join-Path $PSScriptRoot "agent-smoke-invalid.out.log"
$invalidErr = Join-Path $PSScriptRoot "agent-smoke-invalid.err.log"

$processPath = $env:Path
Remove-Item Env:PATH -ErrorAction SilentlyContinue
$env:Path = $processPath

if (Test-Path -LiteralPath $dataPath) {
    Remove-Item -LiteralPath $dataPath -Recurse -Force
}
foreach ($path in @(
    $invalidConfigPath,
    $apiOut,
    $apiErr,
    $agentOut,
    $agentErr,
    $webOut,
    $webErr,
    $invalidOut,
    $invalidErr
)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
    }
}

$invalidConfig = (Get-Content -LiteralPath $configPath -Raw).Replace(
    "requireDone: false",
    "requireDon: false")
Set-Content -LiteralPath $invalidConfigPath -Value $invalidConfig -Encoding UTF8
$invalidAgent = Start-Process -FilePath "dotnet" `
    -ArgumentList @("run", "--project", (Join-Path $root "src\Mezhs.Agent.Api\Mezhs.Agent.Api.csproj"), "-c", "Release", "--no-build", "--", "--config", $invalidConfigPath) `
    -WorkingDirectory $root `
    -RedirectStandardOutput $invalidOut `
    -RedirectStandardError $invalidErr `
    -WindowStyle Hidden `
    -PassThru
try {
    if (-not $invalidAgent.WaitForExit(5000)) {
        Stop-Process -Id $invalidAgent.Id -Force
        $invalidAgent.WaitForExit()
        throw "Agent accepted an unknown policy property instead of rejecting the configuration."
    }
    $invalidErrorText = Get-Content -LiteralPath $invalidErr -Raw
    if ($invalidAgent.ExitCode -eq 0 -or $invalidErrorText -notmatch "requireDon") {
        throw "Agent did not fail specifically on the unknown policy property. Error: $invalidErrorText"
    }
}
finally {
    if (-not $invalidAgent.HasExited) {
        Stop-Process -Id $invalidAgent.Id -Force
        $invalidAgent.WaitForExit()
    }
    Remove-Item -LiteralPath $invalidConfigPath -Force -ErrorAction SilentlyContinue
}

$api = Start-Process -FilePath "dotnet" `
    -ArgumentList @("run", "--project", (Join-Path $root "src\Mezhs.Api\Mezhs.Api.csproj"), "-c", "Release", "--no-build", "--", "--config", $configPath) `
    -WorkingDirectory $root `
    -RedirectStandardOutput $apiOut `
    -RedirectStandardError $apiErr `
    -WindowStyle Hidden `
    -PassThru

$agent = $null
$agentWeb = $null
try {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    do {
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:5198/health"
            break
        }
        catch {
            if ([DateTimeOffset]::UtcNow -ge $deadline) { throw }
            Start-Sleep -Milliseconds 200
        }
    } while ($true)
    if ($health.status -ne "ok") { throw "Generic MEŽS API health check failed." }

    $agent = Start-Process -FilePath "dotnet" `
        -ArgumentList @("run", "--project", (Join-Path $root "src\Mezhs.Agent.Api\Mezhs.Agent.Api.csproj"), "-c", "Release", "--no-build", "--", "--config", $configPath) `
        -WorkingDirectory $root `
        -RedirectStandardOutput $agentOut `
        -RedirectStandardError $agentErr `
        -WindowStyle Hidden `
        -PassThru

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    do {
        try {
            $runtime = Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/runtime"
            break
        }
        catch {
            if ([DateTimeOffset]::UtcNow -ge $deadline) { throw }
            Start-Sleep -Milliseconds 200
        }
    } while ($true)
    if (-not $runtime.mezhsApiHealthy) {
        throw "Agent API cannot reach the generic MEŽS API."
    }

    $agentMetadata = Invoke-RestMethod -Uri "http://127.0.0.1:5199/"
    if ($agentMetadata.name -ne "MEŽS Agent") {
        throw "Agent API root does not expose API metadata."
    }

    $agentWeb = Start-Process -FilePath "dotnet" `
        -ArgumentList @(
            "run",
            "--project", (Join-Path $root "src\Mezhs.Agent.Web\Mezhs.Agent.Web.csproj"),
            "-c", "Release",
            "--no-build",
            "--no-launch-profile",
            "--",
            "--urls", "http://127.0.0.1:5200",
            "--Agent:BaseUrl", "http://127.0.0.1:5199"
        ) `
        -WorkingDirectory $root `
        -RedirectStandardOutput $webOut `
        -RedirectStandardError $webErr `
        -WindowStyle Hidden `
        -PassThru

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    do {
        try {
            $dashboard = Invoke-WebRequest -Uri "http://127.0.0.1:5200/"
            if ($dashboard.StatusCode -eq 200) { break }
        }
        catch {
            if ([DateTimeOffset]::UtcNow -ge $deadline) { throw }
            Start-Sleep -Milliseconds 200
        }
    } while ($true)
    if ($dashboard.Content -notmatch '<title>MEŽS Agent</title>') {
        throw "Mezhs.Agent.Web did not serve the Agent dashboard."
    }
    $proxiedRuntime = Invoke-RestMethod -Uri "http://127.0.0.1:5200/v1/runtime"
    if (-not $proxiedRuntime.mezhsApiHealthy) {
        throw "Mezhs.Agent.Web did not forward /v1 to Mezhs.Agent.Api."
    }

    $policy = Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/policies/test"
    if ($policy.modelInstructions -notlike "*deterministic test task*" -or
        $policy.modelInstructions -notlike "*<SH*") {
        throw "Compiled policy did not expose task/shell model instructions."
    }
    if ($policy.snapshot -notmatch "requireDone: false" -or
        $policy.snapshot -notmatch "maxTurns: 3" -or
        $policy.snapshot -notmatch "SH") {
        throw "Compiled policy snapshot does not contain normalized effective command/completion/limit rules."
    }

    $created = Invoke-RestMethod `
        -Method Post `
        -Uri "http://127.0.0.1:5199/v1/executions" `
        -ContentType "application/json" `
        -Body (ConvertTo-Json @{
            policyId = "test"
            input = "hello agent"
        })
    if (-not $created.executionId) { throw "Execution creation returned no executionId." }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    do {
        $execution = Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/executions/$($created.executionId)"
        if ($execution.status -eq "Completed") { break }
        if ($execution.status -in @("Failed", "Cancelled", "Interrupted")) {
            throw "Agent execution ended as $($execution.status): $($execution.error)"
        }
        if ([DateTimeOffset]::UtcNow -ge $deadline) { throw "Agent execution polling timed out." }
        Start-Sleep -Milliseconds 100
    } while ($true)

    if (-not $execution.chatId) { throw "Completed execution has no MEŽS chatId." }
    if ($execution.result -notlike "Echo:*hello agent*") {
        throw "Agent did not route its task through the mock MEŽS integration."
    }
    if ($execution.policySnapshot -notmatch "requireDone: false" -or
        $execution.policySnapshot -notmatch "maxTurns: 3") {
        throw "Execution did not retain the compiled effective policy snapshot."
    }

    $doneRequired = Invoke-RestMethod `
        -Method Post `
        -Uri "http://127.0.0.1:5199/v1/executions" `
        -ContentType "application/json" `
        -Body (ConvertTo-Json @{
            policyId = "test-done"
            input = "reply without DONE command"
        })
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    do {
        $doneExecution = Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/executions/$($doneRequired.executionId)"
        if ($doneExecution.status -in @("Completed", "Failed", "Cancelled", "Interrupted")) { break }
        if ([DateTimeOffset]::UtcNow -ge $deadline) { throw "DONE-policy execution polling timed out." }
        Start-Sleep -Milliseconds 100
    } while ($true)
    if ($doneExecution.status -ne "Failed" -or $doneExecution.error -notlike "*limit of 1 turns*") {
        throw "Compiled <DONE>/turn policy was not enforced by the runtime."
    }

    $chat = Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/agent-chats/$($execution.chatId)"
    if ($chat.policyId -ne "test" -or $chat.originSource -ne "manual" -or $chat.paused) {
        throw "Agent chat policy/source/pause metadata was not persisted correctly."
    }
    if ($chat.title -notlike "*hello agent*") {
        throw "Agent chat API did not expose the underlying MEŽS chat title."
    }
    $messages = Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/agent-chats/$($execution.chatId)/messages"
    if (@($messages).Count -lt 2) {
        throw "Agent chat API did not expose the underlying MEŽS conversation."
    }

    $client = [Net.Http.HttpClient]::new()
    try {
        $conflictBody = ConvertTo-Json @{
            policyId = "test-alt"
            chatId = $execution.chatId
            input = "policy conflict"
        }
        $content = [Net.Http.StringContent]::new(
            $conflictBody,
            [Text.Encoding]::UTF8,
            "application/json")
        try {
            $response = $client.PostAsync(
                "http://127.0.0.1:5199/v1/executions",
                $content).GetAwaiter().GetResult()
            $responseBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ([int]$response.StatusCode -ne 400 -or $responseBody -notlike "*already owned by policy 'test'*") {
                throw "Expected policy ownership conflict, got HTTP $([int]$response.StatusCode): $responseBody"
            }
        }
        finally {
            $content.Dispose()
            if ($null -ne $response) { $response.Dispose() }
        }

        $pauseBody = ConvertTo-Json @{ paused = $true }
        $pauseContent = [Net.Http.StringContent]::new(
            $pauseBody,
            [Text.Encoding]::UTF8,
            "application/json")
        try {
            $pauseResponse = $client.PatchAsync(
                "http://127.0.0.1:5199/v1/agent-chats/$($execution.chatId)",
                $pauseContent).GetAwaiter().GetResult()
            $pauseResponseBody = $pauseResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ([int]$pauseResponse.StatusCode -ne 200 -or $pauseResponseBody -notmatch '"paused":true') {
                throw "Agent chat could not be paused: HTTP $([int]$pauseResponse.StatusCode): $pauseResponseBody"
            }
        }
        finally {
            $pauseContent.Dispose()
            if ($null -ne $pauseResponse) { $pauseResponse.Dispose() }
        }

        $pausedExecutionBody = ConvertTo-Json @{
            policyId = "test"
            chatId = $execution.chatId
            input = "must be rejected while paused"
        }
        $pausedExecutionContent = [Net.Http.StringContent]::new(
            $pausedExecutionBody,
            [Text.Encoding]::UTF8,
            "application/json")
        try {
            $pausedExecutionResponse = $client.PostAsync(
                "http://127.0.0.1:5199/v1/executions",
                $pausedExecutionContent).GetAwaiter().GetResult()
            $pausedExecutionResponseBody = $pausedExecutionResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ([int]$pausedExecutionResponse.StatusCode -ne 400 -or $pausedExecutionResponseBody -notlike "*is paused*") {
                throw "Paused chat accepted execution: HTTP $([int]$pausedExecutionResponse.StatusCode): $pausedExecutionResponseBody"
            }
        }
        finally {
            $pausedExecutionContent.Dispose()
            if ($null -ne $pausedExecutionResponse) { $pausedExecutionResponse.Dispose() }
        }

        $resumeBody = ConvertTo-Json @{ paused = $false }
        $resumeContent = [Net.Http.StringContent]::new(
            $resumeBody,
            [Text.Encoding]::UTF8,
            "application/json")
        try {
            $resumeResponse = $client.PatchAsync(
                "http://127.0.0.1:5199/v1/agent-chats/$($execution.chatId)",
                $resumeContent).GetAwaiter().GetResult()
            $resumeResponseBody = $resumeResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ([int]$resumeResponse.StatusCode -ne 200 -or $resumeResponseBody -notmatch '"paused":false') {
                throw "Agent chat could not be resumed: HTTP $([int]$resumeResponse.StatusCode): $resumeResponseBody"
            }
        }
        finally {
            $resumeContent.Dispose()
            if ($null -ne $resumeResponse) { $resumeResponse.Dispose() }
        }
    }
    finally {
        $client.Dispose()
    }

    $chatAfterConflict = Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/agent-chats/$($execution.chatId)"
    if ($chatAfterConflict.policyId -ne "test" -or $chatAfterConflict.paused) {
        throw "Rejected policy conflict/pause cycle changed durable agent chat ownership or state."
    }

    $history = Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/agent-chats/$($execution.chatId)/executions"
    if (@($history).Count -ne 1 -or $history[0].executionId -ne $execution.executionId) {
        throw "Agent execution history did not round-trip through SQLite."
    }

    $sqlitePath = Join-Path $dataPath "agent.sqlite"
    if (-not (Test-Path -LiteralPath $sqlitePath)) {
        throw "Agent SQLite database was not created."
    }

    Write-Host "PASS: Agent API and Agent Web run separately, Agent Web proxies the API, and fixed policy/source/pause behavior is preserved."
}
finally {
    if ($null -ne $agentWeb -and -not $agentWeb.HasExited) {
        Stop-Process -Id $agentWeb.Id -Force
        $agentWeb.WaitForExit()
    }
    if ($null -ne $agent -and -not $agent.HasExited) {
        Stop-Process -Id $agent.Id -Force
        $agent.WaitForExit()
    }
    if (-not $api.HasExited) {
        Stop-Process -Id $api.Id -Force
        $api.WaitForExit()
    }
}
