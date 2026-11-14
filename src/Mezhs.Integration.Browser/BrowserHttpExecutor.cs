using System.Net.Http;
using Mezhs.Browser;

namespace Mezhs.Integrations.Browser;

/// <summary>
/// Minimal typed-endpoint feasibility bridge. Do not migrate provider send paths
/// until authenticated Electron transport checks establish its behavior.
/// </summary>
public sealed record HttpEndpoint<TRequest, TResponse>(HttpMethod Method, string Path);

public sealed class BrowserHttpExecutor(IChatBrowserTransport transport)
{
    public Task<TResponse> CallAsync<TRequest, TResponse>(
        HttpEndpoint<TRequest, TResponse> endpoint,
        TRequest request,
        Action<BrowserOperationProgress>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (string.IsNullOrWhiteSpace(endpoint.Path) ||
            !endpoint.Path.StartsWith("/", StringComparison.Ordinal) ||
            endpoint.Path.StartsWith("//", StringComparison.Ordinal) ||
            endpoint.Path.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("Typed HTTP path must be origin-relative.", nameof(endpoint));

        // The provider operation owns HTTP authorization, cookies and integrity
        // observations. Credentials never cross into this typed descriptor.
        return transport.InvokeWithProgressAsync<TResponse>(
            "typedHttp",
            new { method = endpoint.Method.Method, path = endpoint.Path, body = request },
            reportProgress,
            cancellationToken);
    }
}
