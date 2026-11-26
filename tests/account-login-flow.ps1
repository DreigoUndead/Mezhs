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
    await TestWarmContextsAsync(Path.Combine(root, "warm-contexts"));
    await TestConcurrentLoginDoesNotBlockCancellationAsync(Path.Combine(root, "login-cancel"));
    await TestAnonymousDiagnosticsAsync(Path.Combine(root, "guests"));
    Console.WriteLine("PASS: T2 browser session ownership and diagnostics regressions");
    if (args.Contains("--ownership-only")) return;
    await TestAutomaticLoginAsync(Path.Combine(root, "automatic"));
    await TestExpiredOperationLoginAsync(Path.Combine(root, "expired-operation"));
    await TestExplicitLoginAsync(Path.Combine(root, "explicit"));
    await TestModelDeduplicationAsync(Path.Combine(root, "models"));
    await TestProjectDiscoveryAsync(Path.Combine(root, "projects"));
    await TestStaleConversationRecoveryAsync(Path.Combine(root, "stale"));
    Console.WriteLine("PASS: ChatGPT account login, model discovery, served-model propagation, and stale conversation recovery are correct.");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

static IntegrationConnection Connection(int? maxParallel = null, string? workspace = null) => new(
    "account",
    "Account",
    "chatgpt-web-account",
    workspace is null ? new Dictionary<string, string?>()
        : new Dictionary<string, string?> { ["workspace"] = workspace },
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
    var models = await integration.GetModelsAsync();

    Assert(models.Count == 4, $"expected one Default plus deduplicated native models, got {models.Count} models");
    Assert(models[0].Id == "default" && models[0].Name == "Default", "direct model discovery must expose exactly one Default.");
    Assert(models[1].Id == "gpt-sol-primary", "provider order was not retained for duplicate model names");
    Assert(models[3].Id == "gpt-thinking", "unique model was removed during deduplication");
}

