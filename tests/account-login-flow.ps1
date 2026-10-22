param([switch]$OwnershipOnly)
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
    <ProjectReference Include="$root\integrations\Mezhs.Integrations.Gemini\Mezhs.Integrations.Gemini.csproj" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $temp 'Test.csproj') -Encoding UTF8

    @'
using System.Text.Json;
using Mezhs.Browser;
using Mezhs.Integrations;
using Mezhs.Integrations.Browser;
using Mezhs.Integrations.ChatGpt;
using Mezhs.Integrations.Gemini;

var root = Path.Combine(Path.GetTempPath(), "mezhs-account-login-flow", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await TestSessionOwnershipAsync(Path.Combine(root, "ownership"));
    await TestAnonymousDiagnosticsAsync(Path.Combine(root, "guests"));
    Console.WriteLine("PASS: T2 browser session ownership and diagnostics regressions");
    if (args.Contains("--ownership-only")) return;
    await TestAutomaticLoginAsync(Path.Combine(root, "automatic"));
    await TestExplicitLoginAsync(Path.Combine(root, "explicit"));
    await TestModelDeduplicationAsync(Path.Combine(root, "models"));
    await TestStaleConversationRecoveryAsync(Path.Combine(root, "stale"));
    Console.WriteLine("PASS: ChatGPT account login, model discovery, served-model propagation, and stale conversation recovery are correct.");
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
    var host = new FakeHost(root, authorized: false);
    await using var integration = new ChatGptAccountIntegration(Connection(), host);
    var now = DateTimeOffset.UtcNow;
    var result = await integration.SendMessageAsync(new IntegrationSendContext(
        new IntegrationChatContext("chat", "account"),
        new IntegrationMessageContext("message", "user", "hello", false, now, "requested-test"),
        Array.Empty<IntegrationMessageContext>(),
        Array.Empty<IntegrationInputFile>()));

    Assert(result.Text == "ok", "message did not continue after login");
    Assert(result.Model == "served-test", "integration copied the requested model instead of the provider-served model");
    Assert(host.Initializations.Count == 3,
        $"expected hidden check + visible login + hidden resume, got {host.Initializations.Count} initializations");
    Assert(!host.Initializations[0].ShowBrowser, "authorization check unexpectedly started visible");
    Assert(host.Initializations[0].RequireAuthorization, "hidden account start did not require authorization");
    Assert(host.Initializations[1].ShowBrowser, "authorization requirement did not transition to visible login");
    Assert(host.Initializations[1].RequireAuthorization, "visible login did not require authorization");
    Assert(!host.Initializations[2].ShowBrowser, "post-login account transport did not resume hidden");
    Assert(host.Initializations[2].RequireAuthorization, "post-login hidden transport did not verify authorization");
    Assert(host.ActiveTransports == 1, $"expected only hidden transport to remain active, got {host.ActiveTransports}");
    Assert(host.PromptCount == 1, $"expected one prompt after login, got {host.PromptCount}");
}

static async Task TestExplicitLoginAsync(string root)
{
    var host = new FakeHost(root, authorized: false);
    await using var integration = new ChatGptAccountIntegration(Connection(), host);
    await integration.Login!.LoginAsync();

    Assert(host.Initializations.Count == 1, $"expected one explicit login initialization, got {host.Initializations.Count}");
    Assert(host.Initializations[0].ShowBrowser, "explicit login did not start visible");
    Assert(host.Initializations[0].RequireAuthorization, "explicit login did not require authorization");
    Assert(host.ActiveTransports == 0, "explicit login browser stayed active after authorization completed");
}

static async Task TestModelDeduplicationAsync(string root)
{
    var host = new FakeHost(root, authorized: true)
    {
        ModelResults =
        [
            new IntegrationModel("gpt-sol-primary", "GPT-5.6 Sol"),
            new IntegrationModel("gpt-sol-alias", "GPT-5.6 Sol"),
            new IntegrationModel("gpt-thinking", "GPT-5.5 Thinking")
        ]
    };
    await using var integration = new ChatGptAccountIntegration(Connection(), host);
    var models = await integration.Models!.GetModelsAsync();

    Assert(models.Count == 2, $"expected duplicate display names to collapse, got {models.Count} models");
    Assert(models[0].Id == "gpt-sol-primary", "provider order was not retained for duplicate model names");
    Assert(models[1].Id == "gpt-thinking", "unique model was removed during deduplication");
}

