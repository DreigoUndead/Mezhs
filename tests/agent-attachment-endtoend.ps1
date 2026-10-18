$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([IO.Path]::GetTempPath()) ('mezhs-agent-attach-e2e-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$cfg = Join-Path $temp 'agent.yaml'
$dataRoot = Join-Path $temp 'data'
$agentStore = Join-Path $temp 'agent.sqlite'
$stdout = Join-Path $temp 'agent-out.txt'
$stderr = Join-Path $temp 'agent-err.txt'
$txt = Join-Path $temp 'verify.txt'
$png = Join-Path $temp 'verify.png'
$agent = $null
function Check($condition, $label) {
  if (-not $condition) { throw "FAIL: $label" }
  Write-Output "PASS: $label"
}
function WaitHealth([string]$uri) {
  $deadline = [DateTimeOffset]::UtcNow.AddSeconds(25)
  while ([DateTimeOffset]::UtcNow -lt $deadline) {
    try {
      if ((Invoke-RestMethod -Uri $uri -TimeoutSec 2).status -eq 'ok') { return }
    } catch {}
    Start-Sleep -Milliseconds 150
  }
  throw 'Server never became healthy.'
}
function WaitExecution([string]$id) {
  $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
  while ([DateTimeOffset]::UtcNow -lt $deadline) {
    $e = Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/executions/$id"
    if ($e.status -in @('Completed','Failed','Cancelled')) { return $e }
    Start-Sleep -Milliseconds 100
  }
  throw "Execution did not terminate: $id"
}
try {
  Set-Content -LiteralPath $txt -Value 'MEZHS-ATTACH-TXT-E2E-20261019' -Encoding UTF8
  [IO.File]::WriteAllBytes($png, [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScL/nwAAAABJRU5ErkJggg=='))
  $config = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'mezhs.agent.test.yaml') -Raw)
  $config = $config.Replace('allow: [SH]', 'allow: [SH, ATTACH]')
  $config = $config.Replace('data-agent-test/core', ($dataRoot.Replace('\','/')))
  $config = $config.Replace('data-agent-test/agent.sqlite', ($agentStore.Replace('\','/')))
  $config = $config.Replace('electronDirectory: ../electron', 'electronDirectory: ' + (Join-Path $root 'electron').Replace('\','/'))
  $config = $config.Replace('workspace: ..', 'workspace: ' + $root.Replace('\','/'))
  Set-Content -LiteralPath $cfg -Value $config -Encoding UTF8
  $dll = Join-Path $root 'src\Mezhs.Agent.Api\bin\Release\net10.0\Mezhs.Agent.Api.dll'
  $agent = Start-Process -FilePath 'dotnet' -ArgumentList @($dll,'--config',$cfg) -WorkingDirectory $root -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
  WaitHealth 'http://127.0.0.1:5199/health'
  $command = "<ATTACH>`n$txt`n$png`n</ATTACH>"
  $task = "Test attachment in own next turn.`n$command"
  $body = @{ policyId = 'low'; input = $task } | ConvertTo-Json
  $created = Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:5199/v1/executions' -ContentType 'application/json' -Body $body
  $finished = WaitExecution $created.executionId
  Check ($finished.status -eq 'Completed') "agent completes ATTACH command, status=$($finished.status) error=$($finished.error)"
  $messages = Invoke-RestMethod -Uri "http://127.0.0.1:5199/v1/agent-chats/$($finished.chatId)/messages"
  $requests = @($messages | Where-Object { $_.role -eq 'user' })
  Check ($requests.Count -eq 2) "exactly two ordinary user turns, actual=$($requests.Count)"
  $files = @($requests[1].fileIds)
  Check ($files.Count -eq 2) "next ordinary turn carries both attachments, actual=$($files.Count)"
  $metadata = @($files | ForEach-Object { Invoke-RestMethod -Uri ('http://127.0.0.1:5199/v1/files/' + $_) })
  Check (@($metadata | Where-Object { $_.name -eq 'verify.txt' -and $_.contentType -eq 'text/plain' }).Count -eq 1) 'TXT metadata retained'
  Check (@($metadata | Where-Object { $_.name -eq 'verify.png' -and $_.contentType -eq 'image/png' }).Count -eq 1) 'PNG metadata retained'
  foreach ($file in $metadata) {
    $saved = Join-Path $temp ('roundtrip-' + $file.name)
    Invoke-WebRequest -Uri ('http://127.0.0.1:5199/v1/files/' + $file.fileId + '/content') -OutFile $saved -UseBasicParsing
    $source = if ($file.name -eq 'verify.txt') { $txt } else { $png }
    Check ((Get-FileHash -LiteralPath $saved -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash) ('Content roundtrip: ' + $file.name)
  }
  $first = @($requests[0].fileIds)
  Check ($first.Count -eq 0) 'initial ordinary turn has no attachments'
  $db = Join-Path $temp 'agent.sqlite'
  Check (Test-Path $db) 'execution staging ledger persisted'
  Write-Output "E2E_EXECUTION_ID=$($finished.executionId) CHAT_ID=$($finished.chatId)"
}
finally {
  if ($agent -and -not $agent.HasExited) { Stop-Process -Id $agent.Id -Force -ErrorAction SilentlyContinue; $agent.WaitForExit() }
  if ($agent -and $agent.ExitCode -ne 0 -and (Test-Path $stderr)) { Write-Output (Get-Content $stderr -Tail 25) }
  Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
