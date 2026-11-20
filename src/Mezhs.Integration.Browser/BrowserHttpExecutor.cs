using Mezhs.Browser;

namespace Mezhs.Integrations.Browser;

/// <summary>
/// Binds typed HTTP operations to an existing Electron browser operation/context.
/// It never creates a new browser host, login session or HTTP credential store.
/// </summary>
public sealed class BrowserHttpExecutor(IChatBrowserTransport transport)
{
    private readonly IntegrationHttpClient _http = new(async (request, report, token) =>
        await transport.InvokeWithProgressAsync<HttpTransportResponse>(
            "typedHttp", request,
            progress => report?.Invoke(new HttpTransferProgress(progress.State, progress.Detail)),
            token));

    public Task<TResponse> CallAsync<TRequest, TResponse>(
        HttpEndpoint<TRequest, TResponse> endpoint,
        TRequest? request,
        Action<BrowserOperationProgress>? reportProgress = null,
        CancellationToken cancellationToken = default) =>
        _http.CallAsync(endpoint, request,
            progress => reportProgress?.Invoke(new BrowserOperationProgress(
                progress.State, progress.Detail)), cancellationToken);
}
