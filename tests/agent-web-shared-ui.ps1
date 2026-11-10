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
$baseCss = Get-Content (Join-Path $root "src/Mezhs.Web.Lib/src/styles.css") -Raw

foreach ($component in @("ChatTranscript", "ChatComposer", "MarkdownContent", "ConnectionModelPicker")) {
    if ($exports -notmatch $component) { throw "Shared MEZS web library does not export $component." }
}
if ($agentApp -notmatch 'ChatTranscript' -or $agentApp -notmatch 'ChatComposer') {
    throw "Agent Web does not consume the shared chat surface."
}
if ($shared -notmatch 'sideControls\?: ReactNode' -or
    $shared -notmatch 'topContent\?: ReactNode' -or
    $shared -notmatch 'composer-top-content' -or
    $shared -notmatch 'composer-side-controls' -or
    $agentApp -notmatch 'sideControls=\{\(' -or
    $agentApp -match 'agent-target-picker-header|agent-target-picker-new') {
    throw "Agent target controls are not consistently owned by the prompt composer."
}
if ($agentApp -match 'className="agent-new-chat"|agent-new-chat-controls|agent-policy-row' -or
    $agentApp -notmatch 'topContent=\{selectedPolicy' -or
    $agentApp -notmatch 'className="agent-policy-overview"' -or
    $agentApp -notmatch 'className="agent-composer-settings"' -or
    $agentApp -notmatch 'className="agent-policy-readonly"' -or
    $agentApp -notmatch 'policy\.name') {
    throw "Agent policy UI is not integrated into the composer with new-chat details and existing-chat read-only state."
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
if ($shared -match '>\^</button>' -or
    $shared -notmatch '<svg viewBox="0 0 20 20"' -or
    $shared -notmatch 'aria-label="Send message"') {
    throw "Shared composer still uses a text glyph instead of the send icon."
}
if ($agentCss -match 'min-height:\s*108px' -or
    $agentCss -notmatch 'color-scheme:\s*light' -or
    $agentCss -notmatch 'option:checked' -or
    $agentCss -notmatch 'background:\s*#fffefa') {
    throw "Agent composer is not compact or its native target dropdown options can fall back to the dark shared theme."
}
if ($shared -notmatch '<MarkdownContent content=\{message\.content\}' -or
    $sharedApp -notmatch '<MarkdownContent content=\{message\.content\}' -or
    $markdown -notmatch 'remarkGfm' -or $markdown -notmatch 'remarkRichMarkup' -or $markdown -match 'StaticMarkup') {
    throw "Both chat surfaces must render messages through the shared Markdown owner."
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
    $agentApp -notmatch 'loadMessages\(chatId, syncModel, signal\)' -or
    $agentApp -notmatch 'loadExecutions\(chatId, signal\)') {
    throw "Agent Web couples durable execution refresh to the remote chat-message request."
}

# Shared chat-management semantics and compact sidebar structure on both web surfaces.
if ($agentCss -notmatch 'agent-sidebar-tools[\s\S]*?gap:\s*6px' -or
    $baseCss -notmatch '\.base-group-filter' -or
    $baseCss -notmatch '\.base-sidebar-tools' -or
    $sharedApp -notmatch 'className="base-group-filter"' -or
    $sharedApp -notmatch 'className="base-sidebar-tools"' -or
    $sharedApp -notmatch 'async function renameChat' -or
    $agentApp -notmatch 'async function renameChat' -or
    $sharedApp -notmatch 'method: "PUT"' -or
    $agentApp -notmatch 'method: "PUT"' -or
    $sharedApp -match 'setCategoryFilter\(category\.categoryId\)' -or
    $agentApp -match 'selectCategoryFilter\(category\.categoryId\)') {
    throw "Base and Agent chat group controls, rename actions, or non-switching group creation have diverged."
}

# Both web surfaces can operate against the Agent host, where group removal also
# unassigns Agent conversations and chat deletion retains Executor audit history.
if ($sharedApp -notmatch 'including Agent conversations if present' -or
    $sharedApp -notmatch 'Historical Executor records and stored files may remain' -or
    $sharedApp -notmatch 'deletedChatIdsRef\.current\.has\(chat\.chatId\)' -or
    $sharedApp -notmatch 'deletedChatIdsRef\.current\.add\(id\)') {
    throw "Base chat deletion/group confirmations or stale-deletion list protection have regressed."
}

# Normal API Web group deletion is three-choice; bulk deletion must use the
# shared Core/Agent chat endpoint before category removal, without a native confirm.
$groupDialogStart = $sharedApp.IndexOf('async function promptDeleteCategory(')
$groupWorkflowStart = $sharedApp.IndexOf('async function deleteCategory(mode:')
$groupWorkflowEnd = $sharedApp.IndexOf('function openConversationManager()', $groupWorkflowStart)
if ($groupDialogStart -lt 0 -or $groupWorkflowStart -lt 0 -or $groupWorkflowEnd -le $groupWorkflowStart) {
    throw "Normal Web group deletion dialog/workflow is missing."
}
$groupWorkflow = $sharedApp.Substring($groupWorkflowStart, $groupWorkflowEnd - $groupWorkflowStart)
if ($sharedApp -notmatch 'role="dialog" aria-modal="true"' -or
    $sharedApp -notmatch 'groupDeleteDialog.chatIds.length' -or
    $sharedApp -notmatch 'deleteCategory\("unassign"\)' -or
    $sharedApp -notmatch 'deleteCategory\("delete"\)' -or
    $sharedApp -notmatch '>Cancel</button>' -or
    $sharedApp -notmatch 'group-delete-disclosure' -or
    $sharedApp -notmatch 'role="alert"' -or
    $sharedApp -match 'window.confirm\(`Delete group' -or
    $baseCss -notmatch '\.group-delete-dialog' -or
    $baseCss -notmatch '\.group-delete-choices button.destructive') {
    throw "Normal Web group deletion must present unassign/delete/cancel with count and disclosure."
}
if ($groupWorkflow -notmatch 'await apiJson<Chat\[\]>\(apiBase, "/v1/chats"\)' -or
    $groupWorkflow -notmatch 'Group membership changed' -or
    $groupWorkflow -notmatch 'method: "DELETE"' -or
    $groupWorkflow -notmatch '"/v1/chats"' -or
    $groupWorkflow -notmatch '/v1/categories/' -or
    $groupWorkflow -notmatch 'deletedChatIdsRef.current.add' -or
    $groupWorkflow -notmatch 'if \(chatId && removed.has\(chatId\)\) newChat\(\)') {
    throw "Normal Web group destructive deletion bypasses the shared chat API or stale-data guard."
}
$chatDeleteOffset = $groupWorkflow.IndexOf('if (mode === "delete"')
$categoryDeleteOffset = $groupWorkflow.IndexOf('/v1/categories/')
# Source guard: the category delete request must come after the conditional chat delete.
if ($chatDeleteOffset -lt 0 -or $categoryDeleteOffset -le $chatDeleteOffset -or
    $categoryDeleteOffset -lt $chatDeleteOffset) {
    throw "Group removal must follow completed conversation removal."
}

node (Join-Path $root "tests/agent-web-rich-formatting.test.cjs")
if ($LASTEXITCODE -ne 0) { throw "Shared chat rich-formatting regressions failed." }

Write-Host "PASS: shared chat/target controls, generic provider model discovery, independent Agent execution refresh, and canonical Agent protocol/evidence rendering are wired through their owning components."
