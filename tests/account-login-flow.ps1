$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('mezhs-account-login-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null

try {
    $chatGptProject = Join-Path $root 'integrations\Mezhs.Integrations.ChatGpt\Mezhs.Integrations.ChatGpt.csproj'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$chatGptProject" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $temp 'Test.csproj') -Encoding UTF8

    @'
using System.Text.Json;
using Mezhs.Browser;
using Mezhs.Integrations;
using Mezhs.Integrations.Browser;
using Mezhs.Integrations.ChatGpt;

var root = Path.Combine(Path.GetTempPath(), "mezhs-account-login-flow", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await TestAutomaticLoginAsync(Path.Combine(root, "automatic"));
    await TestExplicitLoginAsync(Path.Combine(root, "explicit"));
    Console.WriteLine("PASS: ChatGPT account requests interactive login when hidden authorization is required, and explicit login starts visible.");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

static IntegrationConnection Connection() => new(
    "account",
    "Account",
    "chatgpt-web-account",
    new Dictionary<string, string?>());

static async Task TestAutomaticLoginAsync(string root)
{
    var host = new FakeHost(root, rejectHiddenAuthorization: true);
    await using var integration = new ChatGptAccountIntegration(Connection(), host);
    var now = DateTimeOffset.UtcNow;
    var result = await integration.SendMessageAsync(new IntegrationSendContext(
        new IntegrationChatContext("chat", "account"),
        new IntegrationMessageContext("message", "user", "hello", false, now),
        Array.Empty<IntegrationMessageContext>(),
        Array.Empty<IntegrationInputFile>()));

    Assert(result.Text == "ok", "message did not continue after login");
    Assert(host.Initializations.Count == 2, $"expected hidden check + visible login, got {host.Initializations.Count} initializations");
    Assert(!host.Initializations[0].ShowBrowser, "authorization check unexpectedly started visible");
    Assert(host.Initializations[0].RequireAuthorization, "hidden account start did not require authorization");
    Assert(host.Initializations[1].ShowBrowser, "authorization requirement did not transition to visible login");
    Assert(host.Initializations[1].RequireAuthorization, "visible login did not require authorization");
    Assert(host.PromptCount == 1, $"expected one prompt after login, got {host.PromptCount}");
}

static async Task TestExplicitLoginAsync(string root)
{
    var host = new FakeHost(root, rejectHiddenAuthorization: false);
    await using var integration = new ChatGptAccountIntegration(Connection(), host);
    await integration.Login!.LoginAsync();

    Assert(host.Initializations.Count == 1, $"expected one explicit login initialization, got {host.Initializations.Count}");
    Assert(host.Initializations[0].ShowBrowser, "explicit login did not start visible");
    Assert(host.Initializations[0].RequireAuthorization, "explicit login did not require authorization");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class FakeHost(string root, bool rejectHiddenAuthorization) : IBrowserIntegrationHost
{
    public int BrowserIdleMinutes => 0;
    public List<BrowserTransportOptions> Initializations { get; } = [];
    public int PromptCount { get; set; }

    public string GetConnectionRoot(string connectionId)
    {
        var path = Path.Combine(root, connectionId);
        Directory.CreateDirectory(path);
        return path;
    }

    public IChatBrowserTransport CreateBrowserTransport() =>
        new FakeTransport(this, rejectHiddenAuthorization);
}

sealed class FakeTransport(FakeHost host, bool rejectHiddenAuthorization) : IChatBrowserTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Name => "Fake";

    public Task InitializeAsync(BrowserTransportOptions options, CancellationToken cancellationToken = default)
    {
        host.Initializations.Add(options);
        if (rejectHiddenAuthorization && options.RequireAuthorization && !options.ShowBrowser)
            throw new BrowserAuthorizationRequiredException("Authorization required.");
        return Task.CompletedTask;
    }

    public Task<TResult> InvokeAsync<TResult>(
        string operation,
        object? arguments = null,
        CancellationToken cancellationToken = default)
    {
        host.PromptCount++;
        var json = JsonSerializer.Serialize(new
        {
            text = "ok",
            conversationId = "test",
            parentMessageId = "assistant",
            chatUrl = "https://chatgpt.com/c/test",
            projectId = (string?)null,
            artifacts = Array.Empty<BrowserArtifact>()
        }, JsonOptions);
        return Task.FromResult(JsonSerializer.Deserialize<TResult>(json, JsonOptions)!);
    }

    public Task ShowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
'@ | Set-Content -LiteralPath (Join-Path $temp 'Program.cs') -Encoding UTF8

    dotnet run --project (Join-Path $temp 'Test.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw "Account login flow test failed with exit code $LASTEXITCODE." }
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
