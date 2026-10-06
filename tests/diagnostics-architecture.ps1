$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Read-Source([string]$relativePath) {
    [System.IO.File]::ReadAllText((Join-Path $root $relativePath))
}

function Assert([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

$contracts = Read-Source 'src\Mezhs.Integration.Abstractions\IntegrationContracts.cs'
$browserDiagnostics = Read-Source 'src\Mezhs.Integration.Browser\BrowserDiagnosticsModule.cs'
$chatGpt = Read-Source 'integrations\Mezhs.Integrations.ChatGpt\ChatGptIntegration.cs'
$grok = Read-Source 'integrations\Mezhs.Integrations.Grok\GrokIntegration.cs'
$agentProgram = Read-Source 'src\Mezhs.Agent.Api\Program.cs'
$diagnosticsApi = Read-Source 'src\Mezhs.Agent.Api\DiagnosticsApi.cs'
$electron = Read-Source 'electron\main.js'
$solution = Read-Source 'Mezhs.sln'

Assert ($contracts.Contains('public interface IIntegrationDiagnosticsModule') -and
        $contracts.Contains('IIntegrationDiagnosticsModule? Diagnostics')) `
    'Diagnostics are not an optional integration capability.'

Assert ($browserDiagnostics.Contains('BrowserDiagnosticsModule(BrowserAccountSession session)') -and
        $browserDiagnostics.Contains('session.UseAuthorizedAsync') -and
        $browserDiagnostics.Contains('$diagnostics/{normalized}')) `
    'Browser diagnostics do not execute through the owning authenticated BrowserAccountSession.'

foreach ($source in @($chatGpt, $grok)) {
    Assert ($source.Contains('_session = CreateAccountSession();') -and
            $source.Contains('_diagnostics = new BrowserDiagnosticsModule(_session);') -and
            $source.Contains('Diagnostics => _diagnostics')) `
        'An account integration does not share its exact BrowserAccountSession with diagnostics.'
}

Assert (-not $agentProgram.Contains('--diagnostics') -and
        $agentProgram.Contains('app.MapAgentDiagnostics();')) `
    'Agent API diagnostics must be an ordinary always-mapped endpoint, not a launch mode.'

Assert ($diagnosticsApi.Contains('/v1/diagnostics/connections/{connectionId}/browser/{operation}') -and
        $diagnosticsApi.Contains('integration.Diagnostics.InvokeAsync')) `
    'Diagnostics API does not route through the integration-owned diagnostics capability.'

Assert (-not $solution.Contains('Mezhs.Diagnostics.Api') -and
        -not $solution.Contains('src\Mezhs.Diagnostics\Mezhs.Diagnostics.csproj')) `
    'Obsolete standalone diagnostics projects remain in the solution.'

foreach ($marker in @(
    'startsWith("$diagnostics/")',
    "case 'snapshot'",
    "case 'inspectPoint'",
    "case 'click'",
    "case 'type'",
    "case 'key'",
    "case 'screenshot'"
)) {
    Assert ($electron.Contains($marker)) "Electron diagnostic primitive is missing: $marker"
}

Assert ($electron.Contains('url: parsed.origin + parsed.pathname') -and
        -not $electron.Contains('headers: params.request.headers')) `
    'Diagnostic network observation is not limited to redacted method/path/status metadata.'

Assert ($electron.Contains("element.type === 'password' ? ''")) `
    'Diagnostic snapshots can expose password field values.'

Write-Host 'PASS: diagnostics reuse the integration-owned authenticated BrowserAccountSession, are ordinary Agent.Api endpoints, and keep generic browser probes in Electron.'
