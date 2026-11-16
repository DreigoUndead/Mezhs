$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('mezhs-admission-wait-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    $integration = Join-Path $root 'src\Mezhs.Integration.Browser\Mezhs.Integration.Browser.csproj'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$integration" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $temp 'Test.csproj') -Encoding UTF8
    @'
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Mezhs.Browser;
using Mezhs.Integrations;
using Mezhs.Integrations.Browser;

await CheckParallelAsync(1, 1);
await CheckParallelAsync(2, 2);
await CheckParallelAsync(0, 3);
await CheckCapacityCancellationAsync();
await CheckSpacingCancellationAsync();
Console.WriteLine("PASS: model-send admission limits (1, 2, unlimited), genuine capacity/spacing Waiting, cancellation, permit cleanup, provider activity isolation.");

static async Task CheckParallelAsync(int capacity, int immediate)
{
    await using var probe = new ProbeIntegration(capacity, 0);
    var trace = new Trace();
    var tasks = Enumerable.Range(1, 3)
        .Select(n => probe.SendMessageAsync(Context(n.ToString(), trace))).ToArray();
    await UntilAsync(() => probe.Started.Count == immediate, $"maxParallel={capacity} immediate admissions");
    Assert(probe.Started.Count == immediate, "Unexpected number of active sends.");
    if (capacity > 0)
    {
        await UntilAsync(() => trace.IsBlocked("3", IntegrationAdmissionWaitReason.Capacity),
            $"maxParallel={capacity} capacity waiting");
    }
    else Assert(!trace.AnyBlocked, "Unlimited connection must never signal capacity waiting.");

    for (var i = 1; i <= 3; i++)
    {
        var id = i.ToString();
        await UntilAsync(() => probe.Started.ContainsKey(id), "send admission " + id);
        Assert(!trace.IsBlocked(id), "Admission Waiting was not cleared for " + id);
        Assert(trace.ProviderWaiting.ContainsKey(id) && !trace.IsBlocked(id),
            "Provider waiting after admission incorrectly controls integration admission.");
        probe.Release(id);
    }
    await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
    Assert(!trace.AnyBlocked, "Completed sends left stale admission state.");
}

static async Task CheckCapacityCancellationAsync()
{
    await using var probe = new ProbeIntegration(1, 0);
    var trace = new Trace();
    var first = probe.SendMessageAsync(Context("first", trace));
    await UntilAsync(() => probe.Started.ContainsKey("first"), "first capacity sender");
    using var cancel = new CancellationTokenSource();
    var blocked = probe.SendMessageAsync(Context("blocked", trace), cancel.Token);
    await UntilAsync(() => trace.IsBlocked("blocked", IntegrationAdmissionWaitReason.Capacity),
        "blocked capacity sender");
    cancel.Cancel();
    await ExpectCancelled(blocked);
    Assert(!trace.IsBlocked("blocked"), "Cancelled capacity waiter retained Waiting.");
    probe.Release("first");
    await first;
    var next = probe.SendMessageAsync(Context("next", trace));
    await UntilAsync(() => probe.Started.ContainsKey("next"), "capacity after cancel");
    Assert(!trace.IsBlocked("next"), "Free capacity falsely signalled Waiting.");
    probe.Release("next");
    await next;
}

static async Task CheckSpacingCancellationAsync()
{
    await using var probe = new ProbeIntegration(0, 5000);
    var trace = new Trace();
    var first = probe.SendMessageAsync(Context("first", trace));
    await UntilAsync(() => probe.Started.ContainsKey("first"), "first spacing sender");
    probe.Release("first");
    await first;
    using var cancel = new CancellationTokenSource();
    var blocked = probe.SendMessageAsync(Context("blocked", trace), cancel.Token);
    await UntilAsync(() => trace.IsBlocked("blocked", IntegrationAdmissionWaitReason.Spacing),
        "positive spacing waiter");
    Assert(!probe.Started.ContainsKey("blocked"), "Spacing waiter reached provider early.");
    cancel.Cancel();
    await ExpectCancelled(blocked);
    Assert(!trace.IsBlocked("blocked"), "Cancelled spacing waiter retained Waiting.");
    var admitted = probe.SendMessageAsync(Context("admitted", trace));
    await UntilAsync(() => trace.IsBlocked("admitted", IntegrationAdmissionWaitReason.Spacing),
        "subsequent spacing waiter", 2000);
    await UntilAsync(() => probe.Started.ContainsKey("admitted"), "spacing admission", 7000);
    Assert(!trace.IsBlocked("admitted"), "Spacing Waiting not cleared on admission.");
    var elapsedMs = Stopwatch.GetElapsedTime(probe.StartTicks["first"], probe.StartTicks["admitted"]).TotalMilliseconds;
    Assert(elapsedMs >= 4900, $"Configured 5000 ms spacing was violated: {elapsedMs:F1} ms.");
    Assert(trace.ProviderWaiting.ContainsKey("admitted"),
        "Probe must report provider waiting independently from admission.");
    probe.Release("admitted");
    await admitted;
    Assert(!trace.AnyBlocked, "Spacing test retained stale admission state.");
}

static (ChatContext Chat, PromptRequest Request) Context(string id, Trace trace) =>
    (new ChatContext("chat-" + id, "probe", null,
        (_, _, _, _) => Task.FromResult<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>()),
        reportActivity: activity => trace.OnProvider(id, activity),
        reportAdmissionWait: reason => trace.OnAdmission(id, reason)),
     new PromptRequest(id, "probe", null, []));

