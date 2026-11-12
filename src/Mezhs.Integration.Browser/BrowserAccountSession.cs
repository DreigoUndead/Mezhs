using Mezhs.Browser;

namespace Mezhs.Integrations.Browser;

/// <summary>
/// Owns a shared browser host, short authorization/initialization critical sections,
/// active send lifetimes and the existing warm-host idle shutdown policy.
/// </summary>
public sealed class BrowserAccountSession : IAsyncDisposable
{
    private readonly IBrowserIntegrationHost _host;
    private readonly Func<bool, bool, BrowserTransportOptions> _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChatBrowserTransport? _transport;
    private CancellationTokenSource? _idleCancellation;
    private Task? _idleTask;
    private int _activeUses;
    private readonly Dictionary<string, int> _retainedChats = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    internal BrowserAccountSession(
        IBrowserIntegrationHost host,
        Func<bool, bool, BrowserTransportOptions> options)
    {
        _host = host;
        _options = options;
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

    public async Task LoginAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            CancelIdle();
            await EnsureTransportAsync(cancellationToken);
            await _transport!.EnsureAuthorizedAsync(interactive: true, cancellationToken);
        }
        finally
        {
            if (!_disposed && _activeUses == 0) ScheduleIdle();
            _gate.Release();
        }
    }

    public async Task OpenBrowserAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            CancelIdle();
            await EnsureTransportAsync(cancellationToken);
            await _transport!.ShowAsync(cancellationToken);
        }
        finally
        {
            if (!_disposed && _activeUses == 0) ScheduleIdle();
            _gate.Release();
        }
    }

    public async Task RetainConversationAsync(string chatId)
    {
        await _gate.WaitAsync();
        try
        {
            ThrowIfDisposed();
            _retainedChats[chatId] = _retainedChats.GetValueOrDefault(chatId) + 1;
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
                if (_transport is not null)
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
        IChatBrowserTransport contextTransport;
        var admitted = false;
        reportStage?.Invoke("Checking browser session.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            CancelIdle();
            await EnsureTransportAsync(cancellationToken);
            if (authorize)
                await _transport!.EnsureAuthorizedAsync(interactive, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            contextTransport = chatId is null ? _transport! : _transport!.ForContext(chatId, ephemeral);
            _activeUses++;
            admitted = true;
        }
        finally
        {
            if (!admitted && !_disposed && _activeUses == 0) ScheduleIdle();
            _gate.Release();
        }

        var cancelled = false;
        try
        {
            reportStage?.Invoke("Browser session ready; preparing request.");
            return await action(contextTransport, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
            throw;
        }
        finally
        {
            await _gate.WaitAsync();
            try
            {
                // Ordinary account turns do not keep Chromium windows indefinitely.
                // A cancelled send must discard even an Agent-retained context before
                // a later explicit same-chat send can enter MessageService.
                if (!ephemeral && chatId is not null &&
                    (cancelled || !_retainedChats.ContainsKey(chatId)) &&
                    _transport is not null)
                    await ReleaseContextBestEffortAsync(_transport, chatId);
                _activeUses--;
                if (!_disposed && _activeUses == 0) ScheduleIdle();
            }
            finally { _gate.Release(); }
        }
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
            await Task.Delay(TimeSpan.FromMinutes(_host.BrowserIdleMinutes), cancellation.Token);
            await _gate.WaitAsync(cancellation.Token);
            try
            {
                if (ReferenceEquals(_idleCancellation, cancellation) &&
                    _activeUses == 0 && _retainedChats.Count == 0)
                {
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
            if (ReferenceEquals(_idleCancellation, cancellation))
            {
                _idleCancellation = null;
                _idleTask = null;
            }
            cancellation.Dispose();
        }
    }

    private void CancelIdle()
    {
        var cancellation = _idleCancellation;
        _idleCancellation = null;
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BrowserAccountSession));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var idleTask = _idleTask;
        CancelIdle();
        if (idleTask is not null) await idleTask;
        await _gate.WaitAsync();
        try
        {
            var transport = _transport;
            _transport = null;
            if (transport is not null) await transport.DisposeAsync();
        }
        finally
        {
            _gate.Release();
            // Leave semaphore valid for any already-returning in-flight calls during shutdown.
        }
    }
}
