$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot

function Read-Source([string] $relativePath) {
    return [System.IO.File]::ReadAllText((Join-Path $root $relativePath))
}

$chatGpt = Read-Source 'integrations\Mezhs.Integrations.ChatGpt\browser\chatgpt.ts'
if ($chatGpt.Contains('canUseNativeSend') -or
    $chatGpt.Contains('sendNativeAccountMessage') -or
    $chatGpt.Contains('observeNativeConversationRequest') -or
    $chatGpt.Contains('waitForNativeConversationId')) {
    throw 'ChatGPT account sending must not depend on the browser UI/debugger path.'
}
if (-not $chatGpt.Contains('return sendApiAccountMessage(context, isNew, token, selection);')) {
    throw 'ChatGPT account sending must use the semantic API path.'
}

$agentWorker = Read-Source 'src\Mezhs.Agent.Api\Services\AgentWorker.cs'
if (-not $agentWorker.Contains('policy.Settings.Limits.TurnTimeoutSeconds')) {
    throw 'Agent turns are not bounded by the configured turn timeout.'
}
if (-not $agentWorker.Contains('CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)') -or
    -not $agentWorker.Contains('timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));')) {
    throw 'Agent turn timeout does not cancel the actual provider operation.'
}
if (-not $agentWorker.Contains('throw new TimeoutException(') -or
    -not $agentWorker.Contains('Agent turn timed out after {timeoutSeconds} seconds.')) {
    throw 'Agent turn timeout is not surfaced as a deterministic failure.'
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

Write-Host 'PASS: Agent/provider lifetime ownership is explicit, Agent turns are bounded, and cancellation reaches the provider boundary.'
