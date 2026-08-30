$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$sourceConfig = Join-Path $PSScriptRoot "mezhs.agent.test.yaml"
$configPath = Join-Path $PSScriptRoot "mezhs.agent.runtime.test.yaml"
$dataPath = Join-Path $PSScriptRoot "data-agent-runtime-test"
$apiOut = Join-Path $PSScriptRoot "agent-runtime-api.out.log"
$apiErr = Join-Path $PSScriptRoot "agent-runtime-api.err.log"
$agentOut = Join-Path $PSScriptRoot "agent-runtime-agent.out.log"
$agentErr = Join-Path $PSScriptRoot "agent-runtime-agent.err.log"

foreach ($path in @($configPath, $apiOut, $apiErr, $agentOut, $agentErr)) {
    Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
}
Remove-Item -LiteralPath $dataPath -Recurse -Force -ErrorAction SilentlyContinue

$config = Get-Content -LiteralPath $sourceConfig -Raw
$config = $config.Replace("http://127.0.0.1:5198", "http://127.0.0.1:5218")
$config = $config.Replace("http://127.0.0.1:5199", "http://127.0.0.1:5219")
$config = $config.Replace("data-agent-test/mezhs", "data-agent-runtime-test/mezhs")
$config = $config.Replace("data-agent-test/agent.sqlite", "data-agent-runtime-test/agent.sqlite")
Set-Content -LiteralPath $configPath -Value $config -Encoding UTF8

function Wait-Health([string]$uri) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    do {
        try {
            $health = Invoke-RestMethod -Uri $uri
            if ($health.status -eq "ok") { return }
        }
        catch {
            if ([DateTimeOffset]::UtcNow -ge $deadline) { throw }
        }
        Start-Sleep -Milliseconds 150
    } while ($true)
}

function Start-AgentExecution([string]$input, [string]$chatId = "") {
    $body = @{
        policyId = "test"
        input = $input
    }
    if ($chatId) { $body.chatId = $chatId }
    return Invoke-RestMethod `
        -Method Post `
        -Uri "http://127.0.0.1:5219/v1/executions" `
        -ContentType "application/json" `
        -Body (ConvertTo-Json $body)
}

function Wait-AgentExecution([string]$executionId) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    do {
        $execution = Invoke-RestMethod -Uri "http://127.0.0.1:5219/v1/executions/$executionId"
        if ($execution.status -eq "Completed") { return $execution }
        if ($execution.status -in @("Failed", "Cancelled", "Interrupted")) {
            throw "Execution $executionId ended as $($execution.status): $($execution.error)"
        }
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            throw "Execution $executionId timed out."
        }
        Start-Sleep -Milliseconds 100
    } while ($true)
}

$api = Start-Process -FilePath "dotnet" `
    -ArgumentList @("run", "--project", (Join-Path $root "src\Mezhs.Api\Mezhs.Api.csproj"), "-c", "Release", "--no-build", "--", "--config", $configPath) `
    -WorkingDirectory $root `
    -RedirectStandardOutput $apiOut `
    -RedirectStandardError $apiErr `
    -WindowStyle Hidden `
    -PassThru
$agent = $null

