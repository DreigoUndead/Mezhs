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

$temp = Join-Path $env:RUNNER_TEMP ('mezhs-hidden-auth-' + [Guid]::NewGuid().ToString('N'))
$profile = Join-Path $temp 'profile'
$modulePath = Join-Path $temp 'integration.js'
$stdoutPath = Join-Path $temp 'stdout.log'
$stderrPath = Join-Path $temp 'stderr.log'
New-Item -ItemType Directory -Path $profile -Force | Out-Null
@'
module.exports = {
  name: "Hidden Authorization Test",
  homeUrl: "data:text/html,<html><body>hidden</body></html>",
  operations: {},
  async isAuthorized() { return false; }
};
'@ | Set-Content -LiteralPath $modulePath -Encoding UTF8

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NativeWindow {
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
}
'@

$previous = @{
    PROFILE = $env:MEZHS_PROFILE_DIRECTORY
    SHOW = $env:MEZHS_SHOW_BROWSER
    MODULE = $env:MEZHS_BROWSER_MODULE
    AUTH = $env:MEZHS_REQUIRE_AUTHORIZATION
    PARENT = $env:MEZHS_PARENT_PROCESS_ID
}
$process = $null

try {
    $env:MEZHS_PROFILE_DIRECTORY = $profile
    $env:MEZHS_SHOW_BROWSER = '0'
    $env:MEZHS_BROWSER_MODULE = $modulePath
    $env:MEZHS_REQUIRE_AUTHORIZATION = '1'
    $env:MEZHS_PARENT_PROCESS_ID = $PID.ToString()

    $process = Start-Process -FilePath $executable `
        -ArgumentList '.' `
        -WorkingDirectory $electronRoot `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -PassThru

    $visibleSeen = $false
    $errorSeen = $false
    $stdout = ''
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        foreach ($candidate in Get-Process electron -ErrorAction SilentlyContinue) {
            if ($candidate.MainWindowHandle -ne 0 -and [NativeWindow]::IsWindowVisible($candidate.MainWindowHandle)) {
                $visibleSeen = $true
                Write-Host "VISIBLE ELECTRON WINDOW: pid=$($candidate.Id) title=$($candidate.MainWindowTitle)"
            }
        }

        $stdout = Get-Content $stdoutPath -Raw -ErrorAction SilentlyContinue
        if ($stdout -match '"event":"error"') {
            $errorSeen = $true
            break
        }
        if ($process.HasExited) { break }
        Start-Sleep -Milliseconds 50
        $process.Refresh()
    }

    if ($visibleSeen) {
        throw 'A hidden authorization check exposed a visible Electron window.'
    }

    $stderr = Get-Content $stderrPath -Raw -ErrorAction SilentlyContinue
    if (-not $errorSeen) {
        throw "Hidden authorization test timed out waiting for Electron's initialization error contract. stdout=$stdout stderr=$stderr"
    }
    if ($stdout -notmatch '"code":"authorization_required"') {
        throw "Electron did not return the typed authorization-required code. stdout=$stdout"
    }
    if ($stdout -notmatch 'authorization is required') {
        throw "Electron authorization-required error did not contain a useful message. stdout=$stdout"
    }

    Write-Host 'PASS: hidden authorization stays hidden and returns a typed authorization-required result.'
}
finally {
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
