$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot

function Read-Source([string] $relativePath) {
    return [System.IO.File]::ReadAllText((Join-Path $root $relativePath))
}

$chatGpt = Read-Source 'integrations\Mezhs.Integrations.ChatGpt\browser\chatgpt.ts'
$triggerIndex = $chatGpt.IndexOf('await trigger();', [StringComparison]::Ordinal)
$timeoutIndex = $chatGpt.IndexOf('const timeout = setTimeout(', [StringComparison]::Ordinal)
if ($triggerIndex -lt 0 -or $timeoutIndex -lt 0 -or $timeoutIndex -lt $triggerIndex) {
    throw 'ChatGPT native request timeout must start only after submitPrompt has returned.'
}

$messageService = Read-Source 'src\Mezhs.Api\Services\MessageService.cs'
if (-not $messageService.Contains('Channel<QueuedMessage>')) {
    throw 'MessageService queue must carry the operation cancellation token.'
}
if (-not $messageService.Contains('running.Add(ProcessAsync(queued.Message, queued.CancellationToken));')) {
    throw 'MessageService must preserve queued cancellation into processing.'
}
if (-not $messageService.Contains('RestoreConversation: !continueRemote),' + [Environment]::NewLine + '                cancellationToken);')) {
    throw 'MessageService must pass the operation cancellation token into the integration.'
}
if (-not $messageService.Contains('message.Status = MessageStatus.Cancelled;')) {
    throw 'Cancelled provider work must be persisted as Cancelled.'
}

$browserSession = Read-Source 'src\Mezhs.Integration.Browser\BrowserAccountSession.cs'
if (-not $browserSession.Contains('catch (OperationCanceledException)')) {
    throw 'BrowserAccountSession must own cancellation of its transport operation.'
}
if (-not $browserSession.Contains('await DisposeTransportAsync();')) {
    throw 'BrowserAccountSession cancellation must discard the possibly still-running transport.'
}

Write-Host 'PASS: Agent/provider lifetime ownership is explicit and cancellation reaches the provider boundary.'
