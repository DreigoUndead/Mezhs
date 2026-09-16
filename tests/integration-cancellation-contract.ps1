$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('mezhs-integration-cancel-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null

try {
    $integrationProjects = @(Get-ChildItem -LiteralPath (Join-Path $root 'integrations') -Filter '*.csproj' -Recurse)
    if ($integrationProjects.Count -eq 0) { throw 'No integration projects were found.' }

    $integrationAssemblyNames = ($integrationProjects | ForEach-Object {
        [System.IO.Path]::GetFileNameWithoutExtension($_.Name)
    }) -join ';'

    $projectReferences = @(
        (Join-Path $root 'src\Mezhs.Integration.Abstractions\Mezhs.Integration.Abstractions.csproj'),
        (Join-Path $root 'src\Mezhs.Integration.Browser\Mezhs.Integration.Browser.csproj'),
        (Join-Path $root 'transports\Mezhs.Browser.Abstractions\Mezhs.Browser.Abstractions.csproj')
    ) + @($integrationProjects | Select-Object -ExpandProperty FullName)

    $referenceXml = ($projectReferences | Sort-Object -Unique | ForEach-Object {
        '    <ProjectReference Include="' + $_ + '" />'
    }) -join [Environment]::NewLine

    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
$referenceXml
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $temp 'Test.csproj') -Encoding UTF8

    @'
using System.Collections.Concurrent;
using System.Reflection;
using Mezhs.Browser;
using Mezhs.Integrations;
using Mezhs.Integrations.Browser;

var tempRoot = args[0];
var integrationAssemblies = args[1]
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(name =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, name + ".dll");
        if (!File.Exists(path))
            throw new InvalidOperationException($"Integration assembly '{name}' was not produced by its project reference.");
        return Assembly.LoadFrom(path);
    })
    .ToArray();

var registrations = integrationAssemblies
    .SelectMany(GetLoadableTypes)
    .Where(type => !type.IsAbstract && typeof(IChatIntegration).IsAssignableFrom(type))
    .SelectMany(type => type.GetCustomAttributes<IntegrationAttribute>()
        .Select(attribute => new Registration(attribute.Type, type)))
    .OrderBy(registration => registration.Name, StringComparer.Ordinal)
    .ToArray();

if (registrations.Length == 0)
    throw new InvalidOperationException("No integration registrations were discovered.");

foreach (var registration in registrations)
{
    await VerifyPreCancelledAsync(registration, tempRoot);
    await VerifyInFlightCancellationAsync(registration, tempRoot);
}

Console.WriteLine($"PASS: {registrations.Length} integration registrations honor cancellation; browser-backed integrations also discard cancelled transports before reuse.");

static async Task VerifyPreCancelledAsync(Registration registration, string tempRoot)
{
    await using var fixture = CreateFixture(registration, tempRoot, "pre");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    var send = fixture.Integration.SendMessageAsync(CreateContext(registration.Name), cancellation.Token);
    await ExpectCancelledAsync(send, registration.Name, "pre-cancelled");
    fixture.Host.AssertAllTransportsDisposed(registration.Name, "pre-cancelled");
}

static async Task VerifyInFlightCancellationAsync(Registration registration, string tempRoot)
{
    await using var fixture = CreateFixture(registration, tempRoot, "flight");
    using var cancellation = new CancellationTokenSource();

    var send = fixture.Integration.SendMessageAsync(CreateContext(registration.Name), cancellation.Token);
    var browserBacked = typeof(BrowserIntegrationBase).IsAssignableFrom(registration.Type);
    if (browserBacked)
        await fixture.Host.WaitForInvocationAsync(registration.Name, TimeSpan.FromSeconds(5));
    else if (send.IsCompleted)
    {
        await send;
        return;
    }

    var transportsBeforeCancel = fixture.Host.CreatedCount;
    cancellation.Cancel();
    await ExpectCancelledAsync(send, registration.Name, "in-flight");
    fixture.Host.AssertAllTransportsDisposed(registration.Name, "in-flight");

    if (!browserBacked)
        return;

    using var secondCancellation = new CancellationTokenSource();
    var secondSend = fixture.Integration.SendMessageAsync(CreateContext(registration.Name), secondCancellation.Token);
    await fixture.Host.WaitForInvocationAsync(registration.Name, TimeSpan.FromSeconds(5));
    if (fixture.Host.CreatedCount <= transportsBeforeCancel)
        throw new InvalidOperationException(
            $"Integration '{registration.Name}' reused a browser transport after cancellation.");

    secondCancellation.Cancel();
    await ExpectCancelledAsync(secondSend, registration.Name, "second in-flight");
    fixture.Host.AssertAllTransportsDisposed(registration.Name, "second in-flight");
}

