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
    await TestContextLifetimeAsync(Path.Combine(root, "lifetimes"));
    await TestConcurrentLoginDoesNotBlockCancellationAsync(Path.Combine(root, "login-cancel"));
    await TestAnonymousDiagnosticsAsync(Path.Combine(root, "guests"));
    Console.WriteLine("PASS: T2 browser session ownership and diagnostics regressions");
    if (args.Contains("--ownership-only")) return;
    await TestAutomaticLoginAsync(Path.Combine(root, "automatic"));
    await TestExpiredOperationLoginAsync(Path.Combine(root, "expired-operation"));
    await TestExplicitLoginAsync(Path.Combine(root, "explicit"));
    await TestModelIdentityPreservationAsync(Path.Combine(root, "models"));
    await TestStaleConversationRecoveryAsync(Path.Combine(root, "stale"));
    Console.WriteLine("PASS: ChatGPT account login, model discovery, served-model propagation, and stale conversation recovery are correct.");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

static IntegrationConnection Connection(int? maxParallel = null) => new(
    "account",
    "Account",
    "chatgpt-web-account",
    new Dictionary<string, string?>(),
    maxParallel is null ? null : new IntegrationLimits { MaxParallel = maxParallel.Value });

static async Task TestExpiredOperationLoginAsync(string root)
{
    var host = new FakeHost(root, authorized: true) { AuthorizationFailureOnce = true };
    await using var integration = new ChatGptAccountIntegration(Connection(), host);
    try
    {
        await Send(integration);
        throw new InvalidOperationException("An expired authorization was replayed or incorrectly accepted.");
    }
    catch (BrowserAuthorizationRequiredException) { }
    Assert(host.PromptCount == 0, "The first failed operation should not be automatically replayed.");
    Assert(host.Initializations.Count == 1, "A mid-turn auth failure replaced the shared host.");

    // A later explicit user attempt may reauthorize and submit a new turn.
    var result = await Send(integration);
    Assert(result.Text == "ok", "Explicit retry after reauthorization failed.");
    Assert(host.Initializations.Count == 1 && host.PromptCount == 1,
        "Explicit retry should reuse the one host and submit exactly one turn.");
}

static async Task TestAutomaticLoginAsync(string root)
{
    var host = new FakeHost(root, authorized: false);
    await using var integration = new ChatGptAccountIntegration(Connection(), host);
    var result = await Send(integration, model: "requested-test");

    Assert(result.Text == "ok", "message did not continue after login");
    Assert(result.Model == "served-test", "integration copied the requested model instead of the provider-served model");
    Assert(host.Initializations.Count == 1, "Login recreated the browser process instead of authorizing the shared host.");
    Assert(!host.Initializations[0].ShowBrowser, "Hidden shared host unexpectedly started visible.");
    Assert(host.ActiveTransports == 1, "Authorized shared host should remain available for further chats.");
    Assert(host.PromptCount == 1, "Expected exactly one submission after login.");
}

static async Task TestExplicitLoginAsync(string root)
{
    var host = new FakeHost(root, authorized: false);
    await using var integration = new ChatGptAccountIntegration(Connection(), host);
    await integration.LoginAsync();
    Assert(host.Authorized, "Explicit login did not authorize the shared account session.");
    Assert(host.Initializations.Count == 1, "Explicit login created more than one host.");
    Assert(host.ActiveTransports == 1, "Explicit login discarded the warm shared host.");
}

static async Task TestModelIdentityPreservationAsync(string root)
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
    var models = await integration.GetModelsAsync();

    Assert(models.Count == 4, $"expected one Default plus three distinct native model IDs, got {models.Count} models");
    Assert(models[0].Id == "default" && models[0].Name == "Default", "direct model discovery must expose exactly one Default.");
    Assert(models[1].Id == "gpt-sol-primary", "provider order was not retained for duplicate model names");
    Assert(models[2].Id == "gpt-sol-alias", "distinct model ID with the same display name was removed");
    Assert(models[3].Id == "gpt-thinking", "provider model order was not retained");
}

