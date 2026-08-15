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

$temp = Join-Path $env:RUNNER_TEMP ('mezhs-webauthn-disabled-' + [Guid]::NewGuid().ToString('N'))
$profile = Join-Path $temp 'profile'
$modulePath = Join-Path $temp 'integration.js'
$stdoutPath = Join-Path $temp 'stdout.log'
$stderrPath = Join-Path $temp 'stderr.log'
New-Item -ItemType Directory -Path $profile -Force | Out-Null
@'
module.exports = {
  name: "WebAuthn Disabled Test",
  homeUrl: "data:text/html,<html><body>test</body></html>",
  async sendPrompt({ window }) {
    const state = await window.webContents.executeJavaScript(`
      ({
        publicKeyCredential: typeof PublicKeyCredential,
        credentials: typeof navigator.credentials,
        credentialsGet: typeof navigator.credentials?.get
      })
    `, true);
    return { ok: true, text: JSON.stringify(state) };
  }
};
'@ | Set-Content -LiteralPath $modulePath -Encoding UTF8

$previous = @{
    PROFILE = $env:MEZHS_PROFILE_DIRECTORY
    SHOW = $env:MEZHS_SHOW_BROWSER
    MODULE = $env:MEZHS_BROWSER_MODULE
    AUTH = $env:MEZHS_REQUIRE_AUTHORIZATION
    DISABLE_WEBAUTHN = $env:MEZHS_DISABLE_WEBAUTHN
    PARENT = $env:MEZHS_PARENT_PROCESS_ID
}
$process = $null

try {
    $env:MEZHS_PROFILE_DIRECTORY = $profile
    $env:MEZHS_SHOW_BROWSER = '0'
    $env:MEZHS_BROWSER_MODULE = $modulePath
    $env:MEZHS_REQUIRE_AUTHORIZATION = '0'
    $env:MEZHS_DISABLE_WEBAUTHN = '1'
    $env:MEZHS_PARENT_PROCESS_ID = $PID.ToString()

    $process = Start-Process -FilePath $executable `
        -ArgumentList '.' `
        -WorkingDirectory $electronRoot `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -PassThru

    $port = $null
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $stdout = Get-Content $stdoutPath -Raw -ErrorAction SilentlyContinue
        foreach ($line in ($stdout -split "`r?`n")) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            try {
                $event = $line | ConvertFrom-Json
                if ($event.event -eq 'ready') {
                    $port = [int]$event.port
                    break
                }
                if ($event.event -eq 'error') {
                    throw "Electron initialization failed: $($event.error)"
                }
            } catch [System.ArgumentException] { }
        }
        if ($null -ne $port) { break }
        if ($process.HasExited) { break }
        Start-Sleep -Milliseconds 100
        $process.Refresh()
    }

    if ($null -eq $port) {
        $stderr = Get-Content $stderrPath -Raw -ErrorAction SilentlyContinue
        throw "Electron did not become ready. stdout=$stdout stderr=$stderr"
    }

    $response = Invoke-RestMethod `
        -Method Post `
        -Uri "http://127.0.0.1:$port/prompt" `
        -ContentType 'application/json' `
        -Body '{"prompt":"test"}'
    $state = $response.text | ConvertFrom-Json

    if ($state.publicKeyCredential -ne 'undefined') {
        throw "WebAuthentication feature is still exposed: PublicKeyCredential=$($state.publicKeyCredential)"
    }

    Write-Host 'PASS: Electron login process disables Chromium WebAuthentication.'
}
finally {
    $env:MEZHS_PROFILE_DIRECTORY = $previous.PROFILE
    $env:MEZHS_SHOW_BROWSER = $previous.SHOW
    $env:MEZHS_BROWSER_MODULE = $previous.MODULE
    $env:MEZHS_REQUIRE_AUTHORIZATION = $previous.AUTH
    $env:MEZHS_DISABLE_WEBAUTHN = $previous.DISABLE_WEBAUTHN
    $env:MEZHS_PARENT_PROCESS_ID = $previous.PARENT
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
