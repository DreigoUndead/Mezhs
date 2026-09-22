$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$executor = (Resolve-Path (Join-Path $root 'src/Mezhs.Executor/bin/Release/net10.0/Mezhs.Executor.dll')).Path
$data = Join-Path $root 'data/executor-validation.sqlite'
$work = Join-Path $root 'executor-validation-work'
Remove-Item $data,"$data-shm","$data-wal" -Force -ErrorAction SilentlyContinue
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path $work) { throw "Previous executor lifecycle process still owns '$work'." }
New-Item $work -ItemType Directory | Out-Null
$env:MEZHS_EXECUTOR_STORAGE = $data
$env:MEZHS_EXECUTION_ID = 'validation-parent'
$env:MEZHS_SOURCE = 'executor-lifecycle-test'

function Executor([Parameter(ValueFromRemainingArguments=$true)][string[]]$arguments) {
    $output = & dotnet $executor @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Executor $($arguments -join ' ') failed: $($output -join [Environment]::NewLine)"
    }
    return ($output -join [Environment]::NewLine).Trim()
}
function Field([string]$record,[string]$name) {
    ([regex]::Match($record,"(?m)^$([regex]::Escape($name)):\s+([^\r\n]+)\s*$")).Groups[1].Value.Trim()
}
function Status([string]$record) { Field $record 'Status' }
function Assert([bool]$condition,[string]$message) { if (-not $condition) { throw $message } }

