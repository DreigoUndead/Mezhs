using Mezhs.Browser;

namespace Mezhs.Integrations.Browser;

/// <summary>
/// Owns a shared browser host, short transport-state critical sections,
/// per-account authorization, active sends, and warm-host idle shutdown.
/// </summary>
public sealed class BrowserAccountSession : IAsyncDisposable
{
    private const int MaxIdleContexts = 8;
    private readonly IBrowserIntegrationHost _host;
    private readonly TimeProvider _timeProvider;
    private readonly Func<bool, bool, BrowserTransportOptions> _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private IChatBrowserTransport? _transport;
    private CancellationTokenSource? _idleCancellation;
    private Task? _idleTask;
    private int _activeUses;
    private readonly Dictionary<string, int> _retainedChats = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _activeChats = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IdleContext> _idleContexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _discardChats = new(StringComparer.OrdinalIgnoreCase);
    private long _idleSequence;
    private bool _disposed;

    private sealed record IdleContext(long Sequence, CancellationTokenSource Cancellation);

    internal BrowserAccountSession(
        IBrowserIntegrationHost host,
        Func<bool, bool, BrowserTransportOptions> options,
        TimeProvider? timeProvider = null)
    {
        _host = host;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<TResult> UseAsync<TResult>(
        Func<IChatBrowserTransport, CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken = default,
        Action<string>? reportStage = null,
        string? chatId = null) =>
        UseCoreAsync(action, authorize: true, interactive: true, ephemeral: false,
            chatId, cancellationToken, reportStage);

    public Task<TResult> UseGuestAsync<TResult>(
        Func<IChatBrowserTransport, CancellationToken, Task<TResult>> action,
        string chatId,
        CancellationToken cancellationToken = default) =>
        UseCoreAsync(action, authorize: false, interactive: false, ephemeral: true,
            chatId, cancellationToken);

    public async Task<TResult> UseAuthorizedAsync<TResult>(
        Func<IChatBrowserTransport, CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await UseCoreAsync(action, authorize: true, interactive: false,
                ephemeral: false, chatId: null, cancellationToken);
        }
        catch (BrowserAuthorizationRequiredException ex)
        {
            throw new IntegrationAuthorizationRequiredException(ex.Message);
        }
    }

    public async Task LoginAsync(CancellationToken cancellationToken = default) =>
        await UseCoreAsync(
            static (_, _) => Task.FromResult(0),
            authorize: true, interactive: true, ephemeral: false,
            chatId: null, cancellationToken);

    public async Task OpenBrowserAsync(CancellationToken cancellationToken = default) =>
        await UseCoreAsync(
            static async (transport, token) =>
            {
                await transport.ShowAsync(token);
                return 0;
            },
            authorize: false, interactive: false, ephemeral: false,
            chatId: null, cancellationToken);

    // Reuse the current host only. No authorization UI, transport initialization or
    // context creation is permitted merely to stop an in-flight provider turn.
    public async Task<BrowserStopOutcome> StopAsync(string chatId, string messageId,
        CancellationToken cancellationToken = default)
    {
        IChatBrowserTransport? transport;
        await _gate.WaitAsync(cancellationToken);
        try { transport = _disposed ? null : _transport; }
        finally { _gate.Release(); }
        return transport is null ? BrowserStopOutcome.NotActive
            : await transport.StopTurnAsync(chatId, messageId, cancellationToken);
    }

    public async Task RetainConversationAsync(string chatId)
    {
        await _gate.WaitAsync();
        try
        {
            ThrowIfDisposed();
            _retainedChats[chatId] = _retainedChats.GetValueOrDefault(chatId) + 1;
            CancelIdleContext(chatId);
            CancelIdle(); // Keep the same authenticated context through Agent shell/wait cycles.
        }
        finally { _gate.Release(); }
    }

