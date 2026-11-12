using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Mezhs.Browser;

namespace Mezhs.Integrations.Browser;

public abstract class BrowserIntegrationBase(
    IntegrationConnection connection,
    IIntegrationHost host) : ChatIntegrationBase(connection), IConversationContextOwner
{
    private BrowserAccountSession? _accountSession;
    private BrowserAccountSession? _guestSession;
    private readonly object _sessionSync = new();
    private BrowserDiagnosticsModule? _diagnostics;
    private readonly SemaphoreSlim? _parallelGate = connection.Limits?.MaxParallel is > 0
        ? new SemaphoreSlim(connection.Limits.MaxParallel)
        : connection.Limits is null ? new SemaphoreSlim(1) : null;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private long _lastStartTimestamp;

    protected IBrowserIntegrationHost Host { get; } = BrowserIntegrationHost.Require(host);
    protected abstract Assembly BrowserModuleAssembly { get; }
    protected abstract string BrowserModuleResourceName { get; }
    protected virtual string? BrowserModuleEntryFileName => null;

    protected string PersistentProfilePath =>
        Path.Combine(Host.GetConnectionRoot(Connection.Id), "profile");

    protected BrowserTransportOptions TransportOptions(
        string profileDirectory,
        bool showBrowser,
        bool requireAuthorization,
        bool guest = false) => new(
            profileDirectory,
            GetBrowserModulePath(),
            showBrowser,
            requireAuthorization,
        Guest: guest);

    protected BrowserAccountSession CreateAccountSession()
    {
        lock (_sessionSync) return _accountSession ??= new(
        Host,
        (showBrowser, requireAuthorization) => TransportOptions(
            PersistentProfilePath,
            showBrowser,
            requireAuthorization));
    }

    private BrowserAccountSession CreateGuestSession()
    {
        lock (_sessionSync) return _guestSession ??= new(
        Host,
        (showBrowser, requireAuthorization) => TransportOptions(
            Path.Combine(Host.GetConnectionRoot(Connection.Id), "guest"),
            showBrowser,
            requireAuthorization,
            guest: true));
    }

    public Task RetainConversationAsync(string chatId) =>
        CreateAccountSession().RetainConversationAsync(chatId);

    public Task ReleaseConversationAsync(string chatId) =>
        _accountSession?.ReleaseConversationAsync(chatId) ?? Task.CompletedTask;

    public override IIntegrationDiagnosticsModule? Diagnostics =>
        _accountSession is null ? null : _diagnostics ??= new BrowserDiagnosticsModule(_accountSession);

    public override async ValueTask DisposeAsync()
    {
        if (_accountSession is not null) await _accountSession.DisposeAsync();
        if (_guestSession is not null) await _guestSession.DisposeAsync();
    }

    // All browser integrations (account and guest) use the same admission owner.
    public sealed override async Task<IntegrationSendResult> SendMessageAsync(
        IntegrationSendContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_parallelGate is not null)
            await _parallelGate.WaitAsync(cancellationToken);
        try
        {
            await _startGate.WaitAsync(cancellationToken);
            try
            {
                var spacingMs = Connection.Limits?.MinIntervalBetweenStartsMs ?? 0;
                if (spacingMs > 0 && _lastStartTimestamp != 0)
                {
                    var remaining = spacingMs - Stopwatch.GetElapsedTime(_lastStartTimestamp).TotalMilliseconds;
                    if (remaining > 0)
                        await Task.Delay(TimeSpan.FromMilliseconds(remaining), cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                _lastStartTimestamp = Stopwatch.GetTimestamp();
            }
            finally
            {
                _startGate.Release();
            }
            return await SendCoreAsync(context, cancellationToken);
        }
        finally
        {
            _parallelGate?.Release();
        }
    }

    protected abstract Task<IntegrationSendResult> SendCoreAsync(
        IntegrationSendContext context,
        CancellationToken cancellationToken);

    protected async Task<IntegrationSendResult> SendAnonymousAsync(
        IntegrationSendContext context,
        CancellationToken cancellationToken)
    {
        return await CreateGuestSession().UseGuestAsync(async (transport, token) =>
        {
            var response = await transport.InvokeAsync<BrowserSendResult>(
                "sendPrompt", new { context.Prompt, NewChat = true }, token);
            if (!response.Ok)
                throw new InvalidOperationException(response.Error ?? "Anonymous browser request failed.");
            return new IntegrationSendResult(response.Text);
        }, context.Chat.ChatId, cancellationToken);
    }

    private string GetBrowserModulePath()
    {
        var assembly = BrowserModuleAssembly;
        var entryFileName = BrowserModuleEntryFileName ?? $"{assembly.GetName().Name}.js";
        var prefix = BrowserModuleResourceName + ".";
        string[] resourceNames = BrowserModuleEntryFileName is null
            ? [BrowserModuleResourceName]
            : assembly.GetManifestResourceNames()
                .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        if (resourceNames.Length == 0)
            throw new InvalidOperationException($"No browser module resources found under '{prefix}'.");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = new List<(string Name, byte[] Contents)>(resourceNames.Length);
        foreach (var resourceName in resourceNames)
        {
            using var source = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Browser module resource '{resourceName}' was not found in '{assembly.GetName().Name}'.");
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            var name = BrowserModuleEntryFileName is null ? entryFileName : resourceName[prefix.Length..];
            var contents = buffer.ToArray();
            files.Add((name, contents));
            hash.AppendData(Encoding.UTF8.GetBytes(name));
            hash.AppendData([0]);
            hash.AppendData(SHA256.HashData(contents));
        }
        if (!files.Any(file => file.Name == entryFileName))
            throw new InvalidOperationException($"Browser module entry '{entryFileName}' was not embedded.");

        var parent = Path.Combine(Host.GetConnectionRoot(Connection.Id), "modules", assembly.GetName().Name!);
        var directory = Path.Combine(parent, Convert.ToHexString(hash.GetHashAndReset()));
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(parent);
            var staging = Path.Combine(parent, $".{Guid.NewGuid():N}.tmp");
            Directory.CreateDirectory(staging);
            try
            {
                foreach (var file in files)
                    File.WriteAllBytes(Path.Combine(staging, file.Name), file.Contents);
                try
                {
                    Directory.Move(staging, directory);
                }
                catch (IOException) when (Directory.Exists(directory))
                {
                    // Another instance published the same complete version first.
                }
            }
            finally
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
        }

        foreach (var file in files)
        {
            var path = Path.Combine(directory, file.Name);
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(file.Contents))
                throw new IOException($"Published browser module is missing or differs from embedded resource: '{path}'.");
        }
        return Path.Combine(directory, entryFileName);
    }

    private sealed record BrowserSendResult(
        bool Ok,
        string Text = "",
        string? Error = null);
}