static async Task TestStaleConversationRecoveryAsync(string root)
{
    var host = new FakeHost(root, authorized: true) { UnavailableContinuationOnce = true };
    await using var integration = new ChatGptAccountIntegration(Connection(), host);
    var now = DateTimeOffset.UtcNow;
    var history = new ChatMessage[]
    {
        new("old-user", "account", "user", "old question", true, now.AddMinutes(-2)),
        new("old-assistant", "account", "assistant", "old answer", true, now.AddMinutes(-1))
    };
    var chat = new ChatContext(
        "chat", "account", "stale-conversation",
        (_, _, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ChatMessage>>(history);
        },
        remoteParentMessageId: "old-parent", restoreConversation: true);
    var result = await integration.PromptAsync(
        chat, new PromptRequest(integration.CreateMessageId(), "new question", null, []));

    Assert(result.Text == "ok", "stale conversation retry did not complete");
    Assert(host.PromptCount == 2, $"expected unavailable continuation + one new-chat retry, got {host.PromptCount} prompt calls");
    Assert(host.Invocations.Count(item => item.Operation == "send") == 1, "stale conversation continuation was retried more than once");
    var fallback = host.Invocations.Single(item => item.Operation == "newChat").Arguments;
    Assert(fallback.Contains("old question", StringComparison.Ordinal), "fallback prompt lost prior user history");
    Assert(fallback.Contains("old answer", StringComparison.Ordinal), "fallback prompt lost prior assistant history");
    Assert(fallback.Contains("new question", StringComparison.Ordinal), "fallback prompt lost current message");
}

// Test fixture invokes the production typed PromptAsync contract, not a compatibility module.
static Task<PromptResult> Send(
    IntegrationBase integration,
    string chatId = "chat",
    CancellationToken cancellationToken = default,
    Action<IntegrationActivity>? reportActivity = null,
    string? model = null) =>
    integration.PromptAsync(
        new ChatContext(
            chatId, integration.Connection.Id, null,
            (_, _, _, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<ChatMessage>>([]);
            },
            reportActivity: reportActivity),
        new PromptRequest(integration.CreateMessageId(), "hello", model, []),
        cancellationToken);