    public async Task ReleaseConversationAsync(string chatId)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_retainedChats.TryGetValue(chatId, out var count)) return;
            if (count > 1)
            {
                _retainedChats[chatId] = count - 1;
                return; // An overlapping execution still owns this chat context.
            }
            _retainedChats.Remove(chatId);
            try
            {
                if (_activeChats.ContainsKey(chatId))
                    _discardChats.Add(chatId);
                else if (_transport is not null)
                    await ReleaseContextBestEffortAsync(_transport, chatId);
            }
            finally
            {
                if (!_disposed && _activeUses == 0) ScheduleIdle();
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<TResult> UseCoreAsync<TResult>(
        Func<IChatBrowserTransport, CancellationToken, Task<TResult>> action,
        bool authorize,
        bool interactive,
        bool ephemeral,
        string? chatId,
        CancellationToken cancellationToken,
        Action<string>? reportStage = null)
    {
        IChatBrowserTransport transport;
        reportStage?.Invoke("Waiting for browser transport.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            CancelIdle();
            if (_transport is null) reportStage?.Invoke("Starting browser host.");
            await EnsureTransportAsync(cancellationToken);
            transport = _transport!;
            _activeUses++; // Keep the shared host alive, including during interactive login.
            if (!ephemeral && chatId is not null)
            {
                CancelIdleContext(chatId);
                _activeChats[chatId] = _activeChats.GetValueOrDefault(chatId) + 1;
            }
        }
        finally
        {
            if (!_disposed && _activeUses == 0) ScheduleIdle();
            _gate.Release();
        }

        var succeeded = false;
        try
        {
            // Do not hold the shared transport lock while the user signs in.
            if (authorize)
                await EnsureAuthorizedForUseAsync(transport, interactive, cancellationToken, reportStage);
            cancellationToken.ThrowIfCancellationRequested();
            var contextTransport = chatId is null ? transport : transport.ForContext(chatId, ephemeral);
            reportStage?.Invoke("Browser session ready; preparing request.");
            var result = await action(contextTransport, cancellationToken);
            succeeded = true;
            return result;
        }
        finally
        {
            await _gate.WaitAsync();
            try
            {
                // MessageService owns same-chat send ordering; track active use here only
                // to prevent lifetime transitions from retiring an in-use context.
                if (!ephemeral && chatId is not null)
                {
                    if (!succeeded || cancellationToken.IsCancellationRequested)
                        _discardChats.Add(chatId);
                    var remaining = _activeChats[chatId] - 1;
                    if (remaining != 0) _activeChats[chatId] = remaining;
                    else
                    {
                        _activeChats.Remove(chatId);
                        var discard = _discardChats.Remove(chatId);
                        if (!_disposed && ReferenceEquals(_transport, transport))
                        {
                            if (discard)
                                await ReleaseContextBestEffortAsync(transport, chatId);
                            else if (!_retainedChats.ContainsKey(chatId))
                            {
                                if (_host.BrowserIdleMinutes > 0)
                                    await KeepIdleContextAsync(transport, chatId);
                                else
                                    await ReleaseContextBestEffortAsync(transport, chatId);
                            }
                        }
                    }
                }
                _activeUses--;
                if (!_disposed && _activeUses == 0) ScheduleIdle();
            }
            finally { _gate.Release(); }
        }
    }

    private async Task EnsureAuthorizedForUseAsync(
        IChatBrowserTransport transport, bool interactive, CancellationToken cancellationToken,
        Action<string>? reportStage)
    {
        // Only the first request opens the login UI. Requests already waiting for
        // that attempt recheck cookies, then proceed or report authorization required.
        var first = await _loginGate.WaitAsync(0, cancellationToken);
        if (!first)
        {
            reportStage?.Invoke("Waiting for browser account authorization.");
            await _loginGate.WaitAsync(cancellationToken);
        }
        try
        {
            reportStage?.Invoke("Checking browser account authorization.");
            await transport.EnsureAuthorizedAsync(interactive && first, cancellationToken);
        }
        finally { _loginGate.Release(); }
    }

    private static async Task ReleaseContextBestEffortAsync(
        IChatBrowserTransport transport, string chatId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await transport.ReleaseContextAsync(chatId, timeout.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not release browser context '{chatId}': {ex.Message}");
        }
    }

    private async Task EnsureTransportAsync(CancellationToken cancellationToken)
    {
        if (_transport is not null) return;
        var candidate = _host.CreateBrowserTransport();
        try
        {
            await candidate.InitializeAsync(_options(false, false), cancellationToken);
            _transport = candidate;
        }
        catch
        {
            await candidate.DisposeAsync();
            throw;
        }
    }

    // All cache mutations occur under _gate, including expiry and LRU eviction.
    private async Task KeepIdleContextAsync(IChatBrowserTransport transport, string chatId)
    {
        CancelIdleContext(chatId);
        if (_idleContexts.Count >= MaxIdleContexts)
        {
            var oldest = _idleContexts.MinBy(pair => pair.Value.Sequence).Key;
            CancelIdleContext(oldest);
            await ReleaseContextBestEffortAsync(transport, oldest);
        }
        var idle = new IdleContext(++_idleSequence, new CancellationTokenSource());
        _idleContexts.Add(chatId, idle);
        _ = ExpireContextAsync(transport, chatId, idle);
    }

    private async Task ExpireContextAsync(IChatBrowserTransport transport, string chatId, IdleContext idle)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(_host.BrowserIdleMinutes), _timeProvider, idle.Cancellation.Token);
            await _gate.WaitAsync(idle.Cancellation.Token);
            try
            {
                if (!_disposed && ReferenceEquals(_transport, transport) &&
                    _idleContexts.TryGetValue(chatId, out var current) && ReferenceEquals(current, idle))
                {
                    _idleContexts.Remove(chatId);
                    await ReleaseContextBestEffortAsync(transport, chatId);
                }
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (idle.Cancellation.IsCancellationRequested) { }
        finally
        {
            await _gate.WaitAsync();
            try { idle.Cancellation.Dispose(); }
            finally { _gate.Release(); }
        }
    }

    private void CancelIdleContext(string chatId)
    {
        if (_idleContexts.Remove(chatId, out var idle)) idle.Cancellation.Cancel();
    }

    private void CancelAllIdleContexts()
    {
        foreach (var idle in _idleContexts.Values) idle.Cancellation.Cancel();
        _idleContexts.Clear();
    }

    private void ScheduleIdle()
    {
        CancelIdle();
        if (_transport is null || _host.BrowserIdleMinutes == 0 || _disposed ||
            _activeUses != 0 || _retainedChats.Count != 0) return;
        var cancellation = new CancellationTokenSource();
        _idleCancellation = cancellation;
        _idleTask = DisposeWhenIdleAsync(cancellation);
    }

    private async Task DisposeWhenIdleAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(_host.BrowserIdleMinutes), _timeProvider, cancellation.Token);
            await _gate.WaitAsync(cancellation.Token);
            try
            {
                if (ReferenceEquals(_idleCancellation, cancellation) &&
                    _activeUses == 0 && _retainedChats.Count == 0)
                {
                    CancelAllIdleContexts();
                    var transport = _transport;
                    _transport = null;
                    if (transport is not null) await transport.DisposeAsync();
                }
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { }
        finally
        {
            await _gate.WaitAsync();
            try
            {
                if (ReferenceEquals(_idleCancellation, cancellation))
                {
                    _idleCancellation = null;
                    _idleTask = null;
                }
                cancellation.Dispose();
            }
            finally { _gate.Release(); }
        }
    }

    private void CancelIdle()
    {
        var cancellation = _idleCancellation;
        _idleCancellation = null;
        cancellation?.Cancel();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BrowserAccountSession));
    }

    public async ValueTask DisposeAsync()
    {
        Task? idleTask;
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            idleTask = _idleTask;
            CancelIdle();
            CancelAllIdleContexts();
            var transport = _transport;
            _transport = null;
            if (transport is not null) await transport.DisposeAsync();
        }
        finally
        {
            _gate.Release();
            // Leave semaphore valid for any already-returning in-flight calls during shutdown.
        }
        if (idleTask is not null) await idleTask;
    }
}
