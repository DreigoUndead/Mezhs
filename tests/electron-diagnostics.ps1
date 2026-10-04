# Runtime smoke test for generic Electron diagnostics using a disposable profile.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$electronRoot = Join-Path $root 'electron'
$packageRoot = Join-Path $electronRoot 'node_modules\electron'
$pathFile = Join-Path $packageRoot 'path.txt'
if (-not (Test-Path -LiteralPath $pathFile)) {
    throw 'Electron is not installed. Run npm ci --prefix electron first.'
}

$executable = Join-Path (Join-Path $packageRoot 'dist') ((Get-Content $pathFile -Raw).Trim())
if (-not (Test-Path -LiteralPath $executable)) {
    throw "Electron executable was not found: $executable"
}

$tempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }
$temp = Join-Path $tempRoot ('mezhs-electron-diagnostics-' + [Guid]::NewGuid().ToString('N'))
$profile = Join-Path $temp 'profile'
$modulePath = Join-Path $temp 'integration.js'
$stdoutPath = Join-Path $temp 'stdout.log'
$stderrPath = Join-Path $temp 'stderr.log'
New-Item -ItemType Directory -Path $profile -Force | Out-Null

@'
module.exports = {
  name: "Diagnostics Smoke",
  homeUrl: "data:text/html,<html><body><button id='picker' aria-label='Model picker' onclick=\"this.textContent='Picker open';const x=document.createElement('button');x.setAttribute('role','option');x.textContent='High';document.body.appendChild(x);fetch('http://127.0.0.1:9/diagnostics-ping').catch(()=>{});\">Open picker</button><input id='editor' aria-label='Prompt' /></body></html>",
  operations: {}
};
'@ | Set-Content -LiteralPath $modulePath -Encoding UTF8

$previous = @{
    PROFILE = $env:MEZHS_PROFILE_DIRECTORY
    SHOW = $env:MEZHS_SHOW_BROWSER
    MODULE = $env:MEZHS_BROWSER_MODULE
    AUTH = $env:MEZHS_REQUIRE_AUTHORIZATION
    PARENT = $env:MEZHS_PARENT_PROCESS_ID
}
$process = $null
$port = $null

function Invoke-Diagnostic([string]$operation, $arguments = @{}) {
    $body = @{ operation = ('$diagnostics/' + $operation); arguments = $arguments } | ConvertTo-Json -Compress -Depth 8
    $start = Invoke-RestMethod `
        -Method Post `
        -Uri "http://127.0.0.1:$port/invoke" `
        -ContentType 'application/json' `
        -Body $body `
        -TimeoutSec 10

    if ([string]::IsNullOrWhiteSpace($start.operationId)) {
        throw "Diagnostic '$operation' did not return an operation id."
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $status = Invoke-RestMethod `
            -Method Get `
            -Uri "http://127.0.0.1:$port/invoke/$($start.operationId)" `
            -TimeoutSec 10
        if ($status.status -eq 'queued' -or $status.status -eq 'running') {
            Start-Sleep -Milliseconds 100
            continue
        }
        if ($status.status -eq 'failed') {
            throw "Diagnostic '$operation' failed: $($status.error)"
        }
        if ($status.status -eq 'completed') {
            return $status.result
        }
        throw "Diagnostic '$operation' returned unknown status '$($status.status)'."
    }
    throw "Diagnostic '$operation' timed out."
}

