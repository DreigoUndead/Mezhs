$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot

function Read-Source([string] $relativePath) {
    return [System.IO.File]::ReadAllText((Join-Path $root $relativePath))
}

$chatGpt = Read-Source 'integrations\Mezhs.Integrations.ChatGpt\browser\modules\account-messages.js'
if ($chatGpt.Contains('canUseNativeSend') -or
    $chatGpt.Contains('sendNativeAccountMessage') -or
    $chatGpt.Contains('observeNativeConversationRequest') -or
    $chatGpt.Contains('waitForNativeConversationId')) {
    throw 'ChatGPT account sending must not depend on the browser UI/debugger path.'
}
if (-not $chatGpt.Contains('return sendApiAccountMessage(context, isNew, token, selection);')) {
    throw 'ChatGPT account sending must use the semantic API path.'
}

$policyModels = Read-Source 'src\Mezhs.Agent.Api\Policy\PolicyModels.cs'
if ($policyModels.Contains('MaxTurns') -or $policyModels.Contains('TurnTimeoutSeconds')) {
    throw 'Agent policy must not impose turn-count or turn-duration limits.'
}

$agentWorker = Read-Source 'src\Mezhs.Agent.Api\Services\AgentWorker.cs'
if ($agentWorker.Contains('ValidateTurn(') -or
    $agentWorker.Contains('SendTurnAsync(') -or
    $agentWorker.Contains('CancelAfter(')) {
    throw 'Agent runtime still contains a turn-count or turn-duration limit.'
}

$messageService = Read-Source 'src\Mezhs.Api\Services\MessageService.cs'
if (-not $messageService.Contains('Channel<QueuedMessage>')) {
    throw 'MessageService queue must carry the operation cancellation token.'
}
if (-not $messageService.Contains('running.Add(ProcessAsync(queued.Message, queued.CancellationToken));')) {
    throw 'MessageService must preserve queued cancellation into processing.'
}
if ($messageService -notmatch '(?s)SendMessageAsync\(\s*new IntegrationSendContext\(.*?\),\s*cancellationToken\);') {
    throw 'MessageService must pass the operation cancellation token into the integration.'
}
if (-not $messageService.Contains('message.Status = MessageStatus.Cancelled;')) {
    throw 'Cancelled provider work must be persisted as Cancelled.'
}

$browserSession = Read-Source 'src\Mezhs.Integration.Browser\BrowserAccountSession.cs'
if ($browserSession.Contains('await DisposeTransportAsync();')) {
    throw 'One cancelled Agent turn must not dispose the shared browser host.'
}
if (-not $browserSession.Contains('ForContext(chatId, ephemeral)')) {
    throw 'BrowserAccountSession must route operations into their own conversation context.'
}
$electron = Read-Source 'transports\Mezhs.Browser.Electron\ElectronBrowserTransport.cs'
if (-not $electron.Contains('CancelOperationBestEffortAsync(operationId)')) {
    throw 'Electron cancellation must target the affected operation, not its shared host.'
}

Write-Host 'PASS: Agent/provider lifetime ownership is explicit, Agent turns are unbounded, and cancellation reaches the provider boundary.'
