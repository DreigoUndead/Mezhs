using Mezhs.Browser;

namespace Mezhs.Integrations.Browser;

/// <summary>
/// Binds typed HTTP operations to an existing Electron browser operation/context.
/// It never creates a new browser host, login session or HTTP credential store.
/// </summary>
public sealed class BrowserHttpExecutor(IChatBrowserTransport transport)
{
    private readonly IntegrationHttpClient _http = new(
        async (request, report, token) =>
            await transport.InvokeWithProgressAsync<HttpTransportResponse>(
                "typedHttp", request,
                progress => report?.Invoke(new HttpTransferProgress(progress.State, progress.Detail)),
                token),
        async (request, onEvent, report, token) =>
            await transport.InvokeWithEventsAsync<HttpTransportResponse>(
                "typedHttp", request,
                value =>
                {
                    if (value.ValueKind != System.Text.Json.JsonValueKind.String)
                        throw new InvalidOperationException("Browser stream event was not a string.");
                    onEvent(value.GetString() ?? "");
                },
                progress => report?.Invoke(new HttpTransferProgress(progress.State, progress.Detail)),
                token));

    public Task<BrowserBinaryTransferResult> TransferAsync(
        BrowserBinaryTransferRequest request,
        Action<BrowserOperationProgress>? reportProgress = null,
        CancellationToken cancellationToken = default) =>
        transport.InvokeWithProgressAsync<BrowserBinaryTransferResult>(
            "binaryTransfer", request, reportProgress, cancellationToken);

    public Task<HttpTransportResponse> CallStreamAsync<TRequest, TResponse>(
        HttpEndpoint<TRequest, TResponse> endpoint,
        TRequest? request,
        Action<string> onEvent,
        string? nativeHook = null,
        object? nativeArgs = null,
        string? turnMessageId = null,
        Action<BrowserOperationProgress>? reportProgress = null,
        CancellationToken cancellationToken = default) =>
        _http.CallStreamAsync(endpoint, request, onEvent,
            nativeHook, nativeArgs, turnMessageId,
            progress => reportProgress?.Invoke(new BrowserOperationProgress(
                progress.State, progress.Detail)), cancellationToken);

    public Task<TResponse> CallAsync<TRequest, TResponse>(
        HttpEndpoint<TRequest, TResponse> endpoint,
        TRequest? request,
        Action<BrowserOperationProgress>? reportProgress = null,
        CancellationToken cancellationToken = default) =>
        _http.CallAsync(endpoint, request,
            progress => reportProgress?.Invoke(new BrowserOperationProgress(
                progress.State, progress.Detail)), cancellationToken);
}

public sealed record BrowserBinaryTransferRequest(
    string Action, string Url, string? LocalPath = null, string? Name = null,
    string? ContentType = null, IReadOnlyDictionary<string, string>? Headers = null);

public sealed record BrowserBinaryTransferResult(
    long Size, int StatusCode, string? LocalPath = null, string? ContentType = null);
