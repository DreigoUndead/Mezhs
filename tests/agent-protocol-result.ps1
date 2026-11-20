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
    Replace('agentStorage: data-agent-test/agent.sqlite', "agentStorage: $tempPath/agent.sqlite").
    Replace('workspace: ..', "workspace: $workspacePath")
# This test needs a scripted assistant reply; the normal mock echoes the prompt.
Set-Content -LiteralPath $agentConfig -Value $config -Encoding UTF8
$agentCoreConfig = Join-Path $temp 'core.yaml'
$coreYaml = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'mezhs.agent.core.test.yaml') -Raw).
    Replace('electronDirectory: ../electron', "electronDirectory: $electronPath").
    Replace('root: data-agent-test/core', "root: $tempPath/core")
$coreYaml = [regex]::Replace($coreYaml, '(?m)^(    integration: )mock(?=\r?$)', '${1}mock-agent-scripted')
Set-Content -LiteralPath $agentCoreConfig -Value $coreYaml -Encoding UTF8

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
            if ($execution.chatId) {
                try {
                    $history = @(Invoke-RestMethod -Uri "$base/v1/agent-chats/$($execution.chatId)/messages")
                    Write-Host "TIMEOUT CHAT MESSAGES: $($history.Count)"
                    $history | Select-Object -Last 5 | ForEach-Object { Write-Host ("MESSAGE role={0} status={1} content={2}" -f $_.role, $_.status, ([string]$_.content)) }
                    $actions = @(Invoke-RestMethod -Uri "$base/v1/agent-chats/$($execution.chatId)/executions")
                    $actions | Select-Object -Last 5 | ForEach-Object { Write-Host ("ACTION kind={0} status={1} index={2} result={3}" -f $_.kind, $_.status, $_.commandIndex, $_.result) }
                } catch { Write-Host "Unable to read timeout history: $_" }
            }
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
using Mezhs.Common.RichUi;

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
// Canonical rich tag grammar defines syntax. Agent Registry defines executable
// authority; every executable command must have exactly one canonical tag role.
foreach (var command in Registry.All)
{
    Check(AgentRichTags.Tags.TryGet(command.Name, out var tag) &&
          tag.Role == (command.Form == CommandForm.Block
              ? RichTagRole.Command : RichTagRole.Marker),
          "Canonical rich tags diverged from Agent command definitions.");
}
Check(AgentRichTags.Tags.TryGet("box", out var boxTag) &&
      boxTag.Role == RichTagRole.Container, "Box must be a container node.");
Check(AgentRichTags.Tags.TryGet("CodeBlock", out var codeTag) &&
      codeTag.Role == RichTagRole.RawEditor, "CodeBlock must remain literal raw source.");
Check(AgentRichTags.Tags.TryGet("WritingBlock", out var writingTag) &&
      writingTag.Role == RichTagRole.RawEditor, "WritingBlock must remain literal raw source.");
Check(AgentRichTags.Tags.TryGet("icon", out var iconTag) &&
      iconTag.Role == RichTagRole.Leaf, "Icon cannot own command descendants.");
Check(!AgentRichTags.Tags.TryGet("UNKNOWN", out _), "Unknown tags must not be registered.");

// All supported command forms are tested from the Agent command registry.
foreach (var command in Registry.All)
{
    var body = command.Form == CommandForm.Marker ? null : "registry-payload";
    var source = body is null ? $"<escape><{command.Name}></escape>"
        : $"<escape><{command.Name}>{body}</{command.Name}></escape>";
    var result = parser.Parse(source);
    Check(result.Commands.SequenceEqual([new Command(command.Name, body)]),
        $"Escaped registered command {command.Name} was not recognized.");
}

// All supported paired tags use source-position matching, not line boundaries.
var inline = parser.Parse("prefix <SH>echo INLINE</SH> suffix");
Check(inline.Commands.SequenceEqual([new Command("SH", "echo INLINE")]) &&
      inline.VisibleContent == "prefix  suffix",
      "Inline SH was not extracted without losing surrounding prose.");

