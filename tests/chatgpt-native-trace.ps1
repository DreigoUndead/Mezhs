param(
    [string]$ProfileDirectory,
    [string]$Prompt,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$electronDir = Join-Path $root 'electron'
$electron = Join-Path $electronDir 'node_modules\electron\dist\electron.exe'
$module = Join-Path $PSScriptRoot 'diagnostics\chatgpt-native-trace-module.js'

if (-not $ProfileDirectory) {
    $ProfileDirectory = Join-Path $root 'data\connections\chatgpt-sub\profile'
}
$ProfileDirectory = [IO.Path]::GetFullPath($ProfileDirectory)

if (-not $Prompt) {
    $Prompt = "MEZHS native network trace $([Guid]::NewGuid().ToString('N')). Reply only TRACE_OK."
}

if (-not $OutputPath) {
    $diagnostics = Join-Path $root 'data\diagnostics'
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputPath = Join-Path $diagnostics "chatgpt-native-trace-$stamp.json"
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)

if (-not (Test-Path $electron)) {
    throw "Electron is not installed at '$electron'. Run 'npm ci --prefix electron' first."
}
if (-not (Test-Path $module)) {
    throw "Trace module was not found at '$module'."
}
if (-not (Test-Path $ProfileDirectory)) {
    throw "ChatGPT profile was not found at '$ProfileDirectory'. Log in through MEZS once first."
}

Write-Host "This sends one real native ChatGPT message using the existing chatgpt-sub browser profile."
Write-Host "Stop the running MEZS process first so Chromium can open the same profile."
Write-Host "No authorization cookies or Sentinel token values are written to the trace."

$temp = Join-Path ([IO.Path]::GetTempPath()) ("mezhs-chatgpt-native-trace-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$stdout = Join-Path $temp 'electron.stdout.log'
$stderr = Join-Path $temp 'electron.stderr.log'

$environmentNames = @(
    'MEZHS_PROFILE_DIRECTORY',
    'MEZHS_SHOW_BROWSER',
    'MEZHS_BROWSER_MODULE',
    'MEZHS_REQUIRE_AUTHORIZATION',
    'MEZHS_PARENT_PROCESS_ID'
)
$previousEnvironment = @{}
foreach ($name in $environmentNames) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

$process = $null
$port = $null

try {
    $env:MEZHS_PROFILE_DIRECTORY = $ProfileDirectory
    $env:MEZHS_SHOW_BROWSER = '1'
    $env:MEZHS_BROWSER_MODULE = $module
    $env:MEZHS_REQUIRE_AUTHORIZATION = '1'
    $env:MEZHS_PARENT_PROCESS_ID = "$PID"

    $process = Start-Process -FilePath $electron -ArgumentList @('.') -WorkingDirectory $electronDir -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru

    $readyDeadline = [DateTime]::UtcNow.AddMinutes(2)
    while ([DateTime]::UtcNow -lt $readyDeadline -and -not $port) {
        if ($process.HasExited) {
            $errorText = if (Test-Path $stderr) { Get-Content $stderr -Raw } else { '' }
            throw "Trace Electron exited before becoming ready. $errorText"
        }

        if (Test-Path $stdout) {
            foreach ($line in Get-Content $stdout) {
                if (-not $line.Trim()) { continue }
                try {
                    $event = $line | ConvertFrom-Json
                    if ($event.event -eq 'ready') {
                        $port = [int]$event.port
                        break
                    }
                    if ($event.event -eq 'error') {
                        throw "Trace Electron initialization failed: $($event.error)"
                    }
                }
                catch [System.ArgumentException] {
                }
            }
        }
        if (-not $port) { Start-Sleep -Milliseconds 250 }
    }

    if (-not $port) {
        throw "Timed out waiting for the trace Electron host. If ChatGPT asked for login, complete it in the visible window and rerun."
    }

    $base = "http://127.0.0.1:$port"
    $request = @{
        operation = 'traceNativeSend'
        arguments = @{ prompt = $Prompt }
    } | ConvertTo-Json -Depth 5

    $started = Invoke-RestMethod -Method Post -Uri "$base/invoke" -ContentType 'application/json' -Body $request

    if (-not $started.operationId) {
        throw 'Trace operation did not return an operation id.'
    }

    $outputDirectory = Split-Path -Parent $OutputPath
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

    $deadline = [DateTime]::UtcNow.AddMinutes(1)
    $result = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $state = Invoke-RestMethod -Method Get -Uri "$base/invoke/$($started.operationId)"
        switch ($state.status) {
            'queued' { Start-Sleep -Milliseconds 250; continue }
            'running' { Start-Sleep -Milliseconds 250; continue }
            'failed' {
                [ordered]@{
                    capturedAt = [DateTime]::UtcNow.ToString('o')
                    status = 'failed'
                    error = [string]$state.error
                } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
                throw "Native trace failed: $($state.error) Failure trace: $OutputPath"
            }
            'completed' {
                $result = $state.result
                break
            }
            default { throw "Unknown trace operation state '$($state.status)'." }
        }
        if ($result) { break }
    }

    if (-not $result) {
        throw 'Timed out waiting for the native ChatGPT request trace.'
    }

    $result | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $OutputPath -Encoding UTF8

    Write-Host ""
    Write-Host "Native ChatGPT request captured."
    Write-Host "Trace: $OutputPath"
    Write-Host ""
    $result | ConvertTo-Json -Depth 30
}
finally {
    if ($port) {
        try {
            Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$port/shutdown" | Out-Null
        }
        catch {
        }
    }

    if ($process -and -not $process.HasExited) {
        try {
            if (-not $process.WaitForExit(3000)) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            }
        }
        catch {
        }
    }

    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }

    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
