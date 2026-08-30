$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$shared = Get-Content (Join-Path $root "src/Mezhs.Web.Lib/src/ChatSurface.tsx") -Raw
$exports = Get-Content (Join-Path $root "src/Mezhs.Web.Lib/src/index.ts") -Raw
$agentApp = Get-Content (Join-Path $root "src/Mezhs.Agent.Web/src/App.tsx") -Raw
$agentMain = Get-Content (Join-Path $root "src/Mezhs.Agent.Web/src/main.tsx") -Raw
$agentCss = Get-Content (Join-Path $root "src/Mezhs.Agent.Web/src/agent.css") -Raw

foreach ($component in @("ChatTranscript", "ChatComposer")) {
    if ($shared -notmatch "export function $component" -or $exports -notmatch $component) {
        throw "Shared MEZS web library does not export $component."
    }
    if ($agentApp -notmatch $component) {
        throw "Agent Web does not consume shared $component."
    }
}

if ($agentMain -notmatch '@mezhs/web-lib/styles\.css') {
    throw "Agent Web is not consuming the common MEZS web styling."
}

if ($agentApp -match '<article[^>]+className=\{`message' -or
    $agentApp -match '<form[^>]+className="composer"' -or
    $agentApp -match '<textarea') {
    throw "Agent Web reimplemented ordinary chat rendering/composer instead of using Mezhs.Web.Lib."
}

if ($agentCss -match '(?m)^\.message\s*\{' -or
    $agentCss -match '(?m)^\.composer\s*\{' -or
    $agentCss -match '(?m)^\.conversation\s*\{') {
    throw "Agent Web overrides shared chat surface styling instead of keeping agent-only chrome local."
}

if ($shared -notmatch 'autoScroll\?: boolean' -or
    $shared -notmatch 'followsBottomRef' -or
    $agentApp -notmatch 'autoScrollResetKey=\{selectedChat\.chatId\}') {
    throw "Agent transcript does not opt into the shared sticky auto-scroll behavior."
}

if ($agentApp -notmatch 'Start policy prompt' -or
    $agentApp -notmatch 'Exact prompt sent to the model for the first agent turn' -or
    $agentCss -notmatch '(?m)^\.agent-start-prompt') {
    throw "Agent Web does not expose the initial policy/bootstrap prompt as expandable chat evidence."
}

if ($agentApp -notmatch 'Command results JSON:' -or
    $agentApp -notmatch 'agent-command-result' -or
    $agentApp -notmatch '<details className="agent-command-result"' -or
    $agentCss -notmatch '(?m)^\.agent-command-result') {
    throw "Agent Web does not render command results as expandable in-chat evidence."
}

if ($agentApp -match 'Full command output and status are recorded in execution history') {
    throw "Agent Web still replaces command results with the old placeholder instead of showing evidence in chat."
}

Write-Host "PASS: Agent Web reuses shared chat UI and exposes sticky scroll, policy prompt, and expandable command evidence."