// Rich raw editors also use paired-tag matching independent of line position.
// Their nested command-looking text must stay opaque.
var sameLineCode = new RichDocumentParser(AgentRichTags.Tags).Parse(
    "before <CodeBlock language=\"text\">raw <SH>INERT</SH></CodeBlock> after");
Check(sameLineCode.Document.Valid && sameLineCode.Commands.Count == 0 &&
      sameLineCode.Document.Nodes.Any(n => n.Kind == "raw" && n.Name == "CodeBlock" &&
          n.Text == "raw <SH>INERT</SH>"),
      "Same-line CodeBlock was rejected or exposed its opaque shell text.");
var rawBeforeShell = parser.Parse(
    "before <CodeBlock language=\"text\">raw</CodeBlock><SH>echo OK</SH>");
Check(rawBeforeShell.Commands.SequenceEqual([new Command("SH", "echo OK")]) &&
      rawBeforeShell.VisibleContent == "before <CodeBlock language=\"text\">raw</CodeBlock>",
      "Inline CodeBlock prevented a following root command.");

var sameLineWriting = new RichDocumentParser(AgentRichTags.Tags).Parse(
    "before <WritingBlock variant=\"document\">raw <SH>INERT</SH></WritingBlock> after");
Check(sameLineWriting.Document.Valid && sameLineWriting.Commands.Count == 0 &&
      sameLineWriting.Document.Nodes.Any(n => n.Kind == "raw" && n.Name == "WritingBlock" &&
          n.Text == "raw <SH>INERT</SH>"),
      "Same-line WritingBlock was rejected or exposed its opaque shell text.");
var writingBeforeShell = parser.Parse(
    "before <WritingBlock variant=\"document\">raw</WritingBlock><SH>echo OK</SH>");
Check(writingBeforeShell.Commands.SequenceEqual([new Command("SH", "echo OK")]) &&
      writingBeforeShell.VisibleContent == "before <WritingBlock variant=\"document\">raw</WritingBlock>",
      "Inline WritingBlock prevented a following root command.");
var sameLineEscape = new RichDocumentParser(AgentRichTags.Tags).Parse(
    "before <escape>raw <SH>EXECUTED</SH></escape> after");
Check(sameLineEscape.Document.Valid &&
      sameLineEscape.Commands.Select(c => c.Body).SequenceEqual(["EXECUTED"]) &&
      sameLineEscape.Document.Nodes.Any(n => n.Kind == "literal" && n.Text == "raw "),
      "Root escape must discover Agent commands without showing executable content.");

var splitSource = "prefix<escape><SH></escape>\r\necho SPLIT \r\n<escape></SH></escape>suffix";
var splitResult = parser.Parse(splitSource);
var splitDocument = new RichDocumentParser(AgentRichTags.Tags).Parse(splitSource);
Check(splitResult.Commands.Count == 0 && splitResult.VisibleContent == splitSource &&
      splitDocument.Document.Valid && splitDocument.Commands.Count == 0,
      "Incomplete command tags in separate escape wrappers must stay literal.");
var splitWithTags = parser.Parse(
    "<escape><SH></escape>echo <box>literal</box><escape></SH></escape>");
Check(splitWithTags.Commands.Count == 0,
      "Separate escape wrappers cannot construct a command from intervening content.");
var splitAttach = parser.Parse("<escape><ATTACH></escape>file.txt<escape></ATTACH></escape>");
Check(splitAttach.Commands.Count == 0,
      "Incomplete escaped attachment delimiters must remain inert.");
var escapedDone = parser.Parse("<escape><SH>echo OK</SH></escape><escape><DONE></escape>");
Check(escapedDone.Commands.SequenceEqual([new Command("SH", "echo OK"), new Command("DONE", null)]),
      "Escaped DONE marker did not preserve command ordering.");
