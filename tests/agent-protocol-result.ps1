$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

$root = Split-Path -Parent $PSScriptRoot
$agentConfig = Join-Path $PSScriptRoot 'mezhs.agent.test.yaml'
$dataPath = Join-Path $PSScriptRoot 'data-agent-test'
$agentOut = Join-Path $PSScriptRoot 'agent-protocol-result.out.log'
$agentErr = Join-Path $PSScriptRoot 'agent-protocol-result.err.log'
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('mezhs-agent-protocol-' + [Guid]::NewGuid().ToString('N'))

Remove-Item -LiteralPath $dataPath -Recurse -Force -ErrorAction SilentlyContinue
foreach ($path in @($agentOut, $agentErr)) {
    Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $temp -Force | Out-Null

function Wait-Health([string]$uri) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(25)
    do {
        try {
            $health = Invoke-RestMethod -Uri $uri
            if ($health.status -eq 'ok') { return }
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
        if ($execution.status -in @('Completed', 'Failed', 'Cancelled')) { return $execution }
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            throw "Execution $executionId did not reach a terminal state. Last status: $($execution.status)"
        }
        Start-Sleep -Milliseconds 100
    } while ($true)
}

$agent = $null
try {
    $agentProject = Join-Path $root 'src\Mezhs.Agent.Api\Mezhs.Agent.Api.csproj'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$agentProject" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $temp 'ParserTest.csproj') -Encoding UTF8

    @'
using Mezhs.Agent.Commands;

var parser = new Parser();
const string content = "before\n<SH>\necho first\n<DONE>\n<ABC>\necho last\n</SH>\n<DONE>\nafter";
var parsed = parser.Parse(content);
if (parsed.Commands.Count != 2)
    throw new InvalidOperationException($"Expected SH + DONE, got {parsed.Commands.Count} commands.");
if (parsed.Commands[0].Name != "SH" || parsed.Commands[0].Body != "echo first\n<DONE>\n<ABC>\necho last")
    throw new InvalidOperationException($"SH body was not opaque: '{parsed.Commands[0].Body}'.");
if (parsed.Commands[1].Name != "DONE" || parsed.Commands[1].Body is not null)
    throw new InvalidOperationException("DONE after the SH block was not parsed as the completion marker.");
if (parsed.VisibleContent != "before\nafter")
    throw new InvalidOperationException($"Visible content was wrong: '{parsed.VisibleContent}'.");

try
{
    parser.Parse("<SH>\necho outer\n<SH>\necho nested\n</SH>\n</SH>");
    throw new InvalidOperationException("Nested SH block was accepted.");
}
catch (CommandParseException)
{
}

Console.WriteLine("PASS: Agent parser keeps unrelated tag-like SH text opaque and rejects a complete nested SH block.");
'@ | Set-Content -LiteralPath (Join-Path $temp 'Program.cs') -Encoding UTF8

    dotnet run --project (Join-Path $temp 'ParserTest.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Opaque SH parser regression test failed.' }

    $agentDll = (Resolve-Path (Join-Path $root 'src\Mezhs.Agent.Api\bin\Release\net10.0\Mezhs.Agent.Api.dll')).Path
    $agent = Start-Process -FilePath 'dotnet' `
        -ArgumentList @($agentDll, '--config', $agentConfig) `
        -WorkingDirectory $root -RedirectStandardOutput $agentOut -RedirectStandardError $agentErr -WindowStyle Hidden -PassThru
    Wait-Health 'http://127.0.0.1:5199/health'

    $task = @'
<SH>
echo SAME_TURN_DONE_OK
</SH>
VISIBLE_FINAL_RESULT
<DONE>
'@
    $created = Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:5199/v1/executions' `
        -ContentType 'application/json' -Body (ConvertTo-Json @{ policyId = 'test-evidence'; input = $task })
    $completed = Wait-Execution $created.executionId 20
    if ($completed.status -ne 'Completed') {
        throw "SH + DONE execution did not complete: status=$($completed.status), error=$($completed.error)"
    }
    if ($completed.result -notmatch 'VISIBLE_FINAL_RESULT') {
        throw "Agent durable result lost the visible final conclusion: $($completed.result)"
    }
    if ($completed.result -match 'echo SAME_TURN_DONE_OK') {
        throw "Agent durable result contains executable protocol instead of only visible assistant content: $($completed.result)"
    }

    $messages = @(Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/agent-chats/$($completed.chatId)/messages")
    $assistantMessages = @($messages | ForEach-Object { $_ } | Where-Object { $_.role -eq 'assistant' })
    if ($assistantMessages.Count -ne 1) {
        throw "SH + DONE required another assistant turn instead of completing immediately. Assistant turns=$($assistantMessages.Count)"
    }

    $executions = @(Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/agent-chats/$($completed.chatId)/executions")
    $shells = @($executions | ForEach-Object { $_ } | Where-Object { $_.kind -eq 'Shell' })
    if ($shells.Count -ne 1 -or $shells[0].status -ne 'Completed' -or $shells[0].result -notmatch 'SAME_TURN_DONE_OK') {
        $observed = $executions | ConvertTo-Json -Depth 6 -Compress
        throw "Same-turn completion did not retain successful Executor evidence. Observed: $observed"
    }

    $continuedTask = @'
<SH>
echo CONTINUED_RESULT_OK
</SH>
FIRST_VISIBLE_RESULT
'@
    $continuedCreated = Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:5199/v1/executions' `
        -ContentType 'application/json' -Body (ConvertTo-Json @{ policyId = 'test-evidence-auto'; input = $continuedTask })
    $continued = Wait-Execution $continuedCreated.executionId 20
    if ($continued.status -ne 'Completed') {
        throw "Continued execution did not complete: status=$($continued.status), error=$($continued.error)"
    }
    if ($continued.result -notmatch 'FIRST_VISIBLE_RESULT') {
        throw "Agent durable result lost visible output from the pre-continuation turn: $($continued.result)"
    }
    if ($continued.result -match 'echo CONTINUED_RESULT_OK') {
        throw "Aggregated Agent result contains executable SH protocol: $($continued.result)"
    }

    $continuedMessages = @(Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/agent-chats/$($continued.chatId)/messages")
    $continuedAssistantMessages = @($continuedMessages | ForEach-Object { $_ } | Where-Object { $_.role -eq 'assistant' })
    if ($continuedAssistantMessages.Count -lt 2) {
        throw "Continuation-result regression did not actually cross an assistant command-result turn. Assistant turns=$($continuedAssistantMessages.Count)"
    }

    Write-Host 'PASS: Same-turn completion and command-result continuation both preserve protocol-stripped visible Agent results.'
}
finally {
    if ($null -ne $agent -and -not $agent.HasExited) {
        Stop-Process -Id $agent.Id -Force
        $agent.WaitForExit()
    }
    Remove-Item -LiteralPath $dataPath -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($path in @($agentOut, $agentErr)) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}