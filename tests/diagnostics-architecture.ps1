$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Read-Source([string]$relativePath) {
    [System.IO.File]::ReadAllText((Join-Path $root $relativePath))
}

function Assert([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

$contracts = Read-Source 'src\Mezhs.Integration.Abstractions\IntegrationRuntimeModels.cs'
$browserBase = Read-Source 'src\Mezhs.Integration.Browser\BrowserIntegrationBase.cs'
$browserDiagnostics = Read-Source 'src\Mezhs.Integration.Browser\BrowserDiagnostics.cs'
$accountBase = Read-Source 'src\Mezhs.Integration.Browser\BrowserAccountIntegrationBase.cs'
$chatGpt = Read-Source 'integrations\Mezhs.Integrations.ChatGpt\ChatGptIntegration.cs'
$grok = Read-Source 'integrations\Mezhs.Integrations.Grok\GrokIntegration.cs'
$gemini = Read-Source 'integrations\Mezhs.Integrations.Gemini\GeminiIntegration.cs'
$agentProgram = Read-Source 'src\Mezhs.Agent.Api\Program.cs'
$diagnosticsApi = Read-Source 'src\Mezhs.Api\MezhsApi.cs'
$electron = Read-Source 'electron\main.js'
$electronDiagnostics = Read-Source 'electron\diagnostics.js'
$solution = Read-Source 'Mezhs.sln'

Assert ($contracts.Contains('public virtual Task<JsonElement> DiagnoseAsync(') -and
        $contracts.Contains('public virtual Task LoginAsync(')) `
    'Integration operations must use direct virtual methods, not the legacy module interfaces.'

Assert ($browserDiagnostics.Contains('session.UseAuthorizedAsync') -and
        $browserDiagnostics.Contains('$diagnostics/{normalized}') -and
        $browserDiagnostics.Contains('"inspectRspackExports"')) `
    'Browser diagnostics must run only through the authorized session and allowlisted operations.'

Assert ($browserBase.Contains('private BrowserAccountSession? _accountSession;') -and
        $browserBase.Contains('lock (_sessionSync) return _accountSession ??= new(') -and
        $browserBase.Contains('private BrowserAccountSession? _guestSession;') -and
        $browserBase.Contains('lock (_sessionSync) return _guestSession ??= new(') -and
        $browserBase.Contains('public override async ValueTask DisposeAsync()') -and
        $browserBase.Contains('await _accountSession.DisposeAsync()') -and
        $browserBase.Contains('await _guestSession.DisposeAsync()')) `
    'BrowserIntegrationBase must retain one account/guest session owner and dispose both.'

Assert ($accountBase.Contains('AccountSession.LoginAsync(') -and
        $accountBase.Contains('AccountSession.OpenBrowserAsync(') -and
        $accountBase.Contains('BrowserDiagnostics.InvokeAsync(AccountSession') -and
        $accountBase.Contains('AccountSession.RetainConversationAsync(') -and
        $accountBase.Contains('AccountSession.ReleaseConversationAsync(')) `
    'Account base must reuse the existing authenticated session for login, diagnostics and chat context retention.'

foreach ($source in @($chatGpt, $grok)) {
    Assert ($source.Contains('BrowserAccountIntegrationBase') -and
            $source.Contains('AccountSession.UseAsync(') -and
            -not $source.Contains('private readonly BrowserAccountSession _session;') -and
            -not $source.Contains('IIntegrationDiagnosticsModule') -and
            -not $source.Contains('override ValueTask DisposeAsync()')) `
        'An account provider still duplicates session/diagnostics ownership.'
}

Assert ($chatGpt.Contains('SendAnonymousAsync(chat, request, cancellationToken)') -and
        $gemini.Contains('SendAnonymousAsync(chat, request, cancellationToken)')) `
    'Anonymous providers no longer use the base-owned ephemeral guest transport.'

Assert (-not $agentProgram.Contains('--diagnostics') -and
        $agentProgram.Contains('app.MapMezhsApi(deleteChats:')) `
    'Agent API must register the shared diagnostics endpoint through MapMezhsApi, not a separate launch mode.'

Assert ($diagnosticsApi.Contains('/v1/diagnostics/connections/{connectionId}/browser/{operation}') -and
        $diagnosticsApi.Contains('integration.DiagnoseAsync')) `
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
