using Mezhs.Browser;

namespace Mezhs.Integrations.Browser;

public sealed class BrowserAccountSession : IAsyncDisposable
{
    private readonly IBrowserIntegrationHost _host;
    private readonly Func<bool, bool, BrowserTransportOptions> _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChatBrowserTransport? _transport;
    private CancellationTokenSource? _idleCancellation;
    private Task? _idleTask;
    private bool _disposed;

    internal BrowserAccountSession(
        IBrowserIntegrationHost host,
        Func<bool, bool, BrowserTransportOptions> options)
    {
        _host = host;
        _options = options;
    }

    public async Task<TResult> UseAsync<TResult>(
        Func<IChatBrowserTransport, CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            CancelIdle();
            await EnsureAuthorizedTransportAsync(cancellationToken);
            return await action(_transport!, cancellationToken);
        }
        finally
        {
            if (!_disposed)
                ScheduleIdle();
            _gate.Release();
        }
    }

    public async Task LoginAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            CancelIdle();
            await EnsureInteractiveLoginAsync(cancellationToken);
            await DisposeTransportAsync();
        }
        finally
        {
            if (!_disposed)
                ScheduleIdle();
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var idleTask = _idleTask;
        CancelIdle();
        if (idleTask is not null)
            await idleTask;
        _idleTask = null;

        await _gate.WaitAsync();
        try
        {
            await DisposeTransportAsync();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task EnsureAuthorizedTransportAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureTransportAsync(
                showBrowser: false,
                requireAuthorization: true,
                cancellationToken);
        }
        catch (BrowserAuthorizationRequiredException)
        {
            await EnsureInteractiveLoginAsync(cancellationToken);
            await DisposeTransportAsync();
            await EnsureTransportAsync(
                showBrowser: false,
                requireAuthorization: true,
                cancellationToken);
        }
    }

    private async Task EnsureInteractiveLoginAsync(CancellationToken cancellationToken)
    {
        await DisposeTransportAsync();
        await EnsureTransportAsync(
            showBrowser: true,
            requireAuthorization: true,
            cancellationToken);
    }

    private async Task EnsureTransportAsync(
        bool showBrowser,
        bool requireAuthorization,
        CancellationToken cancellationToken)
    {
        if (_transport is not null) return;
        var transport = _host.CreateBrowserTransport();
        _transport = transport;
        try
        {
            await transport.InitializeAsync(
                _options(showBrowser, requireAuthorization),
                cancellationToken);
        }
        catch
        {
            if (ReferenceEquals(_transport, transport))
                _transport = null;
            await transport.DisposeAsync();
            throw;
        }
    }

    private async ValueTask DisposeTransportAsync()
    {
        var transport = _transport;
        _transport = null;
        if (transport is not null)
            await transport.DisposeAsync();
    }

    private void ScheduleIdle()
    {
        CancelIdle();
        if (_host.BrowserIdleMinutes == 0 || _disposed) return;
        var cancellation = new CancellationTokenSource();
        _idleCancellation = cancellation;
        _idleTask = DisposeWhenIdleAsync(cancellation);
    }

    private async Task DisposeWhenIdleAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(
                TimeSpan.FromMinutes(_host.BrowserIdleMinutes),
                cancellation.Token);
            await _gate.WaitAsync(cancellation.Token);
            try
            {
                if (ReferenceEquals(_idleCancellation, cancellation))
                    await DisposeTransportAsync();
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
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
        if (_disposed)
            throw new ObjectDisposedException(nameof(BrowserAccountSession));
    }
}