static Fixture CreateFixture(Registration registration, string tempRoot, string suffix)
{
    var root = Path.Combine(tempRoot, Sanitize(registration.Name) + "-" + suffix);
    Directory.CreateDirectory(root);
    var host = new ProbeHost(root);
    var connection = new IntegrationConnection(
        registration.Name,
        registration.Name,
        registration.Name,
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
    var integration = Activator.CreateInstance(registration.Type, connection, host) as IChatIntegration
        ?? throw new InvalidOperationException(
            $"Integration '{registration.Name}' must expose the standard (IntegrationConnection, IIntegrationHost) constructor so the integration contract can exercise it.");
    return new Fixture(integration, host);
}

static IntegrationSendContext CreateContext(string connectionId)
{
    var message = new IntegrationMessageContext(
        "message_contract",
        "user",
        "cancellation contract probe",
        Completed: true,
        DateTimeOffset.UtcNow);
    return new IntegrationSendContext(
        new IntegrationChatContext("chat_contract", connectionId),
        message,
        Array.Empty<IntegrationMessageContext>(),
        Array.Empty<IntegrationInputFile>());
}

static async Task ExpectCancelledAsync(Task task, string integrationName, string phase)
{
    try
    {
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    catch (OperationCanceledException)
    {
        return;
    }
    catch (TimeoutException)
    {
        throw new InvalidOperationException(
            $"Integration '{integrationName}' did not terminate promptly for {phase} cancellation.");
    }

    throw new InvalidOperationException(
        $"Integration '{integrationName}' completed normally instead of honoring {phase} cancellation.");
}

static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
{
    try
    {
        return assembly.GetTypes();
    }
    catch (ReflectionTypeLoadException ex)
    {
        return ex.Types.OfType<Type>();
    }
}

static string Sanitize(string value)
{
    foreach (var invalid in Path.GetInvalidFileNameChars())
        value = value.Replace(invalid, '_');
    return value;
}

sealed record Registration(string Name, Type Type);

sealed class Fixture(IChatIntegration integration, ProbeHost host) : IAsyncDisposable
{
    public IChatIntegration Integration { get; } = integration;
    public ProbeHost Host { get; } = host;

    public ValueTask DisposeAsync() => Integration.DisposeAsync();
}

sealed class ProbeHost(string root) : IBrowserIntegrationHost
{
    private readonly ConcurrentQueue<ProbeTransport> _transports = new();
    private readonly SemaphoreSlim _invocations = new(0);
    private int _createdCount;

    public int BrowserIdleMinutes => 60;
    public int CreatedCount => Volatile.Read(ref _createdCount);

    public string GetConnectionRoot(string connectionId)
    {
        var path = Path.Combine(root, connectionId);
        Directory.CreateDirectory(path);
        return path;
    }

    public IChatBrowserTransport CreateBrowserTransport()
    {
        var transport = new ProbeTransport(this);
        _transports.Enqueue(transport);
        Interlocked.Increment(ref _createdCount);
        return transport;
    }

    public async Task WaitForInvocationAsync(string integrationName, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await _invocations.WaitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Browser-backed integration '{integrationName}' never reached its browser transport.");
        }
    }

    public void AssertAllTransportsDisposed(string integrationName, string phase)
    {
        var leaked = _transports.Where(transport => !transport.IsDisposed).ToArray();
        if (leaked.Length != 0)
            throw new InvalidOperationException(
                $"Integration '{integrationName}' left {leaked.Length} browser transport(s) alive after {phase} cancellation.");
    }

    internal void MarkInvoked() => _invocations.Release();
}

sealed class ProbeTransport(ProbeHost host) : IChatBrowserTransport
{
    private int _disposed;

    public string Name => "CancellationProbe";
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public Task InitializeAsync(
        BrowserTransportOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task<TResult> InvokeAsync<TResult>(
        string operation,
        object? arguments = null,
        CancellationToken cancellationToken = default)
    {
        host.MarkInvoked();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Cancellation probe unexpectedly resumed.");
    }

    public Task ShowAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }
}
'@ | Set-Content -LiteralPath (Join-Path $temp 'Program.cs') -Encoding UTF8

    dotnet run --project (Join-Path $temp 'Test.csproj') -c Release -- $temp $integrationAssemblyNames
    if ($LASTEXITCODE -ne 0) { throw 'Integration cancellation contract failed.' }
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
