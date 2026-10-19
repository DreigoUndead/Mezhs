$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([IO.Path]::GetTempPath()) ("mezhs-diagnostics-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$started = @()

function Request([string]$url, [string]$path, [string]$body = '{}', [string]$origin = '') {
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, "$url$path")
    $request.Content = [Net.Http.StringContent]::new($body, [Text.Encoding]::UTF8, 'application/json')
    if ($origin) { $request.Headers.TryAddWithoutValidation('Origin', $origin) | Out-Null }
    try {
        try {
            $response = $client.SendAsync($request).GetAwaiter().GetResult()
        } catch {
            throw "Request to ${url}${path} failed: $($_.Exception.ToString())"
        }
        try {
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            return @{ Status = [int]$response.StatusCode; Body = $text }
        } finally { $response.Dispose() }
    } finally {
        $request.Dispose()
        $client.Dispose()
        $handler.Dispose()
    }
}

function Check([string]$hostName, [string]$caseName, [string]$url, [string]$path, [int]$status, [string]$error = '', [string]$origin = '') {
    $response = Request $url $path '{}' $origin
    if ($response.Status -ne $status) {
        throw "$hostName/$caseName expected HTTP $status, got $($response.Status): $($response.Body)"
    }
    if ($error -and $response.Body -notlike "*$error*") {
        throw "$hostName/$caseName response did not include '$error': $($response.Body)"
    }
    Write-Host "PASS $hostName/$caseName HTTP $status"
    return $response
}

function Wait-Health([string]$url, [System.Diagnostics.Process]$process) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw "$url exited before becoming healthy (code $($process.ExitCode))." }
        try {
            $client = [Net.Http.HttpClient]::new()
            try {
                $result = $client.GetStringAsync("$url/health").GetAwaiter().GetResult()
                if ($result -like '*"ok"*') { return }
            } finally { $client.Dispose() }
        } catch {}
        Start-Sleep -Milliseconds 200
    }
    throw "$url did not become healthy."
}

function Start-Host([string]$name, [string]$project, [string]$config, [string]$url) {
    $publish = Join-Path $temp "$name-publish"
    & dotnet publish $project --configuration Release --output $publish --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "Failed to publish $name." }
    $logOut = Join-Path $temp "$name.out.log"
    $logErr = Join-Path $temp "$name.err.log"
    $dll = Join-Path $publish "$name.dll"
    $p = Start-Process -FilePath 'dotnet' -ArgumentList @($dll, '--config', $config) -WorkingDirectory $root -RedirectStandardOutput $logOut -RedirectStandardError $logErr -PassThru -WindowStyle Hidden
    $script:started += $p
    try { Wait-Health $url $p } catch {
        if (Test-Path $logOut) { Get-Content $logOut }
        if (Test-Path $logErr) { Get-Content $logErr }
        throw
    }
    return $p
}

