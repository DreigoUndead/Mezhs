$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([IO.Path]::GetTempPath()) ("mezhs-agent-migration-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temp | Out-Null

try {
    $projectPath = Join-Path $temp "MigrationTest.csproj"
    $programPath = Join-Path $temp "Program.cs"
    $agentProject = [IO.Path]::GetFullPath((Join-Path $root "src\Mezhs.Agent.Api\Mezhs.Agent.Api.csproj"))
    $databasePath = Join-Path $temp "agent.sqlite"

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
"@ | Set-Content -LiteralPath $projectPath -Encoding UTF8

    @'
using Mezhs.Agent.Configuration;
using Mezhs.Agent.Models;
using Mezhs.Agent.Persistence;
using Microsoft.Data.Sqlite;

var databasePath = args[0];
var options = new AgentOptions { AgentStorage = databasePath };
var store = new AgentStore(options);
store.Initialize();

var execution = store.TryCreateRootExecution(
    "test-policy",
    "test-connection",
    chatId: null,
    source: "manual",
    sourceReference: null,
    request: "legacy execution",
    environment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
    policySnapshot: "snapshot",
    maxOutstandingExecutions: 1)
    ?? throw new InvalidOperationException("Could not seed legacy execution.");

using (var connection = new SqliteConnection($"Data Source={databasePath}"))
{
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = """
        UPDATE Executions
        SET Status = 'Interrupted',
            Error = 'legacy restart interruption',
            CompletedAt = '2026-09-01T00:00:00.0000000+00:00'
        WHERE ExecutionId = $executionId;
        """;
    command.Parameters.AddWithValue("$executionId", execution.ExecutionId);
    if (command.ExecuteNonQuery() != 1)
        throw new InvalidOperationException("Could not seed Interrupted legacy status.");
}

// Opening an old database with the current store must migrate persisted enum values
// before any execution rows are materialized through Enum.Parse.
store = new AgentStore(options);
store.Initialize();

var migrated = store.GetExecution(execution.ExecutionId)
    ?? throw new InvalidOperationException("Migrated execution was not found.");
if (migrated.Status != AgentExecutionStatus.Failed)
    throw new InvalidOperationException($"Expected legacy Interrupted to migrate to Failed, got {migrated.Status}.");
if (migrated.Error != "legacy restart interruption")
    throw new InvalidOperationException("Legacy interruption error was not preserved.");
if (migrated.CompletedAt is null)
    throw new InvalidOperationException("Legacy completion timestamp was not preserved.");

Console.WriteLine("PASS: legacy Agent Interrupted status migrates to Failed without losing terminal evidence.");
'@ | Set-Content -LiteralPath $programPath -Encoding UTF8

    dotnet run --project $projectPath --configuration Release -- $databasePath
    if ($LASTEXITCODE -ne 0) {
        throw "Legacy Agent status migration test failed with exit code $LASTEXITCODE."
    }
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