static async Task ExpectCancelled(Task<PromptResult> task)
{
    try { await task; }
    catch (OperationCanceledException) { return; }
    throw new InvalidOperationException("Cancelled admission unexpectedly completed successfully.");
}

static async Task UntilAsync(Func<bool> predicate, string label, int timeoutMs = 3000)
{
    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    while (DateTime.UtcNow < deadline)
    {
        if (predicate()) return;
        await Task.Delay(10);
    }
    throw new InvalidOperationException("Timed out observing " + label);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class Trace
{
    private readonly ConcurrentDictionary<string, IntegrationAdmissionWaitReason> _blocked = new();
    public ConcurrentDictionary<string, bool> ProviderWaiting { get; } = new();
    public bool AnyBlocked => !_blocked.IsEmpty;
    public bool IsBlocked(string id, IntegrationAdmissionWaitReason reason) =>
        _blocked.TryGetValue(id, out var actual) && actual == reason;
    public bool IsBlocked(string id) => _blocked.ContainsKey(id);
    public void OnAdmission(string id, IntegrationAdmissionWaitReason? reason)
    {
        if (reason.HasValue) _blocked[id] = reason.Value;
        else _blocked.TryRemove(id, out _);
    }
    public void OnProvider(string id, IntegrationActivity activity)
    {
        if (activity.State == "waiting") ProviderWaiting[id] = true;
    }
}

sealed class ProbeIntegration(int maxParallel, int minInterval)
    : BrowserIntegrationBase(
        new IntegrationConnection("probe", "Probe", "probe",
            new Dictionary<string, string?>(),
            new IntegrationLimits { MaxParallel = maxParallel, MinInterval = minInterval }),
        new ProbeHost())
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _holds = new();
    public ConcurrentDictionary<string, bool> Started { get; } = new();
    public ConcurrentDictionary<string, long> StartTicks { get; } = new();
    protected override Assembly BrowserModuleAssembly => typeof(ProbeIntegration).Assembly;
    protected override string BrowserModuleResourceName => "unused";

    public Task<PromptResult> SendMessageAsync((ChatContext Chat, PromptRequest Request) context,
        CancellationToken cancellationToken = default) =>
        PromptAsync(context.Chat, context.Request, cancellationToken);

    public void Release(string id) =>
        (_holds.TryGetValue(id, out var hold) ? hold :
            throw new InvalidOperationException("Provider turn was not admitted: " + id))
        .TrySetResult(true);

    protected override async Task<PromptResult> SendCoreAsync(
        ChatContext chat,
        PromptRequest request,
        CancellationToken cancellationToken)
    {
        var id = request.MessageId;
        var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_holds.TryAdd(id, hold)) throw new InvalidOperationException("Duplicate provider send " + id);
        StartTicks[id] = Stopwatch.GetTimestamp();
        Started[id] = true;
        chat.ReportActivity?.Invoke(new IntegrationActivity("waiting"));
        await hold.Task.WaitAsync(cancellationToken);
        return new PromptResult("ok", CreateMessageId(), "default");
    }
}

sealed class ProbeHost : IBrowserIntegrationHost
{
    public int BrowserIdleMinutes => 0;
    public string GetConnectionRoot(string connectionId) => Path.GetTempPath();
    public IChatBrowserTransport CreateBrowserTransport() =>
        throw new InvalidOperationException("The admission test must never create a real browser.");
}
'@ | Set-Content -LiteralPath (Join-Path $temp 'Program.cs') -Encoding UTF8

    dotnet run --project (Join-Path $temp 'Test.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Admission wait regression test failed.' }
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