static async Task TestStaleConversationRecoveryAsync(string root)
{
    var host = new FakeHost(root, authorized: true) { UnavailableContinuationOnce = true };
    await using var integration = new ChatGptAccountIntegration(Connection(), host);
    var now = DateTimeOffset.UtcNow;
    var result = await integration.SendMessageAsync(new IntegrationSendContext(
        new IntegrationChatContext(
            "chat",
            "account",
            RemoteConversationId: "stale-conversation",
            RemoteParentMessageId: "old-parent"),
        new IntegrationMessageContext("current", "user", "new question", false, now),
        new[]
        {
            new IntegrationMessageContext("old-user", "user", "old question", true, now.AddMinutes(-2)),
            new IntegrationMessageContext("old-assistant", "assistant", "old answer", true, now.AddMinutes(-1))
        },
        Array.Empty<IntegrationInputFile>()));

    Assert(result.Text == "ok", "stale conversation retry did not complete");
    Assert(host.PromptCount == 2, $"expected unavailable continuation + one new-chat retry, got {host.PromptCount} prompt calls");
    Assert(host.Invocations.Count(item => item.Operation == "send") == 1, "stale conversation continuation was retried more than once");
    var fallback = host.Invocations.Single(item => item.Operation == "newChat").Arguments;
    Assert(fallback.Contains("old question", StringComparison.Ordinal), "fallback prompt lost prior user history");
    Assert(fallback.Contains("old answer", StringComparison.Ordinal), "fallback prompt lost prior assistant history");
    Assert(fallback.Contains("new question", StringComparison.Ordinal), "fallback prompt lost current message");
}

static IntegrationSendContext SimpleContext() => new(
    new IntegrationChatContext("chat", "account"),
    new IntegrationMessageContext("message", "user", "hello", false, DateTimeOffset.UtcNow),
    Array.Empty<IntegrationMessageContext>(),
    Array.Empty<IntegrationInputFile>());

