# Guards Grok ownership boundaries, shared account lifecycle, and browser page-operation architecture.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot

$forbiddenRoots = @(
    (Join-Path $root 'src\Mezhs.Api'),
    (Join-Path $root 'src\Mezhs.Web'),
    (Join-Path $root 'transports\Mezhs.Browser.Electron'),
    (Join-Path $root 'electron')
)
$forbiddenFiles = $forbiddenRoots |
    ForEach-Object { Get-ChildItem $_ -Recurse -File -ErrorAction SilentlyContinue } |
    Where-Object Extension -in '.cs', '.ts', '.tsx', '.js'
$matches = Select-String -Path $forbiddenFiles.FullName -Pattern 'grok|xai' -CaseSensitive:$false
if ($matches) {
    $matches | ForEach-Object {
        Write-Error "$($_.Path):$($_.LineNumber): $($_.Line.Trim())"
    }
    throw 'Grok-specific behavior leaked outside the integration boundary.'
}

$grokProject = Join-Path $root 'integrations\Mezhs.Integrations.Grok\Mezhs.Integrations.Grok.csproj'
$grokIntegration = Join-Path $root 'integrations\Mezhs.Integrations.Grok\GrokIntegration.cs'
$grokBrowser = Join-Path $root 'integrations\Mezhs.Integrations.Grok\browser\grok.ts'
$accountSession = Join-Path $root 'src\Mezhs.Integration.Browser\BrowserAccountSession.cs'
$browserContract = Join-Path $root 'src\Mezhs.Integration.Browser\BrowserModule.d.ts'
foreach ($file in @($grokProject, $grokIntegration, $grokBrowser, $accountSession, $browserContract)) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "Missing Grok architecture file: $file"
    }
}

$grok = Get-Content $grokIntegration -Raw
if ($grok -notmatch '\[Integration\("grok-web-account"\)\]') {
    throw 'Grok account integration is not registered directly.'
}
if ($grok -notmatch 'BrowserAccountSession' -or $grok -notmatch 'ILoginModule') {
    throw 'Grok account integration is not using the shared account lifecycle and login contract.'
}

$grokBrowserSource = Get-Content $grokBrowser -Raw
if ($grokBrowserSource -notmatch 'pageOperations' -or $grokBrowserSource -match 'executeJavaScript') {
    throw 'Grok DOM behavior is not attached through browser page operations.'
}
$browserContractSource = Get-Content $browserContract -Raw
if ($browserContractSource -notmatch 'interface BrowserPage' -or
    $browserContractSource -notmatch 'pageOperations') {
    throw 'Shared browser TypeScript contract does not define page operations.'
}

$chatGpt = Get-Content (Join-Path $root 'integrations\Mezhs.Integrations.ChatGpt\ChatGptIntegration.cs') -Raw
if ($chatGpt -notmatch 'BrowserAccountSession') {
    throw 'ChatGPT account still owns a duplicate browser account lifecycle.'
}

$apiProject = Get-Content (Join-Path $root 'src\Mezhs.Api\Mezhs.Api.csproj') -Raw
if ($apiProject -notmatch 'Mezhs\.Integrations\.Grok') {
    throw 'Grok integration DLL is not included in API output.'
}

$config = Get-Content (Join-Path $root 'mezhs.yaml') -Raw
if ($config -notmatch '(?m)^\s*- id: grok-account\s*$' -or
    $config -notmatch '(?m)^\s*integration: grok-web-account\s*$') {
    throw 'Default Grok account connection is missing.'
}

Write-Host 'PASS: Grok stays integration-owned, uses shared account lifecycle, and attaches DOM work through the browser contract.'
