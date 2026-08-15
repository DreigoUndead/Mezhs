$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$config = Get-Content (Join-Path $root 'mezhs.yaml') -Raw

if ($config -notmatch '(?m)^\s*- id: chatgpt-sub\s*$') {
    throw 'Default ChatGPT account connection id changed. Connection ids own persistent browser profile identity and must remain stable across display/integration renames.'
}

Write-Host 'PASS: default ChatGPT account keeps its stable persistent profile identity.'
