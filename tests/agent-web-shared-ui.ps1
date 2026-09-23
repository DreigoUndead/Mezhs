$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$shared = Get-Content (Join-Path $root "src/Mezhs.Web.Lib/src/ChatSurface.tsx") -Raw
$markdown = Get-Content (Join-Path $root "src/Mezhs.Web.Lib/src/MarkdownContent.tsx") -Raw
$resize = Get-Content (Join-Path $root "src/Mezhs.Web.Lib/src/useAutoResizeTextArea.ts") -Raw
$targetPicker = Get-Content (Join-Path $root "src/Mezhs.Web.Lib/src/ConnectionModelPicker.tsx") -Raw
$sharedApp = Get-Content (Join-Path $root "src/Mezhs.Web.Lib/src/MezhsChatApp.tsx") -Raw
$exports = Get-Content (Join-Path $root "src/Mezhs.Web.Lib/src/index.ts") -Raw
$agentApp = Get-Content (Join-Path $root "src/Mezhs.Agent.Web/src/App.tsx") -Raw
$agentProgram = Get-Content (Join-Path $root "src/Mezhs.Agent.Api/Program.cs") -Raw
$agentMapper = Get-Content (Join-Path $root "src/Mezhs.Agent.Api/Models/AgentApiMapper.cs") -Raw
$agentMain = Get-Content (Join-Path $root "src/Mezhs.Agent.Web/src/main.tsx") -Raw
$agentCss = Get-Content (Join-Path $root "src/Mezhs.Agent.Web/src/agent.css") -Raw

foreach ($component in @("ChatTranscript", "ChatComposer", "MarkdownContent", "ConnectionModelPicker")) {
    if ($exports -notmatch $component) { throw "Shared MEZS web library does not export $component." }
}
if ($agentApp -notmatch 'ChatTranscript' -or $agentApp -notmatch 'ChatComposer') {
    throw "Agent Web does not consume the shared chat surface."
}
if ($agentMain -notmatch '@mezhs/web-lib/styles\.css') {
    throw "Agent Web is not consuming common MEZS web styling."
}
if ($targetPicker -notmatch 'target-picker-field' -or
    $targetPicker -match 'className="connection-picker"|className="model-picker"|connection-avatar' -or
    $sharedApp -notmatch '<ConnectionModelPicker' -or $agentApp -notmatch '<ConnectionModelPicker') {
    throw "Normal chat and Agent Web do not reuse one symmetric integration/model picker."
}
if ($agentApp -notmatch 'ChatProviderRegistry' -or
    $agentApp -match '/v1/connections/\$\{encodeURIComponent\([^)]*\)\}/models') {
    throw "Agent Web bypasses the generic chat-provider model discovery abstraction."
}
if ($agentApp -notmatch 'connectionId,' -or
    $agentApp -notmatch 'onConnectionChange=\{selectTargetConnection\}') {
    throw "Agent Web does not expose per-turn generic connection selection."
}
if ($agentApp -match 'manual-chat-configs|ManualChatConfig|manualConfig' -or
    $agentApp -notmatch 'defaultModel') {
    throw "Agent Web still has a manual preset layer instead of using policy-owned defaults."
}
if ($agentApp -match '<textarea' -or $agentApp -match '<form[^>]+className="composer"') {
    throw "Agent Web reimplemented the shared composer."
}
if ($shared -notmatch '<MarkdownContent content=\{message\.content\}' -or
    $markdown -notmatch '```' -or $markdown -notmatch 'safeLink') {
    throw "Shared chat content is not rendered through the safe Markdown owner."
}
if ($resize -notmatch 'useLayoutEffect' -or $resize -notmatch 'scrollHeight') {
    throw "Shared composer resize is not performed before paint from the textarea's measured content."
}
if ($agentApp -notmatch 'candidate\.triggerMessageId === message\.messageId' -or
    $agentApp -notmatch 'candidate\.commandIndex === command\.commandIndex') {
    throw "Agent command cards reconstruct execution state instead of using durable command identity."
}
if ($agentApp -match 'inspectProtocol|const commandName' -or
    $agentApp -notmatch 'message\.displayContent' -or
    $agentApp -notmatch 'message\.commands' -or
    $agentMapper -notmatch 'parser\.Parse\(message\.Content\)' -or
    $agentProgram -notmatch 'AgentApiMapper\.ToView\(message, parser\)') {
    throw "Agent Web still owns a duplicate command-protocol parser instead of consuming Agent API semantics."
}
if ($agentApp -match 'candidate\.request\.trim\(\) === body' -or $agentApp -match 'const used = new Set') {
    throw "Agent command cards still guess execution linkage from command body text."
}
if ($agentApp -notmatch 'agent-command-evidence' -or
    $agentCss -notmatch '(?m)^\.agent-command-result' -or
    $agentCss -notmatch '(?m)^\.agent-command-request') {
    throw "Agent command request/result presentation is not using the canonical evidence styling."
}
if ($agentApp -notmatch 'CancelRequested' -or $agentApp -notmatch 'stopping') {
    throw "Agent Web does not surface the cancellation acknowledgement lifecycle."
}
if ($agentApp -notmatch 'Download log' -or $agentApp -notmatch '/debug-log') {
    throw "Agent Web no longer exposes authenticated debug-log download through its proxy."
}
if ($agentApp -notmatch 'Promise\.allSettled' -or
    $agentApp -notmatch '\.then\(setMessages\)' -or
    $agentApp -notmatch '\.then\(setExecutions\)') {
    throw "Agent Web couples durable execution refresh to the remote chat-message request."
}

Write-Host "PASS: shared chat/target controls, generic provider model discovery, independent Agent execution refresh, and canonical Agent protocol/evidence rendering are wired through their owning components."