static async Task TestProjectDiscoveryAsync(string root)
{
    var host = new FakeHost(root, authorized: true);
    await using var integration = new ChatGptAccountIntegration(
        Connection(workspace: "Two"), host);
    var result = await Send(integration);
    Assert(result.Text == "ok", "Project-bound send did not complete.");
    var projectPages = host.Invocations
        .Where(x => x.Operation == "typedHttp")
        .Select(x => JsonDocument.Parse(x.Arguments).RootElement
            .GetProperty("path").GetString())
        .Where(x => x != null && x.StartsWith("/backend-api/gizmos/", StringComparison.Ordinal))
        .ToArray();
    Assert(projectPages.SequenceEqual([
        "/backend-api/gizmos/snorlax/sidebar?conversations_per_gizmo=0",
        "/backend-api/gizmos/snorlax/sidebar?conversations_per_gizmo=0&cursor=next"
    ]), "Project discovery did not use typed HTTP pagination.");
    Assert(host.TurnPayloads.Single().Contains("g-p-two", StringComparison.Ordinal),
        "Project selection did not preserve the project ID from the second page.");
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
    Assert(host.PromptCount == 1, $"inaccessible continuation must not POST before one restored new-chat attempt; got {host.PromptCount} calls");
    Assert(host.Invocations.Count(item => item.Operation == "typedHttp" &&
        JsonDocument.Parse(item.Arguments).RootElement.GetProperty("path").GetString() ==
        "/backend-api/conversation/stale-conversation") == 1,
        "inaccessible continuation was preflight-checked more than once");
    var fallback = host.TurnPayloads.Single();
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
        Assert(stages.Take(3).Select(stage => stage.Detail).SequenceEqual(
            ["Waiting for browser transport.", "Checking browser account authorization.", "Browser session ready; preparing request."]),
            "authenticated request did not report the expected session preparation stages");
        await integration.GetModelsAsync();
        Assert(host.Invocations[0].Operation == "$diagnostics/snapshot" &&
            host.Invocations.Skip(1).All(x => x.Operation == "typedHttp") &&
            host.PromptCount == 1,
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

static async Task TestWarmContextsAsync(string root)
{
    var host = new FakeHost(root, authorized: true) { BrowserIdleMinutes = 2 };
    var clock = new ManualLifetimeClock();
    var constructor = typeof(BrowserAccountSession).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
    Func<bool, bool, BrowserTransportOptions> options = (show, auth) => new(root, "unused", show, auth);
    await using var session = (BrowserAccountSession)constructor.Invoke([host, options, clock]);
    Task<int> SendChat(string id) => session.UseAsync(static (_, _) => Task.FromResult(1), chatId: id);
    bool Released(string id) => host.ReleasedContexts.Contains(id);
    static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static async Task Eventually(Func<bool> condition, string message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { while (!condition()) await Task.Delay(10, timeout.Token); }
        catch (OperationCanceledException) { throw new Exception(message); }
    }

    await SendChat("a");
    await SendChat("b");
    Assert(host.ReleasedContexts.IsEmpty, "Successful ordinary turns released their warm contexts.");
    clock.Advance(TimeSpan.FromMinutes(1));
    await SendChat("a");
    var started = Signal();
    var finish = Signal();
    var peer = session.UseAsync(async (_, token) => { started.SetResult(true); return await finish.Task.WaitAsync(token); }, chatId: "peer");
    await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    clock.Advance(TimeSpan.FromMinutes(1));
    await Eventually(() => Released("b"), "Idle peer B was not independently expired.");
    Assert(!Released("a") && !Released("peer"), "Stale timer released a renewed or active conversation.");
    clock.Advance(TimeSpan.FromMinutes(1));
    await Eventually(() => Released("a"), "Renewed A did not expire at its new deadline.");
    Assert(!Released("peer") && host.ActiveTransports == 1, "Idle retirement disrupted an active peer.");
    finish.SetResult(true);
    await peer;

    for (var i = 0; i < 8; i++) await SendChat($"cache-{i}");
    Assert(Released("peer"), "The ninth idle window did not evict the oldest idle window.");
    await SendChat("cache-0");
    await SendChat("cache-8");
    Assert(Released("cache-1") && !Released("cache-0"), "Cache eviction did not respect renewed activity.");
    await session.RetainConversationAsync("cache-0");
    await session.RetainConversationAsync("cache-0");
    clock.Advance(TimeSpan.FromMinutes(2));
    await Eventually(() => Enumerable.Range(2, 7).All(i => Released($"cache-{i}")), "Idle cache expiry was incomplete.");
    Assert(!Released("cache-0"), "Idle expiry retired an Agent-retained context.");
    await session.ReleaseConversationAsync("cache-0");
    Assert(!Released("cache-0"), "First owner release retired a multiply retained context.");
    await session.ReleaseConversationAsync("cache-0");
    Assert(host.ReleasedContexts.Count(id => id == "cache-0") == 1, "Final owner release did not retire the context exactly once.");

    await SendChat("failure");
    try { await session.UseAsync<int>(static (_, _) => throw new InvalidOperationException("expected failure"), chatId: "failure"); throw new Exception("Failure was swallowed."); }
    catch (InvalidOperationException ex) when (ex.Message == "expected failure") { }
    Assert(Released("failure"), "Failed ordinary action retained its context.");

    await session.RetainConversationAsync("active-agent");
    var activeStarted = Signal();
    var activeFinish = Signal();
    var active = session.UseAsync(async (_, token) => { activeStarted.SetResult(true); return await activeFinish.Task.WaitAsync(token); }, chatId: "active-agent");
    await activeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await session.ReleaseConversationAsync("active-agent");
    Assert(!Released("active-agent"), "Final Agent release retired a still-active context.");
    activeFinish.SetResult(true);
    await active;
    Assert(Released("active-agent"), "Deferred Agent release was lost.");

    await SendChat("cancel");
    var cancelStarted = Signal();
    using var cancellation = new CancellationTokenSource();
    var cancelled = session.UseAsync<int>(async (_, token) => { cancelStarted.SetResult(true); await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; }, cancellation.Token, chatId: "cancel");
    await cancelStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    cancellation.Cancel();
    try { await cancelled; throw new Exception("Cancellation was swallowed."); } catch (OperationCanceledException) { }
    Assert(Released("cancel"), "Cancelled warm context was not retired.");

    await SendChat("host-idle");
    clock.Advance(TimeSpan.FromMinutes(2));
    await Eventually(() => host.ActiveTransports == 0, "Idle host did not shut down.");
    await SendChat("replacement");
    Assert(host.Initializations.Count == 2 && host.ActiveTransports == 1, "Idle host did not reinitialize correctly.");
    await session.DisposeAsync();
    clock.Advance(TimeSpan.FromMinutes(10));
    Assert(host.ActiveTransports == 0 && !Released("replacement"), "Disposed host was touched by an obsolete idle timer.");
    Console.WriteLine("PASS: bounded warm context cache, independent expiry, renewed activity, retained owners, failure/cancellation, and host disposal");
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

sealed class ManualLifetimeClock : TimeProvider
{
    private readonly object _sync = new();
    private readonly HashSet<ManualTimer> _timers = [];
    private long _ticks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan duration)
    {
        ManualTimer[] due;
        lock (_sync)
        {
            _ticks += duration.Ticks;
            due = _timers.Where(timer => timer.Due <= _ticks).ToArray();
            foreach (var timer in due) _timers.Remove(timer);
        }
        foreach (var timer in due) timer.Fire();
    }

    private sealed class ManualTimer(ManualLifetimeClock clock, TimerCallback callback, object? state) : ITimer
    {
        private bool _disposed;
        public long Due { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException();
            lock (clock._sync)
            {
                if (_disposed) return false;
                clock._timers.Remove(this);
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    Due = clock._ticks + dueTime.Ticks;
                    clock._timers.Add(this);
                }
                return true;
            }
        }
        public void Fire() => callback(state);
        public void Dispose()
        {
            lock (clock._sync)
            {
                _disposed = true;
                clock._timers.Remove(this);
            }
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

sealed class FakeHost(string root, bool authorized) : IBrowserIntegrationHost
{
    public int BrowserIdleMinutes { get; set; }
    public List<BrowserTransportOptions> Initializations { get; } = [];
    public List<(string Operation, string Arguments)> Invocations { get; } = [];
    public List<string> TurnPayloads { get; } = [];
    public IReadOnlyList<IntegrationModel> ModelResults { get; set; } = [];
    public int PromptCount { get; set; }
    public int ActiveTransports { get; set; }
    public int ShowCount { get; set; }
    public System.Collections.Concurrent.ConcurrentQueue<string> ReleasedContexts { get; } = new();
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
        string operation, object? arguments = null,
        CancellationToken cancellationToken = default)
    {
        host.Invocations.Add((operation, JsonSerializer.Serialize(arguments, JsonOptions)));
        cancellationToken.ThrowIfCancellationRequested();
        if (operation == "$diagnostics/snapshot")
            return Result<TResult>(new { snapshot = true });
        if (operation == "sendPrompt")
            return Result<TResult>(new { ok = true, text = "guest-ok" });
        if (operation != "typedHttp")
            throw new InvalidOperationException($"Unexpected fake operation '{operation}'.");

        var request = (HttpTransportRequest)(arguments ?? throw new InvalidOperationException());
        if (request.Path.StartsWith("/backend-api/gizmos/snorlax/sidebar?", StringComparison.Ordinal))
        {
            var projects = request.Path.Contains("cursor=next", StringComparison.Ordinal)
                ? """{"items":[{"gizmo":{"id":"g-p-two","display":{"name":"Two"}}}],"cursor":null}"""
                : """{"items":[{"gizmo":{"gizmo":{"id":"g-p-one","display":{"name":"One"}}}},{"gizmo":{"id":"not-a-project","display":{"name":"Ignore"}}}],"cursor":"next"}""";
            return Result<TResult>(new HttpTransportResponse(200, projects, "application/json"));
        }
        if (request.Path.StartsWith("/backend-api/models", StringComparison.Ordinal))
        {
            var catalog = new { models = host.ModelResults.Select(model =>
                new { slug = model.Id, title = model.Name }).ToArray() };
            return Result<TResult>(new HttpTransportResponse(200,
                JsonSerializer.Serialize(catalog, JsonOptions), "application/json"));
        }
        if (request.Path.StartsWith("/backend-api/settings/user_last_used_model_config", StringComparison.Ordinal))
            return Result<TResult>(new HttpTransportResponse(200, "", "application/json"));
        if (request.Path == "/backend-api/conversation/init")
            return Result<TResult>(new HttpTransportResponse(200,
                """{"default_model_slug":"served-test"}""", "application/json"));
        if (request.Path.StartsWith("/backend-api/conversation/", StringComparison.Ordinal))
        {
            if (host.UnavailableContinuationOnce &&
                request.Path == "/backend-api/conversation/stale-conversation")
            {
                host.UnavailableContinuationOnce = false;
                return Result<TResult>(new HttpTransportResponse(404, "",
                    ProviderCode: "conversation_inaccessible"));
            }
            if (host.TurnPayloads.Count == 0)
                return Result<TResult>(new HttpTransportResponse(200,
                    """{"conversation_id":"test","mapping":{}}""", "application/json"));
            using var payload = JsonDocument.Parse(host.TurnPayloads[^1]);
            var messageId = payload.RootElement.GetProperty("messages")[0].GetProperty("id").GetString();
            var projectId = payload.RootElement.TryGetProperty("conversation_mode", out var mode) &&
                mode.TryGetProperty("gizmo_id", out var id) ? id.GetString() : null;
            var response = new {
                conversation_id = "test", gizmo_id = projectId,
                current_node = "assistant",
                mapping = new Dictionary<string, object> {
                    ["assistant"] = new { parent = "user",
                        message = new { id = "assistant",
                            author = new { role = "assistant" },
                            status = "finished_successfully",
                            content = new { parts = new [] { "ok" } },
                            metadata = new { model_slug = "served-test" } } },
                    ["user"] = new { parent = (string?)null,
                        message = new { id = messageId,
                            author = new { role = "user" },
                            status = "finished_successfully",
                            content = new { parts = new [] { "hello" } },
                            metadata = new { resolved_model_slug = "served-test" } } }
                }
            };
            return Result<TResult>(new HttpTransportResponse(200,
                JsonSerializer.Serialize(response, JsonOptions), "application/json"));
        }
        throw new InvalidOperationException($"Unexpected typed HTTP path '{request.Path}'.");
    }

    public async Task<TResult> InvokeWithEventsAsync<TResult>(
        string operation, object? arguments, Action<JsonElement> reportEvent,
        Action<BrowserOperationProgress>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        host.Invocations.Add((operation, JsonSerializer.Serialize(arguments, JsonOptions)));
        if (operation != "typedHttp")
            throw new InvalidOperationException("Expected a typed HTTP stream.");
        var request = (HttpTransportRequest)(arguments ?? throw new InvalidOperationException());
        if (!request.Stream || request.Path != "/backend-api/f/conversation" ||
            request.NativeHook != "chatgptTurn")
            throw new InvalidOperationException("Unexpected ChatGPT stream operation.");
        if (host.AuthorizationFailureOnce)
        {
            host.AuthorizationFailureOnce = false;
            host.Authorized = false;
            throw new BrowserAuthorizationRequiredException("Authorization required.");
        }
        if (host.HangNextPrompt)
        {
            host.HangNextPrompt = false;
            host.PromptStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        host.PromptCount++;
        host.TurnPayloads.Add(request.Body ?? "");
        reportEvent(JsonSerializer.SerializeToElement(
            """{"conversation_id":"test"}"""));
        return await Result<TResult>(new HttpTransportResponse(200, "", "text/event-stream"));
    }

    public Task ReleaseContextAsync(string chatId, CancellationToken cancellationToken = default)
    {
        host.ReleasedContexts.Enqueue(chatId);
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
        if (!_disposed) { _disposed = true; host.ActiveTransports--; }
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