try {
    $env:MEZHS_PROFILE_DIRECTORY = $profile
    $env:MEZHS_SHOW_BROWSER = '0'
    $env:MEZHS_BROWSER_MODULE = $modulePath
    $env:MEZHS_REQUIRE_AUTHORIZATION = '0'
    $env:MEZHS_PARENT_PROCESS_ID = $PID.ToString()

    $process = Start-Process -FilePath $executable `
        -ArgumentList '.' `
        -WorkingDirectory $electronRoot `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -PassThru

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $stdout = Get-Content $stdoutPath -Raw -ErrorAction SilentlyContinue
        if ($stdout -match '"event":"ready","port":(\d+)') {
            $port = [int]$Matches[1]
            break
        }
        if ($stdout -match '"event":"error"') { break }
        if ($process.HasExited) { break }
        Start-Sleep -Milliseconds 50
        $process.Refresh()
    }

    if ($null -eq $port) {
        $stdout = Get-Content $stdoutPath -Raw -ErrorAction SilentlyContinue
        $stderr = Get-Content $stderrPath -Raw -ErrorAction SilentlyContinue
        throw "Electron diagnostics smoke host did not initialize. stdout=$stdout stderr=$stderr"
    }

    $snapshot = Invoke-Diagnostic 'snapshot'
    $picker = $snapshot.elements | Where-Object { $_.ariaLabel -eq 'Model picker' } | Select-Object -First 1
    if ($null -eq $picker) { throw 'Snapshot did not expose the model picker button.' }

    $pickerX = [int]($picker.x + ($picker.width / 2))
    $pickerY = [int]($picker.y + ($picker.height / 2))
    $hit = Invoke-Diagnostic 'inspectPoint' @{ x = $pickerX; y = $pickerY }
    if ($hit.ariaLabel -ne 'Model picker') {
        throw "inspectPoint did not resolve the picker: $($hit | ConvertTo-Json -Compress)"
    }

    $click = Invoke-Diagnostic 'click' @{ x = $pickerX; y = $pickerY; waitMs = 400 }
    $option = $click.after.elements | Where-Object { $_.role -eq 'option' -and $_.text -eq 'High' } | Select-Object -First 1
    if ($null -eq $option) { throw 'Click did not expose the synthetic picker option in the after snapshot.' }
    if (-not ($click.network | Where-Object { $_.url -like 'http://127.0.0.1:9/diagnostics-ping' })) {
        throw 'Click did not capture the network request delta.'
    }

    $editor = $click.after.elements | Where-Object { $_.ariaLabel -eq 'Prompt' } | Select-Object -First 1
    if ($null -eq $editor) { throw 'After snapshot did not expose the prompt input.' }
    $editorX = [int]($editor.x + ($editor.width / 2))
    $editorY = [int]($editor.y + ($editor.height / 2))
    Invoke-Diagnostic 'click' @{ x = $editorX; y = $editorY; waitMs = 50 } | Out-Null
    Invoke-Diagnostic 'type' @{ text = 'abc123' } | Out-Null

    $typed = Invoke-Diagnostic 'snapshot'
    $typedText = (($typed.activeElement.text -replace '\s+', ' ').Trim())
   if ($typed.activeElement.ariaLabel -ne 'Prompt' -or $typedText -ne 'abc123') {
        throw "Typing did not affect the focused prompt input: $($typed.activeElement | ConvertTo-Json -Compress)"
    }

    $image = Invoke-Diagnostic 'screenshot'
    if ($image.mimeType -ne 'image/png' -or
        [string]::IsNullOrWhiteSpace($image.base64) -or
        $image.width -le 0 -or
        $image.height -le 0) {
        throw 'Screenshot diagnostic did not return a valid PNG payload.'
    }

    Write-Host 'PASS: Electron diagnostics snapshot, hit-test, real click, DOM delta, redacted network delta, typing, and screenshot work on a disposable profile.'
}
finally {
    if ($null -ne $port) {
        try {
            Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$port/shutdown" -ContentType 'application/json' -Body '{}' | Out-Null
        } catch {}
    }
    $env:MEZHS_PROFILE_DIRECTORY = $previous.PROFILE
    $env:MEZHS_SHOW_BROWSER = $previous.SHOW
    $env:MEZHS_BROWSER_MODULE = $previous.MODULE
    $env:MEZHS_REQUIRE_AUTHORIZATION = $previous.AUTH
    $env:MEZHS_PARENT_PROCESS_ID = $previous.PARENT
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
