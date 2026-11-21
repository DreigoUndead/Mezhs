using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Mezhs.Integrations;

public enum HttpResultFormat { Json, Text, ServerSentEvents, Ndjson }

// Endpoint metadata is independent of the HTTP transport and any browser session.
public sealed record HttpEndpoint<TRequest, TResponse>(
    HttpMethod Method,
    string Path,
    HttpResultFormat Format = HttpResultFormat.Json,
    Func<string, TResponse>? Interpret = null,
    string? Accept = null,
    string? NativeHook = null,
    object? NativeArgs = null);

public sealed record HttpTransportRequest(
    string Method, string Path, string? Body, string? Accept,
    bool Stream = false, string? NativeHook = null, object? NativeArgs = null,
    string? TurnMessageId = null);

public sealed record HttpTransportResponse(
    int StatusCode, string Body, string? ContentType = null,
    string? ProviderCode = null, int? RetryAfterMs = null);

public sealed class IntegrationHttpException(
    string method, string path, int statusCode, string? providerCode, int? retryAfterMs)
    : HttpRequestException($"HTTP {method} {path} failed with status {statusCode}.",
        null, (HttpStatusCode)statusCode)
{
    public string? ProviderCode { get; } = providerCode;
    public int? RetryAfterMs { get; } = retryAfterMs;
}


public sealed record HttpTransferProgress(string State, string? Detail = null);

