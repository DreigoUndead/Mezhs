$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([IO.Path]::GetTempPath()) ('mezhs-agent-attach-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $agentProject = [IO.Path]::GetFullPath((Join-Path $root 'src\Mezhs.Agent.Api\Mezhs.Agent.Api.csproj'))
    $projectPath = Join-Path $temp 'AttachmentTest.csproj'
    $programPath = Join-Path $temp 'Program.cs'
    $databasePath = Join-Path $temp 'agent.sqlite'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><ProjectReference Include="$agentProject" /></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $projectPath -Encoding UTF8
    @'
using Mezhs.Agent.Configuration;
using Mezhs.Agent.Models;
using Mezhs.Agent.Persistence;
using Mezhs.Agent.Commands;

static void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + label);
    Console.WriteLine("PASS: " + label);
}

var options = new AgentOptions { AgentStorage = args[0] };
var store = new AgentStore(options);
store.Initialize();
ExecutionRecord Create(string label) => store.TryCreateRootExecution(
    "test-policy", "test-connection", null, "manual", null, label,
    new Dictionary<string, string>(), "snapshot")
    ?? throw new InvalidOperationException("Root creation failed");

var one = Create("attachment owner");
var claimed = store.TryClaimNextQueuedExecution();
Check(claimed?.ExecutionId == one.ExecutionId, "created execution claimed");
store.StageAttachments(one.ExecutionId, "assistant-message-A", 0, ["file-txt"]);
store.StageAttachments(one.ExecutionId, "assistant-message-A", 1, ["file-png"]);
var pending = store.GetStagedAttachments(one.ExecutionId);
Check(pending.Count == 2 && pending.SelectMany(x => x.FileIds).SequenceEqual(["file-txt", "file-png"]),
    "two ATTACH commands staged and ordered");
try { store.StageAttachments(one.ExecutionId, "assistant-message-A", 0, ["duplicate"]); throw new Exception("duplicate accepted"); }
catch (InvalidOperationException ex) when (ex.Message.Contains("already staged")) { Console.WriteLine("PASS: duplicate command rejected"); }
try { store.ConsumeStagedAttachments(one.ExecutionId, "user-message-1", ["file-png", "file-txt"]); throw new Exception("mismatch accepted"); }
catch (InvalidOperationException ex) when (ex.Message.Contains("differ")) { Console.WriteLine("PASS: mismatched submission rejected"); }
Check(store.GetStagedAttachments(one.ExecutionId).All(x => x.ConsumedByMessageId is null), "failed consumption did not modify ledger");
store.ConsumeStagedAttachments(one.ExecutionId, "user-message-1", ["file-txt", "file-png"]);
store.ConsumeStagedAttachments(one.ExecutionId, "user-message-1", ["file-txt", "file-png"]);
Check(store.GetStagedAttachments(one.ExecutionId).All(x => x.ConsumedByMessageId == "user-message-1"), "exactly-once consumption idempotent");
Check(store.Complete(one.ExecutionId, "done"), "completed execution");
try { store.StageAttachments(one.ExecutionId, "assistant-message-B", 0, ["file-evil"]); throw new Exception("terminal accepted"); }
catch (InvalidOperationException ex) when (ex.Message.Contains("no longer running")) { Console.WriteLine("PASS: terminal execution cannot stage"); }

var two = Create("restart pending");
Check(store.TryClaimNextQueuedExecution()?.ExecutionId == two.ExecutionId, "second execution claimed");
store.StageAttachments(two.ExecutionId, "assistant-message-B", 0, ["file-recovery"]);
store = new AgentStore(options);
store.Initialize();
Check(store.GetStagedAttachments(two.ExecutionId).Single().FileIds.Single() == "file-recovery", "pending ledger survives process restart");
Check(store.GetStagedAttachments(one.ExecutionId).All(x => x.ConsumedByMessageId == "user-message-1"), "consumption survives process restart");
Check(store.GetStagedAttachments(one.ExecutionId).All(x => !x.FileIds.Contains("file-recovery")), "attachments isolated per execution");
var recovery = store.RecoverAfterRestart();
Check(store.TryClaimNextQueuedExecution()?.ExecutionId == two.ExecutionId, "interrupted execution reclaimed after restart");
Check(store.GetStagedAttachments(two.ExecutionId).Single().ConsumedByMessageId is null, "reclaimed execution retains pending attachment");
store.ConsumeStagedAttachments(two.ExecutionId, "recovered-user-message", ["file-recovery"]);
Check(store.GetStagedAttachments(two.ExecutionId).Single().ConsumedByMessageId == "recovered-user-message", "message-history reconciliation can consume pending record");
Console.WriteLine("PASS: attachment staging and recovery tests complete");
'@ | Set-Content -LiteralPath $programPath -Encoding UTF8
    dotnet run --project $projectPath -c Release -- $databasePath
    if ($LASTEXITCODE -ne 0) { throw "Attachment staging test failed with $LASTEXITCODE" }
}
finally { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
