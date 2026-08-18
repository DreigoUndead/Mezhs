# Verifies Grok account login, interactive-browser disposal, hidden resume, and remote continuation.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('mezhs-grok-account-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null

try {
    $grokProject = Join-Path $root 'integrations\Mezhs.Integrations.Grok\Mezhs.Integrations.Grok.csproj'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$grokProject" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $temp 'Test.csproj') -Encoding UTF8

    @'
using System.Text.Json;
using Mezhs.Browser;
using Mezhs.Integrations;
using Mezhs.Integrations.Browser;
using Mezhs.Integrations.Grok;

var root = Path.Combine(Path.GetTempPath(), "mezhs-grok-account-flow", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await TestAutomaticLoginAsync(Path.Combine(root, "automatic"));
    await TestExplicitLoginAsync(Path.Combine(root, "explicit"));
    await TestContinuationAsync(Path.Combine(root, "continuation"));
    Console.WriteLine("PASS: Grok account login, requested mode handling, and remote continuation are correct.");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

static IntegrationConnection Connection() => new(
    "grok-account",
    "Grok Account",
    "grok-web-account",
    new Dictionary<string, string?>());

static IntegrationSendContext Context(string? remoteChatUrl = null) => new(
    new IntegrationChatContext("chat", "grok-account", remoteChatUrl),
    new IntegrationMessageContext("message", "user", "hello", false, DateTimeOffset.UtcNow, "expert"),
    Array.Empty<IntegrationMessageContext>(),
    Array.Empty<IntegrationInputFile>());

static async Task TestAutomaticLoginAsync(string root)
{
    var host = new FakeHost(root, authorized: false);
    await using var integration = new GrokAccountIntegration(Connection(), host);
    var result = await integration.SendMessageAsync(Context());

    Assert(result.Text == "ok", "message did not continue after login");
    Assert(result.Model is null, "Grok requested mode was incorrectly reported as the provider-served model");
    Assert(host.LastArguments?.GetProperty("model").GetString() == "expert",
        "selected Grok mode was not passed to the provider");
    Assert(host.Initializations.Count == 3,
        $"expected hidden check + visible login + hidden resume, got {host.Initializations.Count} initializations");
    Assert(!host.Initializations[0].ShowBrowser, "authorization check unexpectedly started visible");
    Assert(host.Initializations[0].RequireAuthorization, "hidden account start did not require authorization");
    Assert(host.Initializations[1].ShowBrowser, "authorization requirement did not transition to visible login");
    Assert(host.Initializations[1].RequireAuthorization, "visible login did not require authorization");
    Assert(!host.Initializations[2].ShowBrowser, "post-login Grok transport did not resume hidden");
    Assert(host.Initializations[2].RequireAuthorization, "post-login hidden Grok transport did not verify authorization");
    Assert(host.ActiveTransports == 1, $"expected only hidden Grok transport to remain active, got {host.ActiveTransports}");
    Assert(host.Operations.SequenceEqual(["newChat"]), "new Grok chat operation was not used");
}

static async Task TestExplicitLoginAsync(string root)
{
    var host = new FakeHost(root, authorized: false);
    await using var integration = new GrokAccountIntegration(Connection(), host);
    await integration.Login!.LoginAsync();

    Assert(host.Initializations.Count == 1,
        $"expected one explicit login initialization, got {host.Initializations.Count}");
    Assert(host.Initializations[0].ShowBrowser, "explicit login did not start visible");
    Assert(host.Initializations[0].RequireAuthorization, "explicit login did not require authorization");
    Assert(host.ActiveTransports == 0, "explicit Grok login browser stayed active after authorization completed");
}

static async Task TestContinuationAsync(string root)
{
    var host = new FakeHost(root, authorized: true);
    await using var integration = new GrokAccountIntegration(Connection(), host);
    var result = await integration.SendMessageAsync(Context("https://grok.com/c/existing"));

    Assert(result.RemoteChatUrl == "https://grok.com/c/test", "Grok chat URL was not returned");
    Assert(result.Model is null, "Grok continuation invented a served model from the requested mode");
    Assert(host.Operations.SequenceEqual(["send"]), "continuation did not use Grok send operation");
    Assert(host.LastArguments?.GetProperty("chatUrl").GetString() == "https://grok.com/c/existing",
        "stored Grok chat URL was not passed to continuation");
    Assert(host.LastArguments?.GetProperty("model").GetString() == "expert",
        "selected Grok mode was not passed to continuation");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class FakeHost(string root, bool authorized) : IBrowserIntegrationHost
{
    public int BrowserIdleMinutes => 0;
    public List<BrowserTransportOptions> Initializations { get; } = [];
    public List<string> Operations { get; } = [];
    public JsonElement? LastArguments { get; set; }
    public int ActiveTransports { get; set; }
    public bool Authorized { get; set; } = authorized;

    public string GetConnectionRoot(string connectionId)
    {
        var path = Path.Combine(root, connectionId);
        Directory.CreateDirectory(path);
        return path;
    }

    public IChatBrowserTransport CreateBrowserTransport()
    {
        ActiveTransports++;
        return new FakeTransport(this);
    }
}

sealed class FakeTransport(FakeHost host) : IChatBrowserTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private bool _disposed;

    public string Name => "Fake";

    public Task InitializeAsync(
        BrowserTransportOptions options,
        CancellationToken cancellationToken = default)
    {
        host.Initializations.Add(options);
        if (options.RequireAuthorization && !options.ShowBrowser && !host.Authorized)
            throw new BrowserAuthorizationRequiredException("Authorization required.");
        if (options.RequireAuthorization && options.ShowBrowser)
            host.Authorized = true;
        return Task.CompletedTask;
    }

    public Task<TResult> InvokeAsync<TResult>(
        string operation,
        object? arguments = null,
        CancellationToken cancellationToken = default)
    {
        host.Operations.Add(operation);
        host.LastArguments = JsonSerializer.SerializeToElement(arguments, JsonOptions);
        var json = JsonSerializer.Serialize(new
        {
            text = "ok",
            chatUrl = "https://grok.com/c/test"
        }, JsonOptions);
        return Task.FromResult(JsonSerializer.Deserialize<TResult>(json, JsonOptions)!);
    }

    public Task ShowAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            host.ActiveTransports--;
        }
        return ValueTask.CompletedTask;
    }
}
'@ | Set-Content -LiteralPath (Join-Path $temp 'Program.cs') -Encoding UTF8

    dotnet run --project (Join-Path $temp 'Test.csproj') -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "Grok account flow test failed with exit code $LASTEXITCODE."
    }
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
