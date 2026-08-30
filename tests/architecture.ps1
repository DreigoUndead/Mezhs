$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$coreFiles = @(
    "src/Mezhs.Api/ApiExceptionHandler.cs",
    "src/Mezhs.Api/RequestExceptions.cs",
    "src/Mezhs.Api/Program.cs",
    "src/Mezhs.Api/Models/ApiModels.cs",
    "src/Mezhs.Api/Configuration/MezhsConfigLoader.cs",
    "src/Mezhs.Api/Configuration/MezhsOptions.cs",
    "src/Mezhs.Api/Services/IntegrationRegistry.cs",
    "src/Mezhs.Api/Services/IntegrationHost.cs",
    "src/Mezhs.Api/Services/MessageService.cs",
    "src/Mezhs.Web/ClientApp/src/App.tsx",
    "src/Mezhs.Web.Lib/src/MezhsChatApp.tsx",
    "src/Mezhs.Web.Lib/src/providers/contracts.ts",
    "src/Mezhs.Web.Lib/src/providers/apiChatProvider.ts",
    "src/Mezhs.Web.Lib/src/providers/registry.ts",
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
    "src/Mezhs.Api/ApiExceptionHandler.cs",
    "src/Mezhs.Api/RequestExceptions.cs",
    "src/Mezhs.Integration.Abstractions/Mezhs.Integration.Abstractions.csproj",
    "src/Mezhs.Integration.Browser/Mezhs.Integration.Browser.csproj",
    "src/Mezhs.Integration.Browser/IBrowserIntegrationHost.cs",
    "src/Mezhs.Integration.Browser/BrowserModule.d.ts",
    "integrations/Mezhs.Integrations.ChatGpt/Mezhs.Integrations.ChatGpt.csproj",
    "integrations/Mezhs.Integrations.ChatGpt/browser/chatgpt.ts",
    "integrations/Mezhs.Integrations.Gemini/Mezhs.Integrations.Gemini.csproj",
    "integrations/Mezhs.Integrations.Gemini/browser/gemini.ts",
    "integrations/Mezhs.Integrations.Mock/Mezhs.Integrations.Mock.csproj",
    "src/Mezhs.Web.Lib/package.json",
    "src/Mezhs.Web.Lib/src/index.ts",
    "src/Mezhs.Web.Lib/src/styles.css"
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
if ($coreIntegrationContract -match 'IIntegrationFactory|IntegrationFactory') {
    throw "Core integration abstractions still expose the obsolete factory layer."
}
if ($coreIntegrationContract -notmatch 'IntegrationAttribute') {
    throw "Concrete integration registration metadata is missing from the core contract."
}

$runtimeJavascript = Get-ChildItem (Join-Path $root "integrations") -Filter "*.js" -File -Recurse
if ($runtimeJavascript) {
    throw "Integration browser behavior has duplicate committed JavaScript sources: $($runtimeJavascript.FullName -join ', ')"
}

$legacyElectronModules = Get-ChildItem (Join-Path $root "electron/providers") -File -ErrorAction SilentlyContinue
if ($legacyElectronModules) {
    throw "Electron still owns integration-specific modules: $($legacyElectronModules.Name -join ', ')"
}

$frontendAdapters = Get-ChildItem (Join-Path $root "src/Mezhs.Web.Lib/src/providers") -Filter "*.provider.ts" -File
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
if ($registry -notmatch 'GetCustomAttributes<IntegrationAttribute>' -or $registry -match 'IIntegrationFactory|DiscoverFactories') {
    throw "Integration registry is not discovering concrete attributed integrations directly."
}
if ($registry -match 'integrationName') {
    throw "Connection metadata still exposes redundant integrationName."
}

$integrationSources = Get-ChildItem (Join-Path $root "integrations") -Filter "*.cs" -File -Recurse |
    ForEach-Object { Get-Content $_.FullName -Raw }
if (($integrationSources -join "`n") -match 'IntegrationFactory') {
    throw "An integration still contains a factory wrapper."
}

$browserContract = Get-Content (Join-Path $root "src/Mezhs.Integration.Browser/BrowserModule.d.ts") -Raw
if ($browserContract -match 'MezhsBrowser') {
    throw "Browser TypeScript contract names still carry redundant MezhsBrowser prefixes."
}

$chatStore = Get-Content (Join-Path $root "src/Mezhs.Api/Services/ChatStore.cs") -Raw
if ($chatStore -match 'SemaphoreSlim|ReadAllTextAsync|ReadAllLinesAsync|WriteAllTextAsync|AppendAllTextAsync') {
    throw "ChatStore still uses unnecessary async file-I/O machinery."
}

$fileStore = Get-Content (Join-Path $root "src/Mezhs.Api/Services/FileStore.cs") -Raw
if ($fileStore -match 'GetForConnection|belongs to another connection') {
    throw "Local files are still connection-owned instead of reusable across connections."
}

$messageService = Get-Content (Join-Path $root "src/Mezhs.Api/Services/MessageService.cs") -Raw
if ($messageService -match 'Task\.Run') {
    throw "MessageService still launches unowned fire-and-forget Task.Run work."
}
if ($messageService -notmatch 'BackgroundService' -or $messageService -notmatch 'Channel<StoredMessage>') {
    throw "MessageService does not own message processing through a host-managed queue."
}

$program = Get-Content (Join-Path $root "src/Mezhs.Api/Program.cs") -Raw
$apiExceptionHandler = Get-Content (Join-Path $root "src/Mezhs.Api/ApiExceptionHandler.cs") -Raw
$requestExceptions = Get-Content (Join-Path $root "src/Mezhs.Api/RequestExceptions.cs") -Raw
if ($program -match 'catch\s*\(\s*(ArgumentException|KeyNotFoundException)') {
    throw "Program endpoint mappings still translate domain exceptions with repeated try/catch blocks."
}
if ($program -notmatch 'AddExceptionHandler<ApiExceptionHandler>' -or
    $program -notmatch 'AddProblemDetails' -or
    $program -notmatch 'UseExceptionHandler') {
    throw "The API exception handler is not registered as middleware."
}
if ($apiExceptionHandler -notmatch 'Status400BadRequest' -or
    $apiExceptionHandler -notmatch 'Status404NotFound' -or
    $apiExceptionHandler -notmatch 'WriteAsJsonAsync') {
    throw "The API exception handler does not preserve the JSON 400/404 error contract."
}
if ($apiExceptionHandler -match 'ArgumentException|KeyNotFoundException' -or
    $requestExceptions -notmatch 'RequestValidationException' -or
    $requestExceptions -notmatch 'ResourceNotFoundException') {
    throw "The API exception policy uses ambiguous runtime exceptions instead of explicit request exceptions."
}

$models = Get-Content (Join-Path $root "src/Mezhs.Api/Models/ApiModels.cs") -Raw
$chatRecord = [regex]::Match($models, '(?s)public sealed class ChatRecord.*?(?=public sealed class ChatConnectionState)').Value
if ($chatRecord -match 'ConnectionId') {
    throw "ChatRecord still owns a single connection."
}
if ($models -notmatch 'ChatConnectionState' -or $models -notmatch 'RemoteStates') {
    throw "Per-chat/per-connection remote continuation state is missing."
}

$chatGpt = Get-Content (Join-Path $root "integrations/Mezhs.Integrations.ChatGpt/ChatGptIntegration.cs") -Raw
if ($chatGpt -notmatch 'ChatGptAccountIntegration\s*:\s*ChatGptWebIntegration') {
    throw "ChatGPT account integration must extend the common ChatGPT web integration."
}
if ($chatGpt -notmatch 'ILoginModule') {
    throw "ChatGPT account login is not exposed as a login module."
}
if ($chatGpt -match 'Task\.Run') {
    throw "ChatGPT idle disposal still wraps its timer in Task.Run."
}

$webHost = Get-Content (Join-Path $root "src/Mezhs.Web/ClientApp/src/App.tsx") -Raw
$app = Get-Content (Join-Path $root "src/Mezhs.Web.Lib/src/MezhsChatApp.tsx") -Raw
if ($webHost -notmatch 'MezhsChatApp' -or $webHost -match 'ChatProviderRegistry|sendMessage|uploadFile') {
    throw "Mezhs.Web is not a thin host over the shared generic web library."
}
$changeConnection = [regex]::Match($app, '(?s)function changeConnection\(id: string\).*?\n  }').Value
if ($changeConnection -match 'newChat') {
    throw "Changing the selected connection still discards the active local chat."
}
if ($app -match '>U<' -or $app -match '"U"') {
    throw "The shared web UI still contains the obsolete U brand mark."
}
if ($app -notmatch 'Delete selected' -or $app -notmatch 'Delete all shown') {
    throw "The shared web UI does not expose selected and filtered conversation deletion."
}
if ($app -notmatch 'managerSearch' -or $app -notmatch 'Filter by title, connection, or group') {
    throw "The conversation deletion window does not have its own filter."
}
if ($app -notmatch 'chat-options-menu' -or $app -notmatch "deleteConversations\(\[chat\].*false") {
    throw "The main conversation list does not expose the two-click options-menu delete action."
}

$config = Get-Content (Join-Path $root "mezhs.yaml") -Raw
if ($config -match '(?m)^\s*provider\s*:') {
    throw "Configuration still uses the obsolete provider key."
}
if ($config -match 'chatgpt-web-free|chatgpt-web-subscription') {
    throw "Obsolete ChatGPT free/subscription integration types are still configured."
}

Write-Host "PASS: chats are connection-neutral, message processing is owned, integrations register directly, browser contracts are concise, and provider-specific behavior stays outside core."
