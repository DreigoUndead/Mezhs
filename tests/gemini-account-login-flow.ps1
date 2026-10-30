# Exercises Gemini account lifetime and authentication failure through the real integration.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([IO.Path]::GetTempPath()) ('mezhs-gemini-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $project = Join-Path $root 'integrations/Mezhs.Integrations.Gemini/Mezhs.Integrations.Gemini.csproj'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><ProjectReference Include="$project" /></ItemGroup>
</Project>
"@ | Set-Content (Join-Path $temp 'Test.csproj')
    @'
using System.Text.Json;
using Mezhs.Browser;
using Mezhs.Integrations;
using Mezhs.Integrations.Browser;
using Mezhs.Integrations.Gemini;

var host = new FakeHost();
var connection = new IntegrationConnection("gemini-account", "Gemini Account", "gemini-web-account", new Dictionary<string,string?>());
await using (var integration = new GeminiAccountIntegration(connection, host))
{
    Check(integration.Login is not null && integration.Diagnostics is not null, "account capabilities missing");
    Check(integration.Models is null, "unimplemented models were advertised");
    await integration.Login!.LoginAsync();
    Check(host.Initializations.Count == 1 && host.Initializations[0].ShowBrowser, "explicit login must be visible");
    Check(host.Active == 0, "explicit login transport must close after authorization");
    var now = DateTimeOffset.UtcNow;
    var context = new IntegrationSendContext(
        new IntegrationChatContext("chat", connection.Id, null),
        new IntegrationMessageContext("message", "user", "new question", false, now),
        new[] { new IntegrationMessageContext("old", "assistant", "previous answer", true, now.AddMinutes(-1)) },
        Array.Empty<IntegrationInputFile>());
    var result = await integration.SendMessageAsync(context);
    Check(result.Text == "reply" && result.RemoteChatUrl == "https://gemini.google.com/app/test", "provider result lost");
    Check(!host.Initializations.Last().ShowBrowser && host.Initializations.Last().RequireAuthorization, "account send must use hidden authorized session");
    Check(host.LastArgs.GetProperty("requireAccount").GetBoolean(), "account send could fall back to guest");
    Check(host.LastArgs.GetProperty("newChat").GetBoolean(), "remote history could be duplicated");
    Check(host.LastArgs.GetProperty("prompt").GetString()!.Contains("previous answer"), "local history lost");
    Check(host.Initializations.Select(x => x.ProfileDirectory).Distinct().Count() == 1, "account profile changed");
    await integration.Login.OpenBrowserAsync();
    Check(host.Shown == 1 && host.Active == 1, "browser must reuse active session");
    host.Fail = true;
    try { await integration.SendMessageAsync(context); throw new Exception("provider failure was ignored"); }
    catch (InvalidOperationException ex) when (ex.Message == "signed out") { }
}
Check(host.Active == 0, "account dispose leaked transport");
Console.WriteLine("PASS: Gemini explicit login, hidden account reuse, history, errors, profile stability, and disposal");
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

sealed class FakeHost : IBrowserIntegrationHost
{
    public int BrowserIdleMinutes => 0;
    public List<BrowserTransportOptions> Initializations = [];
    public int Active, Shown;
    public bool Authorized, Fail;
    public JsonElement LastArgs;
    public string GetConnectionRoot(string id) => Path.Combine(Path.GetTempPath(), "gemini-test", id);
    public IChatBrowserTransport CreateBrowserTransport() { Active++; return new FakeTransport(this); }
}
sealed class FakeTransport(FakeHost host) : IChatBrowserTransport
{
    public string Name => "Fake";
    public Task InitializeAsync(BrowserTransportOptions options, CancellationToken cancellationToken = default)
    {
        host.Initializations.Add(options);
        if (options.RequireAuthorization && !options.ShowBrowser && !host.Authorized)
            throw new BrowserAuthorizationRequiredException("sign in");
        if (options.RequireAuthorization && options.ShowBrowser) host.Authorized = true;
        return Task.CompletedTask;
    }
    public Task<T> InvokeAsync<T>(string operation, object? arguments = null, CancellationToken cancellationToken = default)
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        host.LastArgs = JsonSerializer.SerializeToElement(arguments, jsonOptions);
        var json = JsonSerializer.Serialize(new { ok = !host.Fail, text = "reply", chatUrl = "https://gemini.google.com/app/test", error = "signed out" });
        return Task.FromResult(JsonSerializer.Deserialize<T>(json, jsonOptions)!);
    }
    public Task ShowAsync(CancellationToken cancellationToken = default) { host.Shown++; return Task.CompletedTask; }
    public ValueTask DisposeAsync() { host.Active--; return ValueTask.CompletedTask; }
}
'@ | Set-Content (Join-Path $temp 'Program.cs')
    dotnet run --project (Join-Path $temp 'Test.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Gemini account flow failed.' }
}
finally {
    $resolved = [IO.Path]::GetFullPath($temp)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf) -like 'mezhs-gemini-test-*') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