var adjacentEscapedSource = "<escape><SH>echo FIRST</SH><ATTACH>queued.txt</ATTACH><DONE></escape>";
var adjacentEscaped = parser.Parse(adjacentEscapedSource);
var adjacentEscapedDoc = new RichDocumentParser(AgentRichTags.Tags).Parse(adjacentEscapedSource);
Check(adjacentEscaped.Commands.SequenceEqual([
          new Command("SH", "echo FIRST"), new Command("ATTACH", "queued.txt"),
          new Command("DONE", null)]) &&
      adjacentEscaped.VisibleContent == "" && adjacentEscapedDoc.Document.Valid &&
      adjacentEscapedDoc.Commands.Count == 3 &&
      adjacentEscapedDoc.Commands[0].SourceSpan.Start == 0 &&
      adjacentEscapedDoc.Commands[^1].SourceSpan.End == adjacentEscapedSource.Length,
      "Fully consumed multi-command escape must not leak an empty wrapper into Agent results.");
Check(parser.Parse("<escape><text><SH>NO</SH></text><SH>YES</SH></escape>")
      .Commands.SequenceEqual([new Command("SH", "YES")]),
      "Other tags within escape must not expose their child commands.");
Check(parser.Parse("<box><escape><SH>NO</SH></escape></box>").Commands.Count == 0 &&
      parser.Parse("<escape><escape><SH>NO</SH></escape></escape>").Commands.Count == 0,
      "Nested UI and nested escape cannot authorize root-only commands.");
Check(parser.Parse("<escape><SH></escape>no closing delimiter").Commands.Count == 0,
      "Unmatched escaped command opener must stay inert.");

var multiline = parser.Parse("before\n<SH>\r\necho first\r\n</SH>\n<DONE>\nafter");
Check(multiline.Commands.SequenceEqual([
      new Command("SH", "\r\necho first\r\n"), new Command("DONE", null)]) &&
      multiline.VisibleContent == "before\n\n\nafter",
      "Multiline command bytes or exact visible spans changed.");

var mixed = parser.Parse("<SH>echo A</SH><ATTACH>file.txt</ATTACH><DONE>");
Check(mixed.Commands.SequenceEqual([
      new Command("SH", "echo A"), new Command("ATTACH", "file.txt"),
      new Command("DONE", null)]) && mixed.VisibleContent == "",
      "Adjacent tags or bodyless DONE were not recognized.");

var missing = parser.Parse("Text <SH>never closed");
Check(missing.Commands.Count == 0 &&
      missing.VisibleContent == "Text <SH>never closed",
      "Unmatched SH opener must be ordinary text.");

var unclosedUi = parser.Parse("before <box>unclosed");
Check(unclosedUi.Commands.Count == 0 && unclosedUi.VisibleContent == "before <box>unclosed",
      "Unmatched UI opener must be ordinary text.");

var nestedShell = parser.Parse("<SH>outer <SH>inner</SH>");
Check(nestedShell.Commands.SequenceEqual([new Command("SH", "outer <SH>inner")]),
      "SH is opaque and ends at the first matching closing delimiter.");

var acrossContexts = parser.Parse(
    "<escape><SH>escape</SH></escape>" +
    "<CodeBlock>raw <SH>raw</SH></CodeBlock>" +
    "<text>text <SH>text</SH></text>" +
    "<svg viewBox=\"0 0 10 10\"><SH>svg</SH></svg>" +
    "`<SH>inline-code</SH>`" +
    "<!-- <SH>comment</SH> -->");
Check(acrossContexts.Commands.Select(c => c.Body).SequenceEqual([
      "escape", "inline-code", "comment"]) &&
      !acrossContexts.VisibleContent.Contains("<SH>escape</SH>", StringComparison.Ordinal) &&
      acrossContexts.VisibleContent.Contains("<SH>raw</SH>", StringComparison.Ordinal),
      "Root escape commands must execute; other registered containers remain inert.");

