$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('mezhs-agent-protocol-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
$agentConfig = Join-Path $temp 'mezhs.agent.test.yaml'
$agentOut = Join-Path $temp 'agent.out.log'
$agentErr = Join-Path $temp 'agent.err.log'

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$base = "http://127.0.0.1:$port"
$electronPath = (Join-Path $root 'electron').Replace('\', '/')
$workspacePath = $root.Replace('\', '/')
$tempPath = $temp.Replace('\', '/')
$config = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'mezhs.agent.test.yaml') -Raw).
    Replace('http://127.0.0.1:5199', $base).
    Replace('electronDirectory: ../electron', "electronDirectory: $electronPath").
    Replace('root: data-agent-test/core', "root: $tempPath/core").
    Replace('agentStorage: data-agent-test/agent.sqlite', "agentStorage: $tempPath/agent.sqlite").
    Replace('workspace: ..', "workspace: $workspacePath")
Set-Content -LiteralPath $agentConfig -Value $config -Encoding UTF8

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
        $execution = Invoke-RestMethod -Uri "$base/v1/executions/$executionId"
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
static void Check(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException(message);
}
static void Reject(Parser parser, string content, string message)
{
    try { parser.Parse(content); }
    catch (CommandParseException) { return; }
    throw new InvalidOperationException(message);
}
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

// Historical raw ChatGPT Account replies ended with this exact escaped line.
// The presentation wrapper carries no executable command intent.
const string observed = "**Final status: Implementation complete, committed, pushed, and locally verified.**\n\n<escape><DONE></escape>";
var escapedDone = parser.Parse(observed);
if (escapedDone.Commands.Count != 0 || escapedDone.VisibleContent != observed)
    throw new InvalidOperationException("Historical escaped DONE must remain visible and cannot complete.");

var literalExamples = parser.Parse("<escape><DONE></escape>\n<escape><SH></escape>\n<escape><ATTACH></escape>");
if (literalExamples.Commands.Count != 0 ||
    literalExamples.VisibleContent != "<escape><DONE></escape>\n<escape><SH></escape>\n<escape><ATTACH></escape>")
    throw new InvalidOperationException("All escaped command examples must be inert.");

var multilineEscape = "<escape>\n<SH>\necho NEVER_EXECUTE\n</SH>\n<ATTACH>\nC:/never-attach\n</ATTACH>\n<DONE>\n</escape>\n<DONE>";
var multiline = parser.Parse(multilineEscape);
if (multiline.Commands.Count != 1 || multiline.Commands[0] != new Command("DONE", null) ||
    multiline.VisibleContent != multilineEscape[..multilineEscape.LastIndexOf("\n<DONE>", StringComparison.Ordinal)])
    throw new InvalidOperationException("Multiline escaped examples must be inert; trailing raw DONE must complete.");

var escapedWithAttribute = parser.Parse("<escape char=\"\\\\\">\n<SH>\necho NEVER_EXECUTE\n</SH>\n</escape>");
if (escapedWithAttribute.Commands.Count != 0 ||
    escapedWithAttribute.VisibleContent != "<escape char=\"\\\\\">\n<SH>\necho NEVER_EXECUTE\n</SH>\n</escape>")
    throw new InvalidOperationException("Escaped presentation markup with attributes must be inert.");

var inline = parser.Parse("Explanation: <escape><DONE></escape>");
if (inline.Commands.Count != 0 || inline.VisibleContent != "Explanation: <escape><DONE></escape>")
    throw new InvalidOperationException("Inline escaped DONE must remain visible text.");

var opaqueBody = parser.Parse("<SH>\necho literal\n<escape><DONE></escape>\n<SH>\n</SH>");
if (opaqueBody.Commands.Count != 1 ||
    opaqueBody.Commands[0].Body != "echo literal\n<escape><DONE></escape>\n<SH>")
    throw new InvalidOperationException("Shell body text must remain opaque.");

var attach = parser.Parse("<ATTACH>\nC:/tmp/safe.txt\n<DONE>\n<SH>\n</ATTACH>");
if (attach.Commands.Count != 1 || attach.Commands[0] !=
    new Command("ATTACH", "C:/tmp/safe.txt\n<DONE>\n<SH>"))
    throw new InvalidOperationException("ATTACH body must remain opaque.");

Console.WriteLine("PASS: Raw DONE, SH, ATTACH, and SH+DONE are recognized with opaque bodies.");
Console.WriteLine("PASS: Historical escaped terminal DONE, all literal examples, and multiline escape markup remain inert.");

const string displayMarkup = "<box border radius=\"lg\" padding={3} gap={2}>\n<row align=\"center\" gap={2}>\n<badge color=\"success\">OK</badge>\n</row>\n<text color=\"secondary\">Visible</text>\n</box>";
var formatted = parser.Parse(displayMarkup);
if (formatted.Commands.Count != 0 || formatted.VisibleContent != displayMarkup)
    throw new InvalidOperationException("Recorded <box> presentation markup was treated as a command.");

Reject(parser, "<SH extra=\"true\">", "Malformed uppercase SH with attributes was accepted.");
Reject(parser, "<DONE/>", "Malformed self-closing DONE was accepted.");
Reject(parser, "</SH>", "Unexpected closing SH was accepted.");
Reject(parser, "<SH>\necho outer\n<SH>\necho nested\n</SH>\n</SH>",
    "Nested SH block with an extra closing tag was accepted.");
var unfinished = parser.Parse("<SH>\necho incomplete");
if (unfinished.Commands.Count != 1 || unfinished.Commands[0] != new Command("SH", null))
    throw new InvalidOperationException("Unclosed SH must remain incomplete for Interpreter to reject.");
Check(!Registry.TryGet("UNKNOWN", out _) && Registry.TryGet("SH", out var shell) &&
      shell.Form == CommandForm.Block, "Existing Registry must remain command authority.");

Console.WriteLine("PASS: Observed <box>, <row>, <text>, <badge> markup is inert.");
Console.WriteLine("PASS: Malformed uppercase commands, unexpected closings, and nested SH are rejected.");
'@ | Set-Content -LiteralPath (Join-Path $temp 'Program.cs') -Encoding UTF8

    dotnet run --project (Join-Path $temp 'ParserTest.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Opaque SH parser regression test failed.' }

    $published = Join-Path $temp 'published'
    dotnet publish $agentProject -c Release --no-restore --nologo -v quiet -o $published
    if ($LASTEXITCODE -ne 0) { throw 'Isolated Agent publish failed.' }
    $agentDll = Join-Path $published 'Mezhs.Agent.Api.dll'
    $agent = Start-Process -FilePath 'dotnet' `
        -ArgumentList @($agentDll, '--config', $agentConfig) `
        -WorkingDirectory $root -RedirectStandardOutput $agentOut -RedirectStandardError $agentErr -WindowStyle Hidden -PassThru
    Wait-Health "$base/health"

    function Assert-RejectedBatch([string]$label, [string]$suffix) {
        $marker = Join-Path $temp "rejected-$label.txt"
        $shellText = "echo EXECUTED>>`"$marker`""
        $taskText = "<SH>`n$shellText`n</SH>`n$suffix"
        $run = Invoke-RestMethod -Method Post -Uri "$base/v1/executions" `
            -ContentType 'application/json' -Body (ConvertTo-Json @{ policyId = 'test'; input = $taskText })
        $outcome = Wait-Execution $run.executionId 20
        if ($outcome.status -ne 'Completed') {
            throw "Rejected batch $label did not finish: $($outcome.status), $($outcome.error)"
        }
        $history = @(Invoke-RestMethod -Uri "$base/v1/agent-chats/$($outcome.chatId)/executions")
        $shells = @($history | ForEach-Object { $_ } | Where-Object { $_.kind -eq 'Shell' })
        if ((Test-Path -LiteralPath $marker) -or $shells.Count -ne 0) {
            throw "Rejected batch $label ran an earlier shell command. Shell count=$($shells.Count), marker=$(Test-Path -LiteralPath $marker)"
        }
        Write-Host "PASS: Rejected $label batch produced no shell execution or filesystem side effect."
    }

    Assert-RejectedBatch 'unknown' '<UNKNOWN>'
    Assert-RejectedBatch 'unclosed' '<SH>'
    Assert-RejectedBatch 'empty-shell' "<SH>`n `n</SH>"
    Assert-RejectedBatch 'disallowed' "<ATTACH>`n$($temp.Replace('\', '/'))/nonexistent.txt`n</ATTACH>"
    Assert-RejectedBatch 'done-order' "<DONE>`n<SH>`necho LATER`n</SH>"
    Assert-RejectedBatch 'done-body' "<DONE>`nbody`n</DONE>"

    $orderedFile = Join-Path $temp 'ordered-batch.txt'
    $orderedTask = "<SH>`necho FIRST>>`"$orderedFile`"`n</SH>`n<SH>`necho SECOND>>`"$orderedFile`"`n</SH>`n<DONE>"
    $orderedCreated = Invoke-RestMethod -Method Post -Uri "$base/v1/executions" `
        -ContentType 'application/json' -Body (ConvertTo-Json @{ policyId = 'test-evidence'; input = $orderedTask })
    $orderedResult = Wait-Execution $orderedCreated.executionId 20
    if ($orderedResult.status -ne 'Completed') {
        throw "Valid ordered SH + SH + DONE failed: $($orderedResult.status), $($orderedResult.error)"
    }
    $orderedLines = @(Get-Content -LiteralPath $orderedFile)
    if ($orderedLines.Count -ne 2 -or $orderedLines[0] -ne 'FIRST' -or $orderedLines[1] -ne 'SECOND') {
        throw "Valid batch did not run exactly once in order: $($orderedLines -join ', ')"
    }
    $orderedHistory = @(Invoke-RestMethod -Uri "$base/v1/agent-chats/$($orderedResult.chatId)/executions")
    $orderedShells = @($orderedHistory | ForEach-Object { $_ } | Where-Object { $_.kind -eq 'Shell' } | Sort-Object commandIndex)
    if ($orderedShells.Count -ne 2 -or $orderedShells[0].commandIndex -ne 0 -or
        $orderedShells[1].commandIndex -ne 1 -or
        $orderedShells[0].executionId -eq $orderedShells[1].executionId -or
        @($orderedShells | Where-Object { $_.status -ne 'Completed' }).Count -ne 0) {
        throw "Valid batch lost shell ordering, unique durable IDs, or success: $($orderedHistory | ConvertTo-Json -Depth 6 -Compress)"
    }
    Write-Host 'PASS: Valid two-shell batch executes exactly once in order with durable IDs and command indices.'

    $partialFile = Join-Path $temp 'partial-results.txt'
    $firstShell = 'echo EARLIER>>' + [char]34 + $partialFile + [char]34
    $partialTask = '<SH>' + [char]10 + $firstShell + [char]10 + '</SH>' + [char]10 + '<SH>' + [char]10 + 'exit /b 37' + [char]10 + '</SH>'
    $partialCreated = Invoke-RestMethod -Method Post -Uri ($base + '/v1/executions') -ContentType 'application/json' -Body (ConvertTo-Json @{ policyId = 'test-evidence-auto'; input = $partialTask })
    $partialResult = Wait-Execution $partialCreated.executionId 20
    if ($partialResult.status -ne 'Completed') { throw 'Failure-result batch did not finish.' }
    $partialLines = @(Get-Content -LiteralPath $partialFile)
    $partialHistory = @(Invoke-RestMethod -Uri ($base + '/v1/agent-chats/' + $partialResult.chatId + '/executions'))
    $partialShells = @($partialHistory | ForEach-Object { $_ } | Where-Object { $_.kind -eq 'Shell' } | Sort-Object commandIndex)
    if ($partialLines.Count -ne 1 -or $partialShells.Count -ne 2 -or $partialShells[0].status -ne 'Completed' -or $partialShells[1].status -ne 'Failed') {
        throw 'Partial failure lost exactly-once execution or durable shell records.'
    }
    $partialMessages = @(Invoke-RestMethod -Uri ($base + '/v1/agent-chats/' + $partialResult.chatId + '/messages'))
    $partialPrompts = @($partialMessages | ForEach-Object { $_ } | Where-Object { $_.role -eq 'user' -and $_.content -match 'Command results JSON:' })
    if ($partialPrompts.Count -ne 1 -or -not $partialPrompts[0].content.Contains($partialShells[0].executionId) -or
        -not $partialPrompts[0].content.Contains($partialShells[1].executionId)) {
        throw 'Partial failure did not communicate both actual shell results and durable IDs to the agent.'
    }
    Write-Host 'PASS: Later shell failure preserves earlier result and communicates both durable execution IDs.'

    $task = @'
<SH>
echo SAME_TURN_DONE_OK
</SH>
VISIBLE_FINAL_RESULT
<DONE>
'@
    $created = Invoke-RestMethod -Method Post -Uri "$base/v1/executions" `
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

    $messages = @(Invoke-RestMethod -Uri "$base/v1/agent-chats/$($completed.chatId)/messages")
    $assistantMessages = @($messages | ForEach-Object { $_ } | Where-Object { $_.role -eq 'assistant' })
    if ($assistantMessages.Count -ne 1) {
        throw "SH + DONE required another assistant turn instead of completing immediately. Assistant turns=$($assistantMessages.Count)"
    }

    $executions = @(Invoke-RestMethod -Uri "$base/v1/agent-chats/$($completed.chatId)/executions")
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
    $continuedCreated = Invoke-RestMethod -Method Post -Uri "$base/v1/executions" `
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

    $continuedMessages = @(Invoke-RestMethod -Uri "$base/v1/agent-chats/$($continued.chatId)/messages")
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
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}