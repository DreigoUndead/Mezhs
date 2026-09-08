$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$executor = (Resolve-Path (Join-Path $root 'src/Mezhs.Executor/bin/Release/net10.0/Mezhs.Executor.dll')).Path
$data = Join-Path $root 'data/executor-validation.sqlite'
$work = Join-Path $root 'executor-validation-work'
Remove-Item $data,"$data-shm","$data-wal" -Force -ErrorAction SilentlyContinue
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
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

    # Execute must return while an independently owned command continues after its caller exits.
    $file = Join-Path $work 'survived.txt'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $id = [int](Executor Execute "ping -n 3 127.0.0.1 >nul & echo survived>$file" $work 30)
    $watch.Stop()
    Assert ($watch.Elapsed.TotalSeconds -lt 5) "Execute blocked for $($watch.Elapsed.TotalSeconds)s instead of detaching"
    $record = Executor Wait $id 20
    Assert ((Status $record) -eq 'Completed') "Detached caller exit: got $(Status $record)"
    Assert (Test-Path $file) 'Detached caller exit: side effect missing'

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

    # The same Agent SH identity is exactly-once, and after restart it reconnects to the latest descendant.
    $env:MEZHS_CHAT_ID = 'validation-chat'
    $env:MEZHS_TRIGGER_MESSAGE_ID = 'validation-message'
    $env:MEZHS_COMMAND_INDEX = '0'
    $file = Join-Path $work 'once.txt'
    $id1 = [int](Executor Execute "ping -n 3 127.0.0.1 >nul & echo once>>$file" $work 30)
    $id2 = [int](Executor Execute "ping -n 3 127.0.0.1 >nul & echo once>>$file" $work 30)
    Assert ($id1 -eq $id2) 'Agent correlation: duplicate Execute changed ID'
    $record = Executor Wait $id1 20
    Assert ((Status $record) -eq 'Completed') "Agent correlation: got $(Status $record)"
    Assert (@(Get-Content $file).Count -eq 1) 'Agent correlation: side effect duplicated'

    $env:MEZHS_TRIGGER_MESSAGE_ID = 'validation-restart-message'
    $file = Join-Path $work 'restart-identity.txt'
    $command = "ping -n 3 127.0.0.1 >nul & echo run>>$file"
    $originalId = [int](Executor Execute $command $work 30)
    $null = Executor Wait $originalId 20
    $replacementId = [int](Executor Restart $originalId)
    $replayedId = [int](Executor Execute $command $work 30)
    Assert ($replayedId -eq $replacementId) "Agent replay reconnected to $replayedId instead of latest restart $replacementId"
    $record = Executor Wait $replacementId 20
    Assert ((Status $record) -eq 'Completed') "Restarted Agent identity replacement: got $(Status $record)"
    Assert (@(Get-Content $file).Count -eq 2) 'Agent identity executed more than original + explicit restart'
    Remove-Item Env:MEZHS_CHAT_ID,Env:MEZHS_TRIGGER_MESSAGE_ID,Env:MEZHS_COMMAND_INDEX -ErrorAction SilentlyContinue

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
    Stop-Process -Id $child -Force -ErrorAction SilentlyContinue

    $directAgentShell = Get-ChildItem (Join-Path $root 'src/Mezhs.Agent.Api') -Filter *.cs -Recurse |
        Select-String -Pattern 'ProcessStartInfo|Process\.Start\('
    Assert ($directAgentShell.Count -eq 0) ("Direct Agent shell process API remains: " + ($directAgentShell -join '; '))

    Write-Host 'PASS: Executor detachment, atomic claim, kill, timeout, restart, Agent idempotency/restart reconnection, heartbeat, and lazy Dead reconciliation are correct.'
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $data,"$data-shm","$data-wal" -Force -ErrorAction SilentlyContinue
    Remove-Item Env:MEZHS_EXECUTOR_STORAGE,Env:MEZHS_EXECUTION_ID,Env:MEZHS_SOURCE,Env:MEZHS_CHAT_ID,Env:MEZHS_TRIGGER_MESSAGE_ID,Env:MEZHS_COMMAND_INDEX -ErrorAction SilentlyContinue
}
