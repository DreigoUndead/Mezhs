$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('mezhs-policy-decoder-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null

try {
    $agentProject = Join-Path $root 'src\Mezhs.Agent.Api\Mezhs.Agent.Api.csproj'
    $configPath = Join-Path $PSScriptRoot 'mezhs.agent.test.yaml'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$agentProject" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $temp 'Test.csproj') -Encoding UTF8

    @'
using Mezhs.Agent.Configuration;
using Mezhs.Agent.Models;
using Mezhs.Agent.Policy;

var options = AgentConfigLoader.Load(args[0]);
var normal = options.Policies["test"];
var done = options.Policies["test-done"];

Assert(normal.Settings.ConnectionId == "test",
    "Typed PolicyContext lost the mapped connectionId.");
Assert(!normal.Settings.Completion.RequireDone,
    "Typed PolicyContext lost requireDone=false.");
Assert(normal.Settings.Limits.MaxTurns == 3,
    "Typed PolicyContext lost maxTurns=3.");
Assert(normal.Snapshot.Contains("requireDone: false", StringComparison.Ordinal),
    "Effective snapshot lost requireDone=false.");
Assert(normal.Snapshot.Contains("maxTurns: 3", StringComparison.Ordinal),
    "Effective snapshot lost maxTurns=3.");
Assert(done.ModelInstructions.Contains("DONE on a line by itself", StringComparison.Ordinal),
    "Completion rule did not compile into model instructions.");

var execution = new ExecutionRecord
{
    ExecutionId = "exec_test",
    CorrelationId = "exec_test",
    Kind = AgentExecutionKind.Agent,
    ChatId = "chat_test",
    PolicyId = "test-done",
    ConnectionId = "test",
    Source = "manual",
    Status = AgentExecutionStatus.Running,
    Request = "test",
    PolicySnapshot = done.Snapshot
};
var evidence = new[] { execution };
var context = new PolicyEvaluationContext(execution, evidence);

var firstTurn = done.ValidateTurn(new PolicyTurnContext(context, 0));
Assert(firstTurn.Allowed, "First turn was incorrectly denied.");
var secondTurn = done.ValidateTurn(new PolicyTurnContext(context, 1));
Assert(!secondTurn.Allowed && secondTurn.Error?.Contains("limit of 1 turns", StringComparison.Ordinal) == true,
    "Turn limit did not compile into runtime validation.");

var incomplete = done.EvaluateCompletion(new PolicyCompletionContext(context, "still working"));
Assert(incomplete.State == PolicyCompletionState.Incomplete,
    "Missing DONE was not treated as incomplete.");
var accepted = done.EvaluateCompletion(new PolicyCompletionContext(context, "result\nDONE\n"));
Assert(accepted.State == PolicyCompletionState.Accepted,
    "DONE completion claim was not accepted.");

var action = done.ValidateAction(new PolicyActionContext(
    context,
    new PolicyAction("shell", "echo hello")));
Assert(!action.Allowed && action.Error?.Contains("does not explicitly allow", StringComparison.Ordinal) == true,
    "Unconfigured executable action was not denied by default.");

Console.WriteLine("PASS: typed policy YAML maps into PolicyContext settings, runtime validators, model rules, snapshots, and deny-by-default actions.");

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
'@ | Set-Content -LiteralPath (Join-Path $temp 'Program.cs') -Encoding UTF8

    dotnet run --project (Join-Path $temp 'Test.csproj') -c Release -- $configPath
    if ($LASTEXITCODE -ne 0) { throw "Policy decoder test failed with exit code $LASTEXITCODE." }
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
