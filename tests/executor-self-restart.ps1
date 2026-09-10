$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$executor = (Resolve-Path (Join-Path $root 'src/Mezhs.Executor/bin/Release/net10.0/Mezhs.Executor.dll')).Path
$data = Join-Path $root 'data/executor-self-restart.sqlite'
$work = Join-Path $root 'executor-self-restart-work'
Remove-Item $data,"$data-shm","$data-wal" -Force -ErrorAction SilentlyContinue
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item $work -ItemType Directory | Out-Null
$env:MEZHS_EXECUTOR_STORAGE = $data

function Executor([Parameter(ValueFromRemainingArguments=$true)][string[]]$arguments) {
    $output = & dotnet $executor @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Executor $($arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
    return ($output -join [Environment]::NewLine).Trim()
}
function Field([string]$record,[string]$name) {
    ([regex]::Match($record,"(?m)^$([regex]::Escape($name)):\s+([^\r\n]+)\s*$")).Groups[1].Value.Trim()
}
function Status([string]$record) { Field $record 'Status' }

try {
    $marker = Join-Path $work 'started.txt'
    $done = Join-Path $work 'replacement.txt'
    $leak = Join-Path $work 'old-leaked.txt'
    $command = "if exist `"$marker`" (echo replacement>`"$done`") else (echo first>`"$marker`" & dotnet `"$executor`" Restart %MEZHS_EXECUTION_ID% & ping -n 30 127.0.0.1 >nul & echo leaked>`"$leak`")"

    $oldId = [int](Executor Execute $command $work 60)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    $newId = 0
    do {
        $old = Executor Get $oldId
        $lineage = Field $old 'RestartedAsId'
        if ($lineage -and $lineage -ne 'null') { $newId = [int]$lineage; break }
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            throw "Self-restart did not create replacement lineage. Old state: $(Status $old)"
        }
        Start-Sleep -Milliseconds 150
    } while ($true)

    $replacement = Executor Wait $newId 20
    $old = Executor Get $oldId
    if ((Status $replacement) -ne 'Completed') {
        throw "Self-restart replacement did not complete: $(Status $replacement)"
    }
    if ((Status $old) -ne 'Killed') {
        throw "Self-restart did not terminate the old owned process: $(Status $old)"
    }
    if ((Field $replacement 'RestartedFromId') -ne "$oldId") {
        throw 'Replacement lost self-restart lineage.'
    }
    if (-not (Test-Path $done)) {
        throw 'Replacement process did not run after self-restart handoff.'
    }
    if (Test-Path $leak) {
        throw 'Old application continued after requesting its own restart.'
    }

    Write-Host "PASS: An owned application can restart its own Executor execution; the owner kills the old tree and launches the replacement independently."
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $data,"$data-shm","$data-wal" -Force -ErrorAction SilentlyContinue
    Remove-Item Env:MEZHS_EXECUTOR_STORAGE -ErrorAction SilentlyContinue
}