var original = "<SH> cd /d D:\\Projects\\eflex-framework && git status --short --branch && git branch --show-current && type specifications\\planned\\debug-log-files\\plan.md && if exist AGENTS.md type AGENTS.md </SH>";
Check(parser.Parse(original).Commands.Single() ==
      new Command("SH", original[4..^5]),
      "Reported same-line Windows command was not recognized byte for byte.");

Reject(parser, "<DONE><SH>echo NEVER</SH>",
      "DONE may not precede another command.");
Reject(parser, "<DONE><DONE>", "Duplicate DONE must be rejected.");
Reject(parser, "<escape><DONE></escape><SH>echo NEVER</SH>",
      "Escaped DONE must still be final.");
Reject(parser, "<SH>echo NEVER</SH><escape><DONE></escape><DONE>",
      "Duplicate DONE after an escaped marker must fail closed.");
Reject(parser, "<SH extra=\"true\">echo NEVER</SH>",
      "Invalid complete SH attributes must be rejected.");
Reject(parser, "<DONE/>", "Self-closing DONE must be rejected.");
var unknownMarkup = parser.Parse("<widget>pre <SH>echo OK</SH> post</widget>");
Check(unknownMarkup.Commands.Count == 0 &&
      unknownMarkup.VisibleContent == "<widget>pre <SH>echo OK</SH> post</widget>",
      "Unknown wrapper must be a structural parent keeping nested SH inert.");
var unknownXml = parser.Parse("~~~xml\n<widget>example</widget>\n~~~\n<SH>echo OK</SH>");
Check(unknownXml.Commands.SequenceEqual([new Command("SH", "echo OK")]) &&
      unknownXml.VisibleContent == "~~~xml\n<widget>example</widget>\n~~~\n",
      "Unknown XML example must not invalidate independent Agent commands.");
var malformedUnknown = parser.Parse("prose </unknown bad> tail");
Check(malformedUnknown.Commands.Count == 0 &&
      malformedUnknown.VisibleContent == "prose </unknown bad> tail",
      "Malformed unknown closing markup must remain ordinary text.");
var foreignSvg = new RichDocumentParser(AgentRichTags.Tags).Parse("<path d=\"M0 0\"/>");
Check(foreignSvg.Document.Valid && foreignSvg.Commands.Count == 0 &&
      foreignSvg.Document.Nodes.Any(n => n.Kind == "unknown" && n.Name == "path"),
      "Registered SVG geometry outside SVG must render an inert diagnostic.");
Check(!Registry.TryGet("UNKNOWN", out _) && Registry.TryGet("SH", out var shell) &&
      shell.Form == CommandForm.Block, "Registry must remain the only command authority.");
var malformedEditor = parser.Parse("<CodeBlock bad=\"value\"><SH>INERT</SH></CodeBlock><SH>ROOT</SH>");
Check(malformedEditor.Commands.SequenceEqual([new Command("SH", "ROOT")]) &&
      malformedEditor.VisibleContent.Contains("<SH>INERT</SH>", StringComparison.Ordinal),
      "Malformed known opaque control must not promote nested shell authority.");