static async Task TestSessionOwnershipAsync(string root)
{
    var host = new FakeHost(root, authorized: true);
    var integration = new ChatGptAccountIntegration(Connection(), host);
    try
    {
        var diagnostics = integration.Diagnostics;
        Assert(diagnostics is not null, "ChatGPT Account diagnostics unavailable");
        Assert(ReferenceEquals(diagnostics, integration.Diagnostics), "diagnostics instance was not reused");
        Assert(host.ActiveTransports == 0, "reading diagnostics unexpectedly opened a browser");
        var method = typeof(BrowserIntegrationBase).GetMethod("CreateAccountSession",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var first = (BrowserAccountSession)method.Invoke(integration, null)!;
        var second = (BrowserAccountSession)method.Invoke(integration, null)!;
        Assert(ReferenceEquals(first, second), "CreateAccountSession returned different sessions");

        var snapshot = await diagnostics!.InvokeAsync("snapshot", JsonSerializer.SerializeToElement(new { }));
        Assert(snapshot.GetProperty("snapshot").GetBoolean(), "snapshot diagnostics did not execute");
        Assert(host.ActiveTransports == 1 && host.Initializations.Count == 1,
            "diagnostics did not initialize exactly one authorized browser transport");

        await integration.Login!.OpenBrowserAsync();
        Assert(host.ShowCount == 1 && host.Initializations.Count == 1,
            "opening existing browser unexpectedly created another transport");
        var send = await integration.SendMessageAsync(SimpleContext());
        Assert(send.Text == "ok", "normal sending failed after diagnostics and browser show");
        await integration.Models!.GetModelsAsync();
        Assert(host.Invocations.Select(x => x.Operation).SequenceEqual(
            ["$diagnostics/snapshot", "newChat", "getModels"]),
            "diagnostics, sending, and model discovery did not use the expected transport");
        Assert(host.ActiveTransports == 1 && host.Initializations.Count == 1,
            "login, sending, or model discovery created a second transport");
    }
    finally
    {
        await integration.DisposeAsync();
    }
    Assert(host.ActiveTransports == 0, "disposing ChatGPT account leaked the browser transport");
    await integration.DisposeAsync();
}

static async Task TestAnonymousDiagnosticsAsync(string root)
{
    var host = new FakeHost(root, authorized: true);
    var settings = new Dictionary<string, string?>();
    await using (var guest = new ChatGptWebIntegration(
        new IntegrationConnection("guest-chatgpt", "ChatGPT Guest", "chatgpt-web", settings), host))
    {
        Assert(guest.Diagnostics is null && host.ActiveTransports == 0,
            "ChatGPT guest diagnostics opened a persistent account session");
        var result = await guest.SendMessageAsync(SimpleContext());
        Assert(result.Text == "guest-ok", "ChatGPT anonymous sending changed");
        Assert(guest.Diagnostics is null && host.ActiveTransports == 0,
            "ChatGPT guest left a persistent transport after sending");
    }
    await using (var guest = new GeminiWebIntegration(
        new IntegrationConnection("guest-gemini", "Gemini Guest", "gemini-web", settings), host))
    {
        Assert(guest.Diagnostics is null && host.ActiveTransports == 0,
            "Gemini guest diagnostics opened a persistent account session");
        var result = await guest.SendMessageAsync(SimpleContext());
        Assert(result.Text == "guest-ok", "Gemini anonymous sending changed");
        Assert(guest.Diagnostics is null && host.ActiveTransports == 0,
            "Gemini guest left a persistent transport after sending");
    }
    Assert(host.Initializations.Count == 2 && host.Initializations.All(x => !x.RequireAuthorization),
        "guest transports unexpectedly required account authorization");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class FakeHost(string root, bool authorized) : IBrowserIntegrationHost
{
    public int BrowserIdleMinutes => 0;
    public List<BrowserTransportOptions> Initializations { get; } = [];
    public List<(string Operation, string Arguments)> Invocations { get; } = [];
    public IReadOnlyList<IntegrationModel> ModelResults { get; set; } = [];
    public int PromptCount { get; set; }
    public int ActiveTransports { get; set; }
    public int ShowCount { get; set; }
    public bool Authorized { get; set; } = authorized;
    public bool UnavailableContinuationOnce { get; set; }

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

    public Task InitializeAsync(BrowserTransportOptions options, CancellationToken cancellationToken = default)
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
        host.Invocations.Add((operation, JsonSerializer.Serialize(arguments, JsonOptions)));

        if (operation == "$diagnostics/snapshot")
            return Result<TResult>(new { snapshot = true });
        if (operation == "sendPrompt")
            return Result<TResult>(new { ok = true, text = "guest-ok" });

        if (operation == "getModels")
            return Result<TResult>(host.ModelResults);
        if (operation == "getProjects")
            return Result<TResult>(Array.Empty<object>());

        if (operation is "newChat" or "send")
        {
            host.PromptCount++;
            if (operation == "send" && host.UnavailableContinuationOnce)
            {
                host.UnavailableContinuationOnce = false;
                return Result<TResult>(new { conversationUnavailable = true });
            }

            return Result<TResult>(new
            {
                text = "ok",
                conversationId = "test",
                parentMessageId = "assistant",
                chatUrl = "https://chatgpt.com/c/test",
                projectId = (string?)null,
                artifacts = Array.Empty<BrowserArtifact>(),
                model = "served-test"
            });
        }

        throw new InvalidOperationException($"Unexpected fake browser operation '{operation}'.");
    }

    private static Task<TResult> Result<TResult>(object value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        return Task.FromResult(JsonSerializer.Deserialize<TResult>(json, JsonOptions)!);
    }

    public Task ShowAsync(CancellationToken cancellationToken = default)
    {
        host.ShowCount++;
        return Task.CompletedTask;
    }

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

    if ($OwnershipOnly) {
    dotnet run --project (Join-Path $temp 'Test.csproj') -c Release -- --ownership-only
    } else {
    dotnet run --project (Join-Path $temp 'Test.csproj') -c Release
    }
    if ($LASTEXITCODE -ne 0) { throw "Account integration flow test failed with exit code $LASTEXITCODE." }
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
