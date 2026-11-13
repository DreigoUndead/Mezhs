$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('mezhs-browser-limits-config-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null

try {
    $apiProject = Join-Path $root 'src\Mezhs.Api\Mezhs.Api.csproj'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$apiProject" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $temp 'Test.csproj') -Encoding UTF8

    @'
using Mezhs.Configuration;

var configPath = Path.Combine(args[0], "mezhs.yaml");
const string BaseConfig = """
    version: 1
    server:
      listen: http://127.0.0.1:5050
    transport:
      type: electron
    connections:
      - id: account
        integration: chatgpt-web-account
    """;

MezhsOptions Read(string limits)
{
    File.WriteAllText(configPath, BaseConfig + limits);
    return MezhsConfigLoader.Load(configPath);
}

static void Assert(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

void Reject(string limits, string reason)
{
    try { Read(limits); }
    catch (Exception) { return; }
    throw new InvalidOperationException(reason);
}

var defaults = Read("");
Assert(defaults.Connections[0].Limits.MaxParallel == 1 &&
       defaults.Connections[0].Limits.MinInterval == 0,
    "Omitted browser limits must default to maxParallel=1, spacing=0.");

var unlimited = Read("""
    
        limits:
          maxParallel: 0
          minInterval: 0
    """);
Assert(unlimited.Connections[0].Limits.MaxParallel == 0 &&
       unlimited.Connections[0].Limits.MinInterval == 0,
    "Explicit zero must disable both browser limits.");

var bounded = Read("""
    
        limits:
          maxParallel: 3
          minInterval: 250
    """);
Assert(bounded.Connections[0].Limits.MaxParallel == 3 &&
       bounded.Connections[0].Limits.MinInterval == 250,
    "Configured browser limits were not parsed.");

Reject("""
    
        limits:
          maxParallel: -1
    """, "Negative maxParallel was accepted.");
Reject("""
    
        limits:
          minInterval: -1
    """, "Negative spacing was accepted.");
Reject("""
    
        limits:
          maxParallel: unlimited
    """, "Nonnumeric limits were accepted.");
Reject("""
    
        limits:
          minIntervalBetweenStartsMs: 100
    """, "Legacy minIntervalBetweenStartsMs must not be silently accepted.");

var sample = MezhsConfigLoader.Load(Path.Combine(args[1], "mezhs.yaml"));
Assert(sample.Connections.Count == 4, "Sample configuration should define four browser providers.");
var account = sample.Connections.Single(connection => connection.Id == "chatgpt-sub");
Assert(account.Limits.MaxParallel == 2 && account.Limits.MinInterval == 5000,
    "ChatGPT account must allow two sends spaced by five seconds.");
foreach (var id in new[] { "chatgpt-guest", "gemini-guest", "grok-account" })
{
    var connection = sample.Connections.Single(item => item.Id == id);
    Assert(connection.Limits.MaxParallel == 1 && connection.Limits.MinInterval == 0,
        $"Connection '{id}' must explicitly preserve default limits.");
}

Console.WriteLine("PASS: Browser YAML limits defaults, explicit unlimited, bounded values, invalid values and configured provider limits.");
'@ | Set-Content -LiteralPath (Join-Path $temp 'Program.cs') -Encoding UTF8

    dotnet run --project (Join-Path $temp 'Test.csproj') -c Release -- $temp $root
    if ($LASTEXITCODE -ne 0) { throw 'Browser limits config test failed.' }
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