Console.WriteLine("PASS: Position-independent matching, unknown containment, and exact source spans.");
'@ | Set-Content -LiteralPath (Join-Path $temp 'Program.cs') -Encoding UTF8

    dotnet run --project (Join-Path $temp 'ParserTest.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Opaque SH parser regression test failed.' }

    $published = Join-Path $temp 'published'
    dotnet publish $agentProject -c Release --no-restore --nologo -v quiet -o $published
    if ($LASTEXITCODE -ne 0) { throw 'Isolated Agent publish failed.' }
    $agentDll = Join-Path $published 'Mezhs.Agent.Api.dll'
    $agent = Start-Process -FilePath 'dotnet' `
        -ArgumentList @($agentDll, '--config', $agentConfig, '--mezhs-config', $agentCoreConfig) `
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

    Assert-RejectedBatch 'invalid-command' '<SH bad="evil">echo NEVER</SH>'
    Assert-RejectedBatch 'empty-shell' "<SH>`n `n</SH>"
    Assert-RejectedBatch 'disallowed' "<ATTACH>`n$($temp.Replace('\', '/'))/nonexistent.txt`n</ATTACH>"
    Assert-RejectedBatch 'done-order' "<DONE>`n<SH>`necho LATER`n</SH>"
    Assert-RejectedBatch 'escaped-done-order' '<escape><DONE></escape><SH>echo LATER</SH>'

    # Real Executor proof: root-level matched pairs execute regardless of line placement.
    function Assert-InlineExecuted([string]$label, [string]$prefix, [string]$suffix) {
        $marker = Join-Path $temp "inline-$label.txt"
        $shellText = "echo INLINE>>`"$marker`""
        $task = "$prefix<SH>$shellText</SH>$suffix<DONE>"
        $run = Invoke-RestMethod -Method Post -Uri "$base/v1/executions" -ContentType 'application/json' -Body (ConvertTo-Json @{ policyId = 'test-evidence'; input = $task })
        $outcome = Wait-Execution $run.executionId 20
        $history = @(Invoke-RestMethod -Uri "$base/v1/agent-chats/$($outcome.chatId)/executions")
        $shells = @($history | Where-Object { $_.kind -eq 'Shell' })
        if ($outcome.status -ne 'Completed' -or $shells.Count -ne 1 -or
            $shells[0].status -ne 'Completed' -or -not (Test-Path -LiteralPath $marker)) {
            throw "Position-independent $label did not execute once. Status=$($outcome.status); shells=$($shells.Count)."
        }
        Write-Host "PASS: Inline $label tag executed once with durable shell evidence."
    }
    Assert-InlineExecuted 'prose' 'before ' ' after '
    Assert-InlineExecuted 'markdown-fence' ('```text' + "`n") ("`n" + '```')

    # A complete escaped shell command executes through the real Executor.
    $escapedMarker = Join-Path $temp 'split-escaped-command.txt'
    $escapedShell = "echo ESCAPED>>`"$escapedMarker`""
    $escapedTask = "<escape><SH>$escapedShell</SH></escape><escape><DONE></escape>"
    $escapedRun = Invoke-RestMethod -Method Post -Uri "$base/v1/executions" -ContentType 'application/json' -Body (ConvertTo-Json @{ policyId = 'test-evidence'; input = $escapedTask })
    $escapedOutcome = Wait-Execution $escapedRun.executionId 20
    $escapedHistory = @(Invoke-RestMethod -Uri "$base/v1/agent-chats/$($escapedOutcome.chatId)/executions")
    $escapedShells = @($escapedHistory | Where-Object { $_.kind -eq 'Shell' })
    if ($escapedOutcome.status -ne 'Completed' -or $escapedShells.Count -ne 1 -or
        $escapedShells[0].status -ne 'Completed' -or
        -not (Test-Path -LiteralPath $escapedMarker) -or
        @((Get-Content -LiteralPath $escapedMarker)).Count -ne 1) {
        throw "Complete escaped command did not execute exactly once with durable evidence."
    }
    Write-Host 'PASS: Complete escaped shell command executes once and escaped DONE completes.'

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
    if (Test-Path -LiteralPath $agentOut) {
        Write-Host "AGENT STDOUT (last 50 lines):"
        Get-Content -LiteralPath $agentOut -Tail 50 | ForEach-Object { Write-Host $_ }
    }
    if (Test-Path -LiteralPath $agentErr) {
        Write-Host "AGENT STDERR (last 50 lines):"
        Get-Content -LiteralPath $agentErr -Tail 50 | ForEach-Object { Write-Host $_ }
    }
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}