try {
    $sleep20 = 'powershell -NoProfile -Command "Start-Sleep -Seconds 20"'
    $sleep30 = 'powershell -NoProfile -Command "Start-Sleep -Seconds 30"'

    # An Agent command identity identifies its direct SH edge only. A nested/direct Executor
    # invocation inherits conversation lineage, but must not reuse the parent's idempotency key.
    $env:MEZHS_CHAT_ID = 'inherited-chat'
    $env:MEZHS_TRIGGER_MESSAGE_ID = 'inherited-message'
    $env:MEZHS_COMMAND_INDEX = '0'
    $nestedA = Join-Path $work 'nested-a.txt'
    $nestedB = Join-Path $work 'nested-b.txt'
    $nestedAId = [int](Executor Execute "echo a>$nestedA" $work 30)
    $nestedBId = [int](Executor Execute "echo b>$nestedB" $work 30)
    Assert ($nestedAId -ne $nestedBId) 'Inherited Agent command identity collapsed two direct Executor calls into one execution.'
    $nestedARecord = Executor Wait $nestedAId 20
    $nestedBRecord = Executor Wait $nestedBId 20
    Assert ((Status $nestedARecord) -eq 'Completed' -and (Status $nestedBRecord) -eq 'Completed') 'Nested direct Executor calls did not both complete.'
    Assert ((Test-Path $nestedA) -and (Test-Path $nestedB)) 'Nested direct Executor calls did not both produce their side effects.'
    Remove-Item Env:MEZHS_CHAT_ID,Env:MEZHS_TRIGGER_MESSAGE_ID,Env:MEZHS_COMMAND_INDEX -ErrorAction SilentlyContinue

    # Execute must return while an independently owned command continues after its caller exits.
    $file = Join-Path $work 'survived.txt'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $id = [int](Executor Execute "ping -n 3 127.0.0.1 >nul & echo survived>$file" $work 30)
    $watch.Stop()
    Assert ($watch.Elapsed.TotalSeconds -lt 5) "Execute blocked for $($watch.Elapsed.TotalSeconds)s instead of detaching"
    $record = Executor Wait $id 20
    Assert ((Status $record) -eq 'Completed') "Detached caller exit: got $(Status $record)"
    Assert ($record -notmatch '@chcp') 'Executor wrote a UTF-8 preamble into cmd.exe input.'
    Assert (Test-Path $file) 'Detached caller exit: side effect missing'

    # The direct shell may intentionally launch a background descendant. The
    # descendant must not keep Executor's redirected output handles open and turn the
    # already-finished shell execution into a false Dead record.
    if ($IsWindows) {
        $backgroundScript = Join-Path $work 'background-service.cmd'
        $backgroundFile = Join-Path $work 'background-child.txt'
        @'
@echo off
ping -n 12 127.0.0.1 >nul
echo survived>background-child.txt
exit
'@ | Set-Content -LiteralPath $backgroundScript -Encoding Ascii
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $id = [int](Executor Execute 'start "" /b background-service.cmd > background-service.log 2>&1' $work 30)
        $record = Executor Wait $id 5
        $watch.Stop()
        Assert ((Status $record) -eq 'Completed') "Background descendant held the direct shell execution open: $(Status $record)"
        Assert ($watch.Elapsed.TotalSeconds -lt 5) "Background shell command did not complete promptly: $($watch.Elapsed.TotalSeconds)s"
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
        while (-not (Test-Path $backgroundFile) -and [DateTimeOffset]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 200
        }
        Assert (Test-Path $backgroundFile) 'Background descendant did not survive the completed direct shell.'
    }

    # Two owners racing for one Created row must still produce one side effect.
    $file = Join-Path $work 'atomic.txt'
    $id = [int](Executor Execute "ping -n 3 127.0.0.1 >nul & echo hit>>$file" $work 30)
    $p1 = Start-Process dotnet -ArgumentList @($executor,'Run',$id,$data) -PassThru -WindowStyle Hidden
    $p2 = Start-Process dotnet -ArgumentList @($executor,'Run',$id,$data) -PassThru -WindowStyle Hidden
    $record = Executor Wait $id 20
    $p1.WaitForExit(5000) | Out-Null
    $p2.WaitForExit(5000) | Out-Null
    Assert ((Status $record) -eq 'Completed') "Atomic claim: got $(Status $record)"
    Assert (@(Get-Content $file).Count -eq 1) 'Atomic claim: side effect duplicated'

    # Kill is a durable request observed by the independent owner.
    $id = [int](Executor Execute $sleep20 $work 30)
    Start-Sleep -Milliseconds 1200
    $before = Executor Get $id
    Assert ((Status $before) -eq 'Running') "Kill setup did not reach Running: $(Status $before)"
    $kill = Executor Kill $id
    Assert ((Status $kill) -in @('KillRequested','Killed')) "Kill request transition: got $(Status $kill)"
    $record = Executor Wait $id 15
    Assert ((Status $record) -eq 'Killed') "Kill: got $(Status $record)"

    # Runtime-owned timeout must terminate the child tree and persist TimedOut.
    $id = [int](Executor Execute $sleep20 $work 1)
    $record = Executor Wait $id 15
    Assert ((Status $record) -eq 'TimedOut') "Timeout: got $(Status $record)"

    # External restart creates a different durable row and preserves both lineage directions.
    $file = Join-Path $work 'restart.txt'
    $id = [int](Executor Execute "ping -n 8 127.0.0.1 >nul & echo done>>$file" $work 30)
    Start-Sleep -Milliseconds 1200
    $newId = [int](Executor Restart $id)
    Assert ($newId -ne $id) 'Restart: ID reused'
    $replacement = Executor Wait $newId 25
    $old = Executor Get $id
    Assert ((Status $replacement) -eq 'Completed') "Restart replacement: got $(Status $replacement)"
    Assert ((Field $replacement 'RestartedFromId') -eq "$id") 'Restart: new lineage missing'
    Assert ((Field $old 'RestartedAsId') -eq "$newId") 'Restart: old lineage missing'
    Assert (@(Get-Content $file).Count -eq 1) 'Restart: old child reached side effect'

    # Heartbeat advances while alive; killing only the owner is reconciled lazily to Dead.
    $id = [int](Executor Execute $sleep30 $work 60)
    Start-Sleep -Milliseconds 1200
    $running = Executor Get $id
    $heartbeat1 = Field $running 'HeartbeatAt'
    Start-Sleep -Milliseconds 1500
    $running = Executor Get $id
    $heartbeat2 = Field $running 'HeartbeatAt'
    Assert ($heartbeat1 -ne $heartbeat2) 'Heartbeat did not advance while execution owner was alive'
    $owner = [int](Field $running 'OwnerProcessId')
    $child = [int](Field $running 'ProcessId')
    Stop-Process -Id $owner -Force
    Start-Sleep -Seconds 10
    $dead = Executor Get $id
    Assert ((Status $dead) -eq 'Dead') "Dead reconciliation: got $(Status $dead)"
    if ($null -ne (Get-Process -Id $child -ErrorAction SilentlyContinue)) {
        & taskkill.exe /PID $child /T /F 2>$null | Out-Null
    }

    $directAgentShell = Get-ChildItem (Join-Path $root 'src/Mezhs.Agent.Api') -Filter *.cs -Recurse |
        Select-String -Pattern 'ProcessStartInfo|Process\.Start\('
    Assert ($directAgentShell.Count -eq 0) ("Direct Agent shell process API remains: " + ($directAgentShell -join '; '))

    Write-Host 'PASS: Executor nested-identity isolation, detachment, atomic claim, kill, timeout, restart, heartbeat, and lazy Dead reconciliation are correct.'
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $data,"$data-shm","$data-wal" -Force -ErrorAction SilentlyContinue
    Remove-Item Env:MEZHS_EXECUTOR_STORAGE,Env:MEZHS_EXECUTION_ID,Env:MEZHS_SOURCE,Env:MEZHS_CHAT_ID,Env:MEZHS_TRIGGER_MESSAGE_ID,Env:MEZHS_COMMAND_INDEX -ErrorAction SilentlyContinue
}
