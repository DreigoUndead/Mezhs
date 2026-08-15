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

$temp = Join-Path $env:RUNNER_TEMP ('mezhs-login-visibility-' + [Guid]::NewGuid().ToString('N'))
$profile = Join-Path $temp 'profile'
$modulePath = Join-Path $temp 'integration.js'
$stdoutPath = Join-Path $temp 'stdout.log'
$stderrPath = Join-Path $temp 'stderr.log'
New-Item -ItemType Directory -Path $profile -Force | Out-Null
@'
module.exports = {
  name: "Login Visibility Test",
  homeUrl: "data:text/html,<html><body>login</body></html>",
  async isAuthorized() { return false; },
  async sendPrompt() { return { ok: true, text: "unused" }; }
};
'@ | Set-Content -LiteralPath $modulePath -Encoding UTF8

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NativeLoginWindow {
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
    $env:MEZHS_SHOW_BROWSER = '1'
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
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        foreach ($candidate in Get-Process electron -ErrorAction SilentlyContinue) {
            if ($candidate.MainWindowHandle -ne 0 -and [NativeLoginWindow]::IsWindowVisible($candidate.MainWindowHandle)) {
                $visibleSeen = $true
                Write-Host "VISIBLE LOGIN WINDOW: pid=$($candidate.Id) title=$($candidate.MainWindowTitle)"
                break
            }
        }
        if ($visibleSeen) { break }
        if ($process.HasExited) { break }
        Start-Sleep -Milliseconds 50
        $process.Refresh()
    }

    $stdout = Get-Content $stdoutPath -Raw -ErrorAction SilentlyContinue
    $stderr = Get-Content $stderrPath -Raw -ErrorAction SilentlyContinue
    if (-not $visibleSeen) {
        throw "Interactive authorization did not expose a visible Electron window. stdout=$stdout stderr=$stderr"
    }
    if ($process.HasExited) {
        throw "Electron exited while interactive authorization was still pending. stdout=$stdout stderr=$stderr"
    }
    if ($stdout -match '"event":"error"') {
        throw "Interactive authorization returned an initialization error instead of waiting for login. stdout=$stdout"
    }

    Write-Host 'PASS: interactive authorization opens a visible Electron window and waits for login.'
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
