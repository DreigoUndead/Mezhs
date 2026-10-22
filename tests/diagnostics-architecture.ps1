$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Read-Source([string]$relativePath) {
    [System.IO.File]::ReadAllText((Join-Path $root $relativePath))
}

function Assert([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

$contracts = Read-Source 'src\Mezhs.Integration.Abstractions\IntegrationContracts.cs'
$browserBase = Read-Source 'src\Mezhs.Integration.Browser\BrowserIntegrationBase.cs'
$browserDiagnostics = Read-Source 'src\Mezhs.Integration.Browser\BrowserDiagnosticsModule.cs'
$chatGpt = Read-Source 'integrations\Mezhs.Integrations.ChatGpt\ChatGptIntegration.cs'
$grok = Read-Source 'integrations\Mezhs.Integrations.Grok\GrokIntegration.cs'
$gemini = Read-Source 'integrations\Mezhs.Integrations.Gemini\GeminiIntegration.cs'
$agentProgram = Read-Source 'src\Mezhs.Agent.Api\Program.cs'
$diagnosticsApi = Read-Source 'src\Mezhs.Api\MezhsApi.cs'
$electron = Read-Source 'electron\main.js'
$electronDiagnostics = Read-Source 'electron\diagnostics.js'
$solution = Read-Source 'Mezhs.sln'

Assert ($contracts.Contains('public interface IIntegrationDiagnosticsModule') -and
        $contracts.Contains('IIntegrationDiagnosticsModule? Diagnostics')) `
    'Diagnostics are not an optional integration capability.'

Assert ($browserDiagnostics.Contains('BrowserDiagnosticsModule(BrowserAccountSession session)') -and
        $browserDiagnostics.Contains('session.UseAuthorizedAsync') -and
        $browserDiagnostics.Contains('$diagnostics/{normalized}')) `
    'Browser diagnostics do not execute through the owning authenticated BrowserAccountSession.'

Assert ($browserBase.Contains('private BrowserAccountSession? _accountSession;') -and
        $browserBase.Contains('CreateAccountSession() => _accountSession ??= new(') -and
        $browserBase.Contains('public override IIntegrationDiagnosticsModule? Diagnostics') -and
        $browserBase.Contains('_accountSession is null ? null : _diagnostics ??=') -and
        $browserBase.Contains('new BrowserDiagnosticsModule(_accountSession)') -and
        $browserBase.Contains('public override ValueTask DisposeAsync()')) `
    'BrowserIntegrationBase must cache and dispose one account session, exposing diagnostics only for accounts.'

foreach ($source in @($chatGpt, $grok)) {
    Assert ($source.Contains('var session = CreateAccountSession();') -and
            $source.Contains('CreateAccountSession().UseAsync(') -and
            -not $source.Contains('private readonly BrowserAccountSession _session;') -and
            -not $source.Contains('private readonly IIntegrationDiagnosticsModule _diagnostics;') -and
            -not $source.Contains('override IIntegrationDiagnosticsModule Diagnostics') -and
            -not $source.Contains('override ValueTask DisposeAsync()')) `
        'An account integration still duplicates base session/diagnostics ownership.'
}

Assert ($chatGpt.Contains('SendAnonymousAsync(context, cancellationToken)') -and
        $gemini.Contains('SendAnonymousAsync(context, cancellationToken)')) `
    'Anonymous providers no longer use temporary anonymous transports.'

Assert (-not $agentProgram.Contains('--diagnostics') -and
        $agentProgram.Contains('app.MapMezhsApi();')) `
    'Agent API must register the shared diagnostics endpoint through MapMezhsApi, not a separate launch mode.'

Assert ($diagnosticsApi.Contains('/v1/diagnostics/connections/{connectionId}/browser/{operation}') -and
        $diagnosticsApi.Contains('integration.Diagnostics.InvokeAsync')) `
    'Diagnostics API does not route through the integration-owned diagnostics capability.'

Assert (-not $solution.Contains('Mezhs.Diagnostics.Api') -and
        -not $solution.Contains('src\Mezhs.Diagnostics\Mezhs.Diagnostics.csproj')) `
    'Obsolete standalone diagnostics projects remain in the solution.'

Assert ($electron.Contains('require("./diagnostics")') -and
        $electron.Contains('startsWith("$diagnostics/")') -and
        $electron.Contains('invokeDiagnostic(')) `
    'Electron main does not delegate diagnostic requests to its diagnostics module.'

foreach ($marker in @(
    "case 'snapshot'",
    "case 'inspectPoint'",
    "case 'click'",
    "case 'type'",
    "case 'key'",
    "case 'screenshot'"
)) {
    Assert ($electronDiagnostics.Contains($marker)) "Electron diagnostic primitive is missing: $marker"
}

Assert ($electronDiagnostics.Contains('url: parsed.origin + parsed.pathname') -and
        -not $electronDiagnostics.Contains('headers: params.request.headers')) `
    'Diagnostic network observation is not limited to redacted method/path/status metadata.'

Assert ($electronDiagnostics.Contains("element.type === 'password' ? ''")) `
    'Diagnostic snapshots can expose password field values.'

Write-Host 'PASS: diagnostics reuse the base-owned authenticated BrowserAccountSession, are shared API endpoints, and keep generic browser probes in Electron.'
