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

$temp = Join-Path $env:RUNNER_TEMP ('mezhs-browser-identity-' + [Guid]::NewGuid().ToString('N'))
$profile = Join-Path $temp 'profile'
$modulePath = Join-Path $temp 'integration.js'
$stdoutPath = Join-Path $temp 'stdout.log'
$stderrPath = Join-Path $temp 'stderr.log'
New-Item -ItemType Directory -Path $profile -Force | Out-Null
@'
async function readIdentity(webContents) {
  return webContents.executeJavaScript(`({
    userAgent: navigator.userAgent,
    chromeApp: Boolean(window.chrome && window.chrome.app),
    chromeCsi: typeof window.chrome?.csi,
    chromeLoadTimes: typeof window.chrome?.loadTimes
  })`, true);
}

module.exports = {
  name: "Browser Identity Test",
  homeUrl: "data:text/html,<html><body>identity</body></html>",
  operations: {
    async inspect({ window, session }) {
      const main = await readIdentity(window.webContents);
      const child = await new Promise((resolve, reject) => {
        const timeout = setTimeout(() => reject(new Error("child window timed out")), 5000);
        window.webContents.once("did-create-window", childWindow => {
          setTimeout(async () => {
            try {
              const result = await readIdentity(childWindow.webContents);
              clearTimeout(timeout);
              childWindow.destroy();
              resolve(result);
            } catch (error) {
              clearTimeout(timeout);
              childWindow.destroy();
              reject(error);
            }
          }, 100);
        });
        void window.webContents.executeJavaScript(
          `void window.open("about:blank", "_blank", "width=300,height=200"); true`,
          true);
      });
      return {
        sessionUserAgent: session.getUserAgent(),
        main,
        child
      };
    }
  }
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

function Assert-ChromeIdentity($identity, [string]$scope) {
    if ($identity.userAgent -notmatch 'Chrome/') {
        throw "$scope user agent does not identify Chromium: $($identity.userAgent)"
    }
    if ($identity.userAgent -match 'Electron|mezhs') {
        throw "$scope user agent leaks an embedded-browser product token: $($identity.userAgent)"
    }
    if (-not $identity.chromeApp) {
        throw "$scope is missing window.chrome.app"
    }
    if ($identity.chromeCsi -ne 'function') {
        throw "$scope is missing window.chrome.csi"
    }
    if ($identity.chromeLoadTimes -ne 'function') {
        throw "$scope is missing window.chrome.loadTimes"
    }
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
        if ($stdout -match '"event":"error"') {
            break
        }
        if ($process.HasExited) { break }
        Start-Sleep -Milliseconds 50
        $process.Refresh()
    }

    $stdout = Get-Content $stdoutPath -Raw -ErrorAction SilentlyContinue
    $stderr = Get-Content $stderrPath -Raw -ErrorAction SilentlyContinue
    if ($null -eq $port) {
        throw "Electron browser identity test did not initialize. stdout=$stdout stderr=$stderr"
    }

    $body = @{ operation = 'inspect'; arguments = @{} } | ConvertTo-Json -Compress
    $response = Invoke-RestMethod `
        -Method Post `
        -Uri "http://127.0.0.1:$port/invoke" `
        -ContentType 'application/json' `
        -Body $body

    if ($response.sessionUserAgent -match 'Electron|mezhs') {
        throw "Session user agent leaks an embedded-browser product token: $($response.sessionUserAgent)"
    }
    if ($response.sessionUserAgent -notmatch 'Chrome/') {
        throw "Session user agent does not identify Chromium: $($response.sessionUserAgent)"
    }
    Assert-ChromeIdentity $response.main 'main window'
    Assert-ChromeIdentity $response.child 'child OAuth window'

    Write-Host 'PASS: Electron main and child windows share the cleaned Chrome identity and runtime surface.'
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
