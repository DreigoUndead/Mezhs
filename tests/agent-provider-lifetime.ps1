$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot

function Read-Source([string] $relativePath) {
    return [System.IO.File]::ReadAllText((Join-Path $root $relativePath))
}

$chatGpt = Read-Source 'integrations\Mezhs.Integrations.ChatGpt\ChatGptAccountProtocol.cs'
$integration = Read-Source 'integrations\Mezhs.Integrations.ChatGpt\ChatGptIntegration.cs'
$entry = Read-Source 'integrations\Mezhs.Integrations.ChatGpt\browser\modules\entry.js'
if ($chatGpt.Contains('sendNativeAccountMessage') -or
    $chatGpt.Contains('observeNativeConversationRequest') -or
    $chatGpt.Contains('waitForNativeConversationId') -or
    $entry.Contains('sendAccountMessage') -or $entry.Contains('newChat(context)') -or
    $entry.Contains('send(context)')) {
    throw 'ChatGPT Account must not retain a parallel JavaScript send protocol.'
}
if (-not $chatGpt.Contains('http.CallStreamAsync(') -or
    -not $integration.Contains('ChatGptAccountProtocol.SendAsync(')) {
    throw 'ChatGPT Account sending must use the C# typed semantic streaming API.'
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
# The outer Agent token remains the cancellation owner. A separate provider
# token is deliberately cancelled only after a bounded scoped Stop attempt.
if (-not $messageService.Contains('PromptWithStopOnCancellationAsync(') -or
    -not $messageService.Contains('integration.PromptAsync(chat, request, providerCancellation.Token)') -or
    -not $messageService.Contains('await integration.StopAsync(chat.Id, request.MessageId, timeout.Token)') -or
    -not $messageService.Contains('providerCancellation.Cancel();') -or
    $messageService.IndexOf('await integration.StopAsync(chat.Id, request.MessageId, timeout.Token)') -gt
      $messageService.IndexOf('providerCancellation.Cancel();')) {
    throw 'MessageService must preserve Agent cancellation and attempt scoped Stop before provider cancellation.'
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
if (-not $browserSession.Contains('_retainedChats.GetValueOrDefault(chatId) + 1') -or
    -not $browserSession.Contains('ReleaseContextBestEffortAsync(_transport, chatId)')) {
    throw 'Agent retention must be counted and ordinary/cancelled contexts must be retired locally.'
}
$registry = Read-Source 'src\Mezhs.Api\Services\IntegrationRegistry.cs'
if (-not $registry.Contains('Get(connectionId) is IConversationContextOwner owner') -or
    $registry.Contains('Task.WhenAll(_integrations.Values.OfType<IConversationContextOwner>()')) {
    throw 'Agent context retention/release must target only the selected browser connection.'
}
if (-not $agentWorker.Contains('ReleaseConversationAsync(execution.ConnectionId, execution.ChatId)')) {
    throw 'Agent execution finalization must release only its selected connection.'
}
$electron = Read-Source 'transports\Mezhs.Browser.Electron\ElectronBrowserTransport.cs'
if (-not $electron.Contains('await CancelOperationBestEffortAsync(operationId)')) {
    throw 'Electron cancellation must be ordered before a later same-chat local send.'
}

Write-Host 'PASS: Agent/provider lifetime ownership is explicit, Agent turns are unbounded, and cancellation reaches the provider boundary.'