/// <summary>
/// Transport-neutral typed HTTP mechanics. Does not own browser authentication,
/// provider session state, retries or the meaning of provider events.
/// </summary>
public sealed class IntegrationHttpClient(
    Func<HttpTransportRequest, Action<HttpTransferProgress>?, CancellationToken,
        Task<HttpTransportResponse>> execute,
    Func<HttpTransportRequest, Action<string>, Action<HttpTransferProgress>?, CancellationToken,
        Task<HttpTransportResponse>>? executeStream = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public const int MaxResponseBytes = 16 * 1024 * 1024;

    public Task<TResponse> GetAsync<TResponse>(
        string path, CancellationToken cancellationToken = default) =>
        CallAsync(new HttpEndpoint<object, TResponse>(HttpMethod.Get, path),
            request: null, cancellationToken: cancellationToken);

    public Task<TResponse> PostAsync<TRequest, TResponse>(
        string path, TRequest request, CancellationToken cancellationToken = default) =>
        CallAsync(new HttpEndpoint<TRequest, TResponse>(HttpMethod.Post, path),
            request, cancellationToken: cancellationToken);

    public Task<TResponse> PatchAsync<TRequest, TResponse>(
        string path, TRequest request, CancellationToken cancellationToken = default) =>
        CallAsync(new HttpEndpoint<TRequest, TResponse>(HttpMethod.Patch, path),
            request, cancellationToken: cancellationToken);

    public async Task<TResponse> CallAsync<TRequest, TResponse>(
        HttpEndpoint<TRequest, TResponse> endpoint,
        TRequest? request,
        Action<HttpTransferProgress>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ValidatePath(endpoint.Path);
        var method = endpoint.Method.Method.ToUpperInvariant();
        if (method is not ("GET" or "POST" or "PATCH"))
            throw new NotSupportedException($"HTTP method '{method}' is not supported.");
        if (method == "GET" && request is not null)
            throw new ArgumentException("GET endpoints do not accept request bodies.", nameof(request));
        if (endpoint.Accept is not null && (endpoint.Accept.Contains('\r') || endpoint.Accept.Contains('\n')))
            throw new ArgumentException("Accept must not contain line breaks.", nameof(endpoint));
        var body = method == "GET" || request is null
            ? null : JsonSerializer.Serialize(request, JsonOptions);
        cancellationToken.ThrowIfCancellationRequested();

        // Exactly one transport invocation. A failed or ambiguous mutation is never replayed.
        var response = await execute(new HttpTransportRequest(method, endpoint.Path, body, endpoint.Accept,
                NativeHook: endpoint.NativeHook, NativeArgs: endpoint.NativeArgs),
            reportProgress, cancellationToken);
        if (response.StatusCode is < 200 or >= 300)
            throw new IntegrationHttpException(method, endpoint.Path, response.StatusCode,
                response.ProviderCode, response.RetryAfterMs);
        if (Encoding.UTF8.GetByteCount(response.Body) > MaxResponseBytes)
            throw new InvalidOperationException("HTTP response exceeded the 16 MiB limit.");

        if (endpoint.Interpret is not null)
            return endpoint.Interpret(response.Body);
        if (endpoint.Format != HttpResultFormat.Json)
        {
            if (typeof(TResponse) == typeof(string))
                return (TResponse)(object)response.Body;
            throw new InvalidOperationException(
                "Non-JSON endpoints require a typed response interpreter or a string response.");
        }
        return JsonSerializer.Deserialize<TResponse>(response.Body, JsonOptions)
            ?? throw new InvalidOperationException("HTTP response was empty or could not be decoded.");
    }

    public async Task<HttpTransportResponse> CallStreamAsync<TRequest, TResponse>(
        HttpEndpoint<TRequest, TResponse> endpoint,
        TRequest? request,
        Action<string> onEvent,
        string? nativeHook = null,
        object? nativeArgs = null,
        string? turnMessageId = null,
        Action<HttpTransferProgress>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(onEvent);
        ValidatePath(endpoint.Path);
        if (endpoint.Format is not (HttpResultFormat.ServerSentEvents or HttpResultFormat.Ndjson))
            throw new ArgumentException("Streaming requires an SSE or NDJSON endpoint.", nameof(endpoint));
        var method = endpoint.Method.Method.ToUpperInvariant();
        if (method is not ("GET" or "POST" or "PATCH"))
            throw new NotSupportedException($"HTTP method '{method}' is not supported.");
        if (method == "GET" && request is not null)
            throw new ArgumentException("GET endpoints do not accept request bodies.", nameof(request));
        if (executeStream is null)
            throw new NotSupportedException("This HTTP transport does not support live stream events.");
        var accept = endpoint.Format == HttpResultFormat.ServerSentEvents
            ? "text/event-stream" : "application/x-ndjson";
        if (endpoint.Accept is not null && endpoint.Accept != accept)
            throw new ArgumentException("Streaming accept type does not match the endpoint format.", nameof(endpoint));
        var body = method == "GET" || request is null
            ? null : JsonSerializer.Serialize(request, JsonOptions);
        cancellationToken.ThrowIfCancellationRequested();
        // This is exactly one dispatch: a possibly delivered mutation is never replayed.
        var result = await executeStream(
            new HttpTransportRequest(method, endpoint.Path, body, accept,
                Stream: true, NativeHook: nativeHook, NativeArgs: nativeArgs,
                TurnMessageId: turnMessageId),
            onEvent, reportProgress, cancellationToken);
        if (result.StatusCode is < 200 or >= 300)
            throw new IntegrationHttpException(method, endpoint.Path, result.StatusCode,
                result.ProviderCode, result.RetryAfterMs);
        return result;
    }

    public static IntegrationHttpClient ForHttpClient(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return new IntegrationHttpClient(async (request, progress, cancellationToken) =>
        {
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Path);
            if (request.Body is not null)
                message.Content = new StringContent(request.Body, Encoding.UTF8, "application/json");
            if (request.Accept is not null)
                message.Headers.TryAddWithoutValidation("Accept", request.Accept);
            using var response = await client.SendAsync(
                message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int count;
            while ((count = await contentStream.ReadAsync(chunk, cancellationToken)) != 0)
            {
                if (buffer.Length + count > MaxResponseBytes)
                    throw new InvalidOperationException("HTTP response exceeded the 16 MiB limit.");
                buffer.Write(chunk, 0, count);
                progress?.Invoke(new HttpTransferProgress("retrieving",
                    $"Received {buffer.Length} bytes."));
            }
            return new HttpTransportResponse((int)response.StatusCode,
                Encoding.UTF8.GetString(buffer.ToArray()),
                response.Content.Headers.ContentType?.MediaType);
        });
    }

    public static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !path.StartsWith("/", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal) ||
            path.Contains("://", StringComparison.Ordinal) ||
            path.Contains('\\') ||
            path.Contains('\r') || path.Contains('\n') ||
            path.Contains('#'))
            throw new ArgumentException("HTTP path must be origin-relative and cannot contain fragments.", nameof(path));
    }
}

/// <summary>Parse SSE data blocks and NDJSON records without assuming provider event meaning.</summary>
public static class HttpStreamData
{
    public static IReadOnlyList<string> Sse(string text)
    {
        var events = new List<string>();
        var data = new List<string>();
        using var reader = new StringReader(text);
        string? line;
        void Complete()
        {
            if (data.Count == 0) return;
            var joined = string.Join("\n", data);
            if (joined != "[DONE]") events.Add(joined);
            data.Clear();
        }
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) { Complete(); continue; }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line[5..];
                data.Add(value.StartsWith(' ') ? value[1..] : value);
            }
        }
        Complete();
        return events;
    }

    public static IReadOnlyList<string> Ndjson(string text) =>
        text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
}