static async Task TestSessionOwnershipAsync(string root)
{
    var host = new FakeHost(root, authorized: true);
    var integration = new ChatGptAccountIntegration(Connection(), host);
    try
    {
        Assert(host.ActiveTransports == 0, "constructing the integration unexpectedly opened a browser");
        var method = typeof(BrowserIntegrationBase).GetMethod("CreateAccountSession",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var first = (BrowserAccountSession)method.Invoke(integration, null)!;
        var second = (BrowserAccountSession)method.Invoke(integration, null)!;
        Assert(ReferenceEquals(first, second), "CreateAccountSession returned different sessions");

        var snapshot = await integration.DiagnoseAsync("snapshot", JsonSerializer.SerializeToElement(new { }));
        Assert(snapshot.GetProperty("snapshot").GetBoolean(), "snapshot diagnostics did not execute");
        Assert(host.ActiveTransports == 1 && host.Initializations.Count == 1,
            "diagnostics did not initialize exactly one authorized browser transport");

        await integration.OpenBrowserAsync();
        Assert(host.ShowCount == 1 && host.Initializations.Count == 1,
            "opening existing browser unexpectedly created another transport");
        var stages = new List<IntegrationActivity>();
        var send = await Send(integration, reportActivity: stages.Add);
        Assert(send.Text == "ok", "normal sending failed after diagnostics and browser show");
        Assert(stages.Select(stage => stage.Detail).SequenceEqual(
            ["Checking browser session.", "Browser session ready; preparing request."]),
            "authenticated request did not report the expected session preparation stages");
        await integration.GetModelsAsync();
        Assert(host.Invocations.Select(x => x.Operation).SequenceEqual(
            ["$diagnostics/snapshot", "newChat", "typedHttp"]),
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

static async Task TestContextLifetimeAsync(string root)
{
    var host = new FakeHost(root, authorized: true);
    await using var integration = new ChatGptAccountIntegration(Connection(), host);
    var owner = (IConversationContextOwner)integration;

    // Ordinary chats retire their own windows even when host idle shutdown is disabled.
    await Send(integration, "ordinary-a");
    await Send(integration, "ordinary-b");
    Assert(host.ReleasedContexts.SequenceEqual(["ordinary-a", "ordinary-b"]),
        "Ordinary account chats must release each context without stopping the shared host.");
    Assert(host.ActiveTransports == 1 && host.Initializations.Count == 1,
        "Retiring ordinary chat contexts unexpectedly restarted the shared host.");

    // Two overlapping Agent owners must keep one context until both release it.
    await owner.RetainConversationAsync("agent-chat");
    await owner.RetainConversationAsync("agent-chat");
    await Send(integration, "agent-chat");
    Assert(!host.ReleasedContexts.Contains("agent-chat"),
        "An Agent model turn released a context needed for shell follow-up.");
    await owner.ReleaseConversationAsync("agent-chat");
    Assert(!host.ReleasedContexts.Contains("agent-chat"),
        "The first of two overlapping Agent executions retired the other's context.");
    await owner.ReleaseConversationAsync("agent-chat");
    Assert(host.ReleasedContexts.Count(id => id == "agent-chat") == 1,
        "The final Agent owner should release its context exactly once.");

    // Cancellation must retire even a retained Agent context before the local send returns.
    await owner.RetainConversationAsync("cancelled-agent");
    host.HangNextPrompt = true;
    using var cancellation = new CancellationTokenSource();
    var send = Send(integration, "cancelled-agent", cancellation.Token);
    await host.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    cancellation.Cancel();
    try
    {
        await send.WaitAsync(TimeSpan.FromSeconds(5));
        throw new InvalidOperationException("Cancelled Agent model send unexpectedly succeeded.");
    }
    catch (OperationCanceledException) { }
    Assert(host.ReleasedContexts.Contains("cancelled-agent"),
        "Cancelled Agent context was not discarded before the send returned.");
    await owner.ReleaseConversationAsync("cancelled-agent");
}

static async Task TestConcurrentLoginDoesNotBlockCancellationAsync(string root)
{
    var host = new FakeHost(root, authorized: true);
    await using var integration = new ChatGptAccountIntegration(Connection(maxParallel: 3), host);

    // A has already submitted. B waits for a user to log in, while C must
    // wait for that same attempt rather than opening another login dialog.
    host.HangNextPrompt = true;
    using var cancelA = new CancellationTokenSource();
    var sendA = Send(integration, "active-a", cancelA.Token);
    await host.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

    host.Authorized = false;
    host.HangAuthorization = true;
    var sendB = Send(integration, "login-b");
    await host.AuthorizationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var sendC = Send(integration, "login-c");

    cancelA.Cancel();
    try
    {
        await sendA.WaitAsync(TimeSpan.FromSeconds(2));
        throw new InvalidOperationException("Active send did not cancel.");
    }
    catch (OperationCanceledException) { }
    Assert(host.ReleasedContexts.Contains("active-a"),
        "Active chat cancellation was blocked behind an interactive login.");
    Assert(!sendB.IsCompleted && !sendC.IsCompleted,
        "A waiting login attempt must not complete before the dialog is dismissed.");

    // Dismiss the single login dialog without a cookie. Both waiting sends
    // fail explicitly; the second may only recheck, not prompt the user again.
    host.FinishAuthorization.TrySetResult(false);
    await ExpectAuthRequiredAsync(sendB);
    await ExpectAuthRequiredAsync(sendC);
    Assert(host.InteractiveAuthorizationAttempts == 1,
        "Concurrent chats started multiple interactive login attempts.");
    Assert(host.ActiveTransports == 1 && host.Initializations.Count == 1,
        "Login or cancellation restarted the shared browser host.");
}

static async Task ExpectAuthRequiredAsync(Task send)
{
    try
    {
        await send.WaitAsync(TimeSpan.FromSeconds(5));
    }
    catch (BrowserAuthorizationRequiredException) { return; }
    throw new InvalidOperationException("Dismissed login did not fail with authorization required.");
}

static async Task TestAnonymousDiagnosticsAsync(string root)
{
    var host = new FakeHost(root, authorized: true);
    var settings = new Dictionary<string, string?>();
    await using (var guest = new ChatGptWebIntegration(
        new IntegrationConnection("guest-chatgpt", "ChatGPT Guest", "chatgpt-web", settings), host))
    {
        Assert((object)guest is not IConversationContextOwner && host.ActiveTransports == 0,
            "Guest integration must not acquire or retain account browser contexts.");
        Assert(host.ActiveTransports == 0,
            "ChatGPT guest diagnostics opened a persistent account session");
        var result = await Send(guest);
        Assert(result.Text == "guest-ok", "ChatGPT anonymous sending changed");
        Assert((object)guest is not IConversationContextOwner && host.ActiveTransports == 1,
            "ChatGPT guest should reuse its host between isolated ephemeral turns");
    }
    await using (var guest = new GeminiWebIntegration(
        new IntegrationConnection("guest-gemini", "Gemini Guest", "gemini-web", settings), host))
    {
        Assert((object)guest is not IConversationContextOwner && host.ActiveTransports == 0,
            "Gemini guest diagnostics opened a persistent account session");
        var result = await Send(guest);
        Assert(result.Text == "guest-ok", "Gemini anonymous sending changed");
        Assert((object)guest is not IConversationContextOwner && host.ActiveTransports == 1,
            "Gemini guest should reuse its host between isolated ephemeral turns");
    }
    Assert(host.Initializations.Count == 2 && host.Initializations.All(x => x.Guest && !x.RequireAuthorization),
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
    public List<string> ReleasedContexts { get; } = [];
    public bool HangNextPrompt { get; set; }
    public TaskCompletionSource<bool> PromptStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Authorized { get; set; } = authorized;
    public bool HangAuthorization { get; set; }
    public int InteractiveAuthorizationAttempts;
    public TaskCompletionSource<bool> AuthorizationStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> FinishAuthorization { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool AuthorizationFailureOnce { get; set; }
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

    public async Task EnsureAuthorizedAsync(bool interactive, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!host.Authorized)
        {
            if (!interactive)
                throw new BrowserAuthorizationRequiredException("Authorization required.");
            Interlocked.Increment(ref host.InteractiveAuthorizationAttempts);
            if (host.HangAuthorization)
            {
                host.AuthorizationStarted.TrySetResult(true);
                if (!await host.FinishAuthorization.Task.WaitAsync(cancellationToken))
                    throw new BrowserAuthorizationRequiredException("Login was dismissed.");
            }
            host.Authorized = true;
        }
    }

    public Task<TResult> InvokeAsync<TResult>(
        string operation,
        object? arguments = null,
        CancellationToken cancellationToken = default)
    {
        host.Invocations.Add((operation, JsonSerializer.Serialize(arguments, JsonOptions)));

        if (host.AuthorizationFailureOnce && operation is "getProjects" or "newChat" or "send")
        {
            host.AuthorizationFailureOnce = false;
            host.Authorized = false;
            throw new BrowserAuthorizationRequiredException("Authorization required.");
        }

        if (operation == "$diagnostics/snapshot")
            return Result<TResult>(new { snapshot = true });
        if (operation == "sendPrompt")
            return Result<TResult>(new { ok = true, text = "guest-ok" });

        if (operation == "typedHttp")
        {
            var catalog = new
            {
                models = host.ModelResults.Select(model =>
                    new { slug = model.Id, title = model.Name }).ToArray()
            };
            return Result<TResult>(new HttpTransportResponse(200,
                JsonSerializer.Serialize(catalog, JsonOptions), "application/json"));
        }
        if (operation == "getProjects")
            return Result<TResult>(Array.Empty<object>());

        if (operation is "newChat" or "send")
        {
            if (host.HangNextPrompt)
            {
                host.HangNextPrompt = false;
                host.PromptStarted.TrySetResult(true);
                return WaitForCancellation<TResult>(cancellationToken);
            }
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

    private static async Task<TResult> WaitForCancellation<TResult>(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Cancellation probe unexpectedly resumed.");
    }

    public Task ReleaseContextAsync(string chatId, CancellationToken cancellationToken = default)
    {
        host.ReleasedContexts.Add(chatId);
        return Task.CompletedTask;
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
