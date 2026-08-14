$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$coreFiles = @(
    "src/Mezhs.Api/Program.cs",
    "src/Mezhs.Api/Models/ApiModels.cs",
    "src/Mezhs.Api/Configuration/MezhsConfigLoader.cs",
    "src/Mezhs.Api/Configuration/MezhsOptions.cs",
    "src/Mezhs.Api/Providers/IChatProvider.cs",
    "src/Mezhs.Api/Providers/IChatProviderFactory.cs",
    "src/Mezhs.Api/Providers/ProviderRegistry.cs",
    "src/Mezhs.Api/Providers/WebChatProvider.cs",
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
    throw "Provider-specific AI references were found in core files."
}

$requiredProviderFiles = @(
    "src/Mezhs.Api/Providers/ChatGpt/ChatGptSubscriptionProvider.cs",
    "src/Mezhs.Api/Providers/ChatGpt/ChatGptFreeProvider.cs",
    "src/Mezhs.Api/Providers/ChatGpt/ChatGptGuestProvider.cs",
    "src/Mezhs.Api/Providers/Gemini/GeminiGuestProvider.cs",
    "src/Mezhs.Web/ClientApp/src/providers/chatgptSubscription.provider.ts",
    "src/Mezhs.Web/ClientApp/src/providers/chatgptFree.provider.ts",
    "src/Mezhs.Web/ClientApp/src/providers/chatgptGuest.provider.ts",
    "src/Mezhs.Web/ClientApp/src/providers/geminiGuest.provider.ts"
)
foreach ($file in $requiredProviderFiles) {
    if (-not (Test-Path (Join-Path $root $file))) {
        throw "Missing provider implementation file: $file"
    }
}

Write-Host "PASS: core is provider-neutral and every configured chat type has separate C# and TypeScript modules."