try {
    Wait-Health "http://127.0.0.1:5218/health"

    $agent = Start-Process -FilePath "dotnet" `
        -ArgumentList @("run", "--project", (Join-Path $root "src\Mezhs.Agent.Api\Mezhs.Agent.Api.csproj"), "-c", "Release", "--no-build", "--", "--config", $configPath) `
        -WorkingDirectory $root `
        -RedirectStandardOutput $agentOut `
        -RedirectStandardError $agentErr `
        -WindowStyle Hidden `
        -PassThru
    Wait-Health "http://127.0.0.1:5219/health"

    $first = Start-AgentExecution "first transcript task"
    $firstExecution = Wait-AgentExecution $first.executionId
    if (-not $firstExecution.chatId) { throw "First execution did not attach a chat." }

    $messages = @(Invoke-RestMethod -Uri "http://127.0.0.1:5219/v1/agent-chats/$($firstExecution.chatId)/messages")
    $firstRequest = $messages | Where-Object { $_.role -eq "user" } | Select-Object -First 1
    $firstReply = $messages | Where-Object { $_.role -eq "assistant" } | Select-Object -First 1
    if ($firstRequest.origin -ne "human") {
        throw "Manual agent task provenance was '$($firstRequest.origin)' instead of 'human'."
    }
    if ($firstReply.origin -ne "assistant") {
        throw "Assistant provenance was '$($firstReply.origin)' instead of 'assistant'."
    }
    if ([regex]::Matches($firstRequest.content, "Policy instructions:").Count -ne 1) {
        throw "First agent prompt did not contain exactly one policy bootstrap."
    }
    if ($firstRequest.content -notmatch "Host shell: cmd.exe on Windows") {
        throw "First agent prompt did not identify the Windows host shell."
    }
    if ($firstRequest.content -notmatch "Do not wrap it in another shell invocation") {
        throw "First agent prompt did not explain that SH already runs inside the host shell."
    }

    $second = Start-AgentExecution "second transcript task" $firstExecution.chatId
    $null = Wait-AgentExecution $second.executionId
    $messages = @(Invoke-RestMethod -Uri "http://127.0.0.1:5219/v1/agent-chats/$($firstExecution.chatId)/messages")
    $humanRequests = @($messages | Where-Object { $_.role -eq "user" -and $_.origin -eq "human" })
    if ($humanRequests.Count -ne 2) {
        throw "Expected two human-origin task messages after continuation, got $($humanRequests.Count)."
    }
    $secondRequest = $humanRequests[-1]
    if ($secondRequest.content -notmatch "second transcript task") {
        throw "Continuation did not contain the second task."
    }
    if ($secondRequest.content -match "Policy instructions:" -or $secondRequest.content -match "Host shell:") {
        throw "Policy/bootstrap instructions were injected again on an existing agent chat."
    }

    $shellTask = "<SH`necho provenance-ok`nSH>"
    $third = Start-AgentExecution $shellTask $firstExecution.chatId
    $null = Wait-AgentExecution $third.executionId
    $messages = @(Invoke-RestMethod -Uri "http://127.0.0.1:5219/v1/agent-chats/$($firstExecution.chatId)/messages")
    $commandResults = @($messages | Where-Object { $_.role -eq "user" -and $_.origin -eq "command-result" })
    if ($commandResults.Count -lt 1) {
        throw "Shell round-trip did not persist command-result provenance."
    }
    $commandResult = $commandResults[-1]
    if ($commandResult.content -notmatch "untrusted command output" -or
        $commandResult.content -notmatch "Command results JSON:") {
        throw "Command results were not framed as untrusted structured data."
    }
    if ($commandResult.content -match "^Agent command results:") {
        throw "Legacy command-result framing is still in use."
    }

    $executions = @(Invoke-RestMethod -Uri "http://127.0.0.1:5219/v1/agent-chats/$($firstExecution.chatId)/executions")
    $shell = $executions | Where-Object { $_.kind -eq "Shell" } | Select-Object -First 1
    if ($null -eq $shell -or $shell.status -ne "Completed" -or $shell.result -notmatch "provenance-ok") {
        throw "Shell execution evidence was not persisted correctly."
    }
    if ($shell.request.Trim() -ne "echo provenance-ok") {
        throw "Shell command text was rewritten before execution: '$($shell.request)'"
    }

    Write-Host "PASS: agent transcript provenance, one-time policy bootstrap, host-shell context, and command-result framing are correct."
}
finally {
    if ($null -ne $agent -and -not $agent.HasExited) {
        Stop-Process -Id $agent.Id -Force
        $agent.WaitForExit()
    }
    if (-not $api.HasExited) {
        Stop-Process -Id $api.Id -Force
        $api.WaitForExit()
    }
    foreach ($path in @($configPath, $apiOut, $apiErr, $agentOut, $agentErr)) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $dataPath -Recurse -Force -ErrorAction SilentlyContinue
}
