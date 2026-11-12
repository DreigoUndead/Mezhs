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
       defaults.Connections[0].Limits.MinIntervalBetweenStartsMs == 0,
    "Omitted browser limits must default to maxParallel=1, spacing=0.");

var unlimited = Read("""
    
        limits:
          maxParallel: 0
          minIntervalBetweenStartsMs: 0
    """);
Assert(unlimited.Connections[0].Limits.MaxParallel == 0 &&
       unlimited.Connections[0].Limits.MinIntervalBetweenStartsMs == 0,
    "Explicit zero must disable both browser limits.");

var bounded = Read("""
    
        limits:
          maxParallel: 3
          minIntervalBetweenStartsMs: 250
    """);
Assert(bounded.Connections[0].Limits.MaxParallel == 3 &&
       bounded.Connections[0].Limits.MinIntervalBetweenStartsMs == 250,
    "Configured browser limits were not parsed.");

Reject("""
    
        limits:
          maxParallel: -1
    """, "Negative maxParallel was accepted.");
Reject("""
    
        limits:
          minIntervalBetweenStartsMs: -1
    """, "Negative spacing was accepted.");
Reject("""
    
        limits:
          maxParallel: unlimited
    """, "Nonnumeric limits were accepted.");

Console.WriteLine("PASS: Browser YAML limits defaults, explicit unlimited, bounded values and invalid values.");
'@ | Set-Content -LiteralPath (Join-Path $temp 'Program.cs') -Encoding UTF8

    dotnet run --project (Join-Path $temp 'Test.csproj') -c Release -- $temp
    if ($LASTEXITCODE -ne 0) { throw 'Browser limits config test failed.' }
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
