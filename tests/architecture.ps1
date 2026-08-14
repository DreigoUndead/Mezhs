$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$coreFiles = @(
    "src/Mezhs.Api/Program.cs",
    "src/Mezhs.Api/Models/ApiModels.cs",
    "src/Mezhs.Api/Configuration/MezhsConfigLoader.cs",
    "src/Mezhs.Api/Configuration/MezhsOptions.cs",
    "src/Mezhs.Api/Services/IntegrationRegistry.cs",
    "src/Mezhs.Api/Services/IntegrationHost.cs",
    "src/Mezhs.Api/Services/MessageService.cs",
    "src/Mezhs.Web/ClientApp/src/App.tsx",
    "src/Mezhs.Web/ClientApp/src/providers/contracts.ts",
    "src/Mezhs.Web/ClientApp/src/providers/apiChatProvider.ts",
    "src/Mezhs.Web/ClientApp/src/providers/registry.ts",
    "electron/main.js"
)
$coreFiles += Get-ChildItem "$root/transports/Mezhs.Browser.Abstractions" -Recurse -File |
    Where-Object Extension -in ".cs", ".csproj" |
    ForEach-Object FullName
$coreFiles += Get-ChildItem "$root/transports/Mezhs.Browser.Electron" -Recurse -File |
    Where-Object Extension -in ".cs", ".csproj" |
    ForEach-Object FullName

$resolved = $coreFiles | ForEach-Object {
    if ([System.IO.Path]::IsPathRooted($_)) { $_ } else { Join-Path $root $_ }
}
$forbidden = "chatgpt|gemini|openai"
$matches = Select-String -Path $resolved -Pattern $forbidden -CaseSensitive:$false
if ($matches) {
    $matches | ForEach-Object { Write-Error "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }
    throw "Integration-specific AI references were found in core files."
}

$requiredFiles = @(
    "src/Mezhs.Integration.Abstractions/Mezhs.Integration.Abstractions.csproj",
    "src/Mezhs.Integration.Browser/Mezhs.Integration.Browser.csproj",
    "src/Mezhs.Integration.Browser/IBrowserIntegrationHost.cs",
    "src/Mezhs.Integration.Browser/BrowserModule.d.ts",
    "integrations/Mezhs.Integrations.ChatGpt/Mezhs.Integrations.ChatGpt.csproj",
    "integrations/Mezhs.Integrations.ChatGpt/browser/chatgpt.ts",
    "integrations/Mezhs.Integrations.Gemini/Mezhs.Integrations.Gemini.csproj",
    "integrations/Mezhs.Integrations.Gemini/browser/gemini.ts",
    "integrations/Mezhs.Integrations.Mock/Mezhs.Integrations.Mock.csproj"
)
foreach ($file in $requiredFiles) {
    if (-not (Test-Path (Join-Path $root $file))) {
        throw "Missing integration architecture file: $file"
    }
}

$coreIntegrationProject = Get-Content (Join-Path $root "src/Mezhs.Integration.Abstractions/Mezhs.Integration.Abstractions.csproj") -Raw
$coreIntegrationContract = Get-Content (Join-Path $root "src/Mezhs.Integration.Abstractions/IntegrationContracts.cs") -Raw
if ($coreIntegrationProject -match 'Browser' -or $coreIntegrationContract -match 'IChatBrowserTransport|BrowserIdleMinutes|CreateBrowserTransport') {
    throw "Core integration abstractions still depend on browser hosting."
}

$runtimeJavascript = Get-ChildItem (Join-Path $root "integrations") -Filter "*.js" -File -Recurse
if ($runtimeJavascript) {
    throw "Integration browser behavior has duplicate committed JavaScript sources: $($runtimeJavascript.FullName -join ', ')"
}

$legacyElectronModules = Get-ChildItem (Join-Path $root "electron/providers") -File -ErrorAction SilentlyContinue
if ($legacyElectronModules) {
    throw "Electron still owns integration-specific modules: $($legacyElectronModules.Name -join ', ')"
}

$frontendAdapters = Get-ChildItem (Join-Path $root "src/Mezhs.Web/ClientApp/src/providers") -Filter "*.provider.ts" -File
if ($frontendAdapters) {
    throw "Frontend still duplicates integration registrations: $($frontendAdapters.Name -join ', ')"
}

$legacyApiProviders = Get-ChildItem (Join-Path $root "src/Mezhs.Api/Providers") -File -Recurse -ErrorAction SilentlyContinue
if ($legacyApiProviders) {
    throw "API still contains the obsolete provider implementation path: $($legacyApiProviders.FullName -join ', ')"
}

$electron = Get-Content (Join-Path $root "electron/main.js") -Raw
if ($electron -match 'MEZHS_AUTOMATION_ID|MEZHS_REQUIRE_LOGIN|providers[/\\]') {
    throw "Electron still uses the old provider-specific automation contract."
}
if ($electron -notmatch 'MEZHS_BROWSER_MODULE' -or $electron -notmatch 'MEZHS_REQUIRE_AUTHORIZATION') {
    throw "Electron is not using the generic browser module contract."
}

$transport = Get-Content (Join-Path $root "transports/Mezhs.Browser.Electron/ElectronBrowserTransport.cs") -Raw
if ($transport -match 'AutomationId|MEZHS_AUTOMATION_ID|MEZHS_REQUIRE_LOGIN') {
    throw "Electron transport still exposes provider-specific automation state."
}

$registry = Get-Content (Join-Path $root "src/Mezhs.Api/Services/IntegrationRegistry.cs") -Raw
if ($registry -notmatch 'Mezhs\.Integrations\.\*\.dll' -or $registry -notmatch 'AssemblyLoadContext') {
    throw "Integration registry is not discovering integration DLLs dynamically."
}

$chatGpt = Get-Content (Join-Path $root "integrations/Mezhs.Integrations.ChatGpt/ChatGptIntegration.cs") -Raw
if ($chatGpt -notmatch 'ChatGptAccountIntegration\s*:\s*ChatGptWebIntegration') {
    throw "ChatGPT account integration must extend the common ChatGPT web integration."
}
if ($chatGpt -notmatch 'ILoginModule') {
    throw "ChatGPT account login is not exposed as a login module."
}

$config = Get-Content (Join-Path $root "mezhs.yaml") -Raw
if ($config -match '(?m)^\s*provider\s*:') {
    throw "Configuration still uses the obsolete provider key."
}
if ($config -match 'chatgpt-web-free|chatgpt-web-subscription') {
    throw "Obsolete ChatGPT free/subscription integration types are still configured."
}

Write-Host "PASS: integrations are dynamic, browser hosting is optional, and integration-specific behavior does not leak into Electron, API core, or React."