try {
    $api = Get-Content (Join-Path $root 'src/Mezhs.Api/MezhsApi.cs') -Raw
    $agent = Get-Content (Join-Path $root 'src/Mezhs.Agent.Api/Program.cs') -Raw
    $route = '/v1/diagnostics/connections/{connectionId}/browser/{operation}'
    if (([regex]::Matches($api, [regex]::Escape("MapPost(`"$route`""))).Count -ne 1 -or
        $agent -match 'MapAgentDiagnostics|MapPost\("/v1/diagnostics' -or
        $agent -notmatch 'app.MapMezhsApi\(\)') {
        throw 'Diagnostics route is not registered exactly once in the common API mapping.'
    }
    Write-Host 'PASS unique shared endpoint ownership'

    $apiUrl = 'http://127.0.0.1:52951'
    $agentUrl = 'http://127.0.0.1:52952'
    $genericConfig = Join-Path $temp 'api.yaml'
    $data = (Join-Path $temp 'generic-data').Replace('\', '/')
    $electron = (Join-Path $root 'electron').Replace('\', '/')
    @"
version: 1
server:
  listen: http://0.0.0.0:52951
transport:
  type: electron
  idleMinutes: 1
  electronDirectory: $electron
storage:
  root: $data
connections:
  - id: test-diagnostics
    name: Diagnostic probe
    integration: mock-diagnostics
  - id: test
    name: Plain mock
    integration: mock
  - id: account
    name: Browser account
    integration: chatgpt-web-account
"@ | Set-Content $genericConfig -Encoding UTF8

    $agentConfig = Join-Path $temp 'agent.yaml'
    $agentText = Get-Content (Join-Path $root 'tests/mezhs.agent.test.yaml') -Raw
    $agentStorage = (Join-Path $temp 'agent-store.sqlite').Replace('\', '/')
    $agentData = (Join-Path $temp 'agent-data').Replace('\', '/')
    $workspace = $root.Replace('\', '/')
    $agentText = $agentText.Replace('http://127.0.0.1:5199', $agentUrl).
        Replace('electronDirectory: ../electron', "electronDirectory: $electron").
        Replace('root: data-agent-test/core', "root: $agentData").
        Replace('agentStorage: data-agent-test/agent.sqlite', "agentStorage: $agentStorage").
        Replace('workspace: ..', "workspace: $workspace")
    $agentText = $agentText.Replace('agentStorage:', @"
  - id: test-diagnostics
    name: Diagnostic probe
    integration: mock-diagnostics
  - id: account
    name: Browser account
    integration: chatgpt-web-account

agentStorage:
"@)
    Set-Content $agentConfig $agentText -Encoding UTF8

    $null = Start-Host 'Mezhs.Api' (Join-Path $root 'src/Mezhs.Api/Mezhs.Api.csproj') $genericConfig $apiUrl
    $null = Start-Host 'Mezhs.Agent.Api' (Join-Path $root 'src/Mezhs.Agent.Api/Mezhs.Agent.Api.csproj') $agentConfig $agentUrl

    $diagnostic = '/v1/diagnostics/connections/test-diagnostics/browser/'
    foreach ($entry in @(@('Mezhs.Api', $apiUrl), @('Mezhs.Agent.Api', $agentUrl))) {
        $name = $entry[0]; $url = $entry[1]
        $response = Request $url ($diagnostic + 'echo') '{"message":"verified"}'
        if ($response.Status -ne 200) {
            throw "$name/success expected HTTP 200, got $($response.Status): $($response.Body)"
        }
        $result = $response.Body | ConvertFrom-Json
        if ($result.operation -ne 'echo' -or $result.arguments.message -ne 'verified') {
            throw "$name/success did not pass through diagnostic arguments: $($response.Body)"
        }
        Write-Host "PASS $name/success HTTP 200 and argument round-trip"
        $null = Check $name 'unsupported' $url ($diagnostic + 'unknown') 400 'not supported'
        $null = Check $name 'account-unsupported' $url '/v1/diagnostics/connections/account/browser/unknown' 400 'not supported'
        $null = Check $name 'missing' $url '/v1/diagnostics/connections/missing/browser/echo' 404 'not found'
        $null = Check $name 'no-capability' $url '/v1/diagnostics/connections/test/browser/echo' 400 'does not expose live diagnostics'
        $null = Check $name 'loopback-origin' $url ($diagnostic + 'echo') 200 '' 'http://localhost:3000'
        $null = Check $name 'remote-origin' $url ($diagnostic + 'echo') 403 '' 'https://attacker.example'
        $null = Check $name 'null-origin' $url ($diagnostic + 'echo') 403 '' 'null'
        $null = Check $name 'lookalike-origin' $url ($diagnostic + 'echo') 403 '' 'http://localhost.attacker.example'
        $null = Check $name 'remote-origin-before-lookup' $url '/v1/diagnostics/connections/missing/browser/echo' 403 '' 'https://attacker.example'
    }

    # Exercise the actual shared endpoint with a non-loopback RemoteIpAddress,
    # independent of whether Windows firewall permits connections via a LAN adapter.
    $remoteUrl = 'http://127.0.0.1:52953'
    $remoteConfig = Join-Path $temp 'remote.yaml'
    (Get-Content $genericConfig -Raw).
        Replace('http://0.0.0.0:52951', $remoteUrl).
        Replace('generic-data', 'remote-data') |
        Set-Content $remoteConfig -Encoding UTF8
    $null = Start-Host 'Mezhs.Diagnostics.RemoteIpTestHost' `
        (Join-Path $root 'tests/Mezhs.Diagnostics.RemoteIpTestHost/Mezhs.Diagnostics.RemoteIpTestHost.csproj') `
        $remoteConfig $remoteUrl
    $null = Check 'Mezhs.Api' 'nonloopback-remote-ip' $remoteUrl ($diagnostic + 'echo') 403

    Write-Host 'PASS: diagnostics endpoint regression for both hosts'

}
finally {
    foreach ($process in $started) {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit()
        }
        $process.Dispose()
    }
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
