# Runtime test for Electron browser identity, OAuth child-window behavior, and attached page operations.
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
$pagePath = Join-Path $temp 'page.js'
$stdoutPath = Join-Path $temp 'stdout.log'
$stderrPath = Join-Path $temp 'stderr.log'
New-Item -ItemType Directory -Path $profile -Force | Out-Null
@'
const { session: electronSession } = require("electron");
// Synthetic provider and IdP content: no real authentication, credentials or network.
const fixtureSession = electronSession.fromPath(process.env.MEZHS_PROFILE_DIRECTORY);
fixtureSession.protocol.handle("https", request => {
  const url = new URL(request.url);
  if (url.origin === "https://accounts.google.com" && url.pathname === "/redirect")
    return new Response(null, { status: 302, headers: { Location: "https://evil.example/next" } });
  const body = url.origin === "https://chatgpt.com"
    ? "<html><body>identity</body></html>"
    : "<html><body>oauth child</body></html>";
  return new Response(body, { headers: { "Content-Type": "text/html" } });
});

async function readIdentity(webContents) {
  return webContents.executeJavaScript(`({
    userAgent: navigator.userAgent,
    chromeApp: Boolean(window.chrome && window.chrome.app),
    chromeCsi: typeof window.chrome?.csi,
    chromeLoadTimes: typeof window.chrome?.loadTimes,
    hasOpener: Boolean(window.opener)
  })`, true);
}

function createChildServer() {
  return { url: "https://accounts.google.com/oauth", server: { close: done => done() } };
}

module.exports = {
  name: "Browser Identity Test",
  homeUrl: "https://chatgpt.com/",
  operations: {
    async inspect({ window, session, page }) {
      await new Promise(resolve => setTimeout(resolve, 2500));
      const attachedPageText = await page.invoke("bodyText");
    const sandboxState = await page.invoke("sandboxState");
      const main = await readIdentity(window.webContents);
      const childHost = await createChildServer();
      try {
        const childResult = await new Promise((resolve, reject) => {
          const timeout = setTimeout(() => reject(new Error("child window timed out")), 5000);
          window.webContents.once("did-create-window", childWindow => {
            const sameSession = childWindow.webContents.session === session;
            childWindow.webContents.once("did-finish-load", async () => {
              try {
                const identity = await readIdentity(childWindow.webContents);
                clearTimeout(timeout);
                childWindow.destroy();
                resolve({ identity, sameSession });
              } catch (error) {
                clearTimeout(timeout);
                childWindow.destroy();
                reject(error);
              }
            });
          });
          const url = JSON.stringify(childHost.url);
          void window.webContents.executeJavaScript(
            `void window.open(${url}, "_blank", "width=300,height=200"); true`,
            true);
        });
        let deniedChildCreated = false;
        const onUnexpectedChild = () => { deniedChildCreated = true; };
        window.webContents.on("did-create-window", onUnexpectedChild);
        const deniedReturn = await window.webContents.executeJavaScript(
          'Boolean(window.open("https://unrelated.example/", "_blank"))', true);
        await new Promise(resolve => setTimeout(resolve, 200));
        window.webContents.removeListener("did-create-window", onUnexpectedChild);

        const redirectResult = await new Promise((resolve, reject) => {
          const timeout = setTimeout(() => reject(new Error("redirect child timed out")), 4000);
          window.webContents.once("did-create-window", child => {
            let redirectObserved = false;
            let navigatedOutside = false;
            child.webContents.on("will-redirect", (_event, details) => {
              if ((typeof details === "string" ? details : details.url) === "https://evil.example/next")
                redirectObserved = true;
            });
            child.webContents.on("did-navigate", (_event, url) => {
              if (url === "https://evil.example/next") navigatedOutside = true;
            });
            setTimeout(() => {
              clearTimeout(timeout);
              child.destroy();
              resolve({ redirectObserved, navigatedOutside });
            }, 650);
          });
          void window.webContents.executeJavaScript(
            'void window.open("https://accounts.google.com/redirect", "_blank"); true', true);
        });
        return {
          deniedReturn, deniedChildCreated, redirectResult,
          sessionUserAgent: session.getUserAgent(),
          attachedPageText,
          sandboxState,
          main,
          child: childResult.identity,
          childSameSession: childResult.sameSession
        };
      } finally {
        await new Promise(resolve => childHost.server.close(resolve));
      }
    }
  },
  pageOperationFile: "page.js",
  pageOperations: require("./page.js").pageOperations
};
'@ | Set-Content -LiteralPath $modulePath -Encoding UTF8

