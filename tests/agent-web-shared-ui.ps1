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

Write-Host "PASS: Agent Web reuses shared MEZS transcript, composer and common styling."