@'
module.exports = {
  pageOperations: {
    bodyText() { return document.body?.textContent?.trim() || ""; },
    sandboxState() {
      let unrestrictedNode = false;
      try { require("node:fs"); unrestrictedNode = true; } catch {}
      return { enabled: process.sandboxed === true, unrestrictedNode };
    }
  }
};
'@ | Set-Content -LiteralPath $pagePath -Encoding UTF8

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
    try {
        $startedAt = [DateTimeOffset]::UtcNow
        $start = Invoke-RestMethod `
            -Method Post `
            -Uri "http://127.0.0.1:$port/invoke" `
            -ContentType 'application/json' `
            -Body $body `
            -TimeoutSec 10
        $startElapsed = [DateTimeOffset]::UtcNow - $startedAt
        if ([string]::IsNullOrWhiteSpace($start.operationId)) {
            throw 'Electron invoke did not return an operation id.'
        }
        if ($startElapsed -ge [TimeSpan]::FromSeconds(1.5)) {
            throw "Electron invoke start waited for provider completion instead of returning promptly: $startElapsed"
        }

        $observedPending = $false
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
        $response = $null
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            $status = Invoke-RestMethod `
                -Method Get `
                -Uri "http://127.0.0.1:$port/invoke/$($start.operationId)" `
                -TimeoutSec 10
            if ($status.status -eq 'queued' -or $status.status -eq 'running') {
                $observedPending = $true
                Start-Sleep -Milliseconds 200
                continue
            }
            if ($status.status -eq 'failed') {
                throw "Electron provider operation failed: $($status.error)"
            }
            if ($status.status -eq 'completed') {
                $response = $status.result
                break
            }
            throw "Electron provider operation returned unknown status '$($status.status)'."
        }
        if ($null -eq $response) {
            throw 'Electron provider operation did not complete within the test window.'
        }
        if (-not $observedPending) {
            throw 'Electron provider operation never exposed queued/running state to polling.'
        }
    }
    catch {
        $stdout = Get-Content $stdoutPath -Raw -ErrorAction SilentlyContinue
        $stderr = Get-Content $stderrPath -Raw -ErrorAction SilentlyContinue
        throw "Electron browser identity invoke failed: $($_.Exception.Message) stdout=$stdout stderr=$stderr"
    }

    if ($response.attachedPageText -ne 'identity') {
        throw "attached page operation did not execute in the loaded renderer: $($response.attachedPageText)"
    }
    if (-not $response.sandboxState.enabled) {
        throw 'The live provider preload did not run inside the Chromium renderer sandbox.'
    }
    if ($response.sandboxState.unrestrictedNode) {
        throw 'The sandboxed preload was able to import an unrestricted Node.js built-in.'
    }
    if ($response.sessionUserAgent -match 'Electron|mezhs') {
        throw "Session user agent leaks an embedded-browser product token: $($response.sessionUserAgent)"
    }
    if ($response.sessionUserAgent -notmatch 'Chrome/') {
        throw "Session user agent does not identify Chromium: $($response.sessionUserAgent)"
    }
    Assert-ChromeIdentity $response.main 'main window'
    Assert-ChromeIdentity $response.child 'child OAuth window'
    if ($response.deniedReturn -or $response.deniedChildCreated) {
        throw 'Unrelated HTTPS popup was not denied by the live Electron handler.'
    }
    if (-not $response.redirectResult.redirectObserved -or $response.redirectResult.navigatedOutside) {
        throw 'The live Electron child did not block the off-origin HTTP redirect.'
    }
    if (-not $response.childSameSession) {
        throw 'child OAuth window does not use the persistent provider session'
    }
    if (-not $response.child.hasOpener) {
        throw 'child OAuth window lost window.opener'
    }

    Write-Host 'PASS: Electron popup policy blocks unrelated destinations and redirects while preserving sandboxed authentication session, opener and browser identity.'
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
