using System.Net.Http.Headers;

var frontendPath = FindFrontendPath();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = frontendPath
});

var listenUrls = RequireLoopbackUrls(
    builder.Configuration["urls"] ?? builder.Configuration["Agent:Listen"] ?? "http://127.0.0.1:5174",
    "Agent Web listen URL");
var agentApiBaseUrl = RequireLoopbackUri(
    builder.Configuration["Agent:BaseUrl"] ?? "http://127.0.0.1:5060",
    "Agent:BaseUrl");
builder.WebHost.UseUrls(listenUrls);

builder.Services.AddHttpClient("agent-api", client =>
{
    client.BaseAddress = agentApiBaseUrl;
    client.Timeout = Timeout.InfiniteTimeSpan;
});

var app = builder.Build();

MapApiProxy(app, "/health");
MapApiProxy(app, "/v1/{**path}");

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

static void MapApiProxy(WebApplication app, string pattern)
{
    app.Map(pattern, async context =>
    {
        var client = context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient("agent-api");
        using var request = new HttpRequestMessage(
            new HttpMethod(context.Request.Method),
            context.Request.Path + context.Request.QueryString);

        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            request.Content = new StreamContent(context.Request.Body);
            if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(context.Request.ContentType);
        }

        if (context.Request.Headers.TryGetValue("Accept", out var accept))
            request.Headers.TryAddWithoutValidation("Accept", accept.ToArray());

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted);

            context.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is not null)
                context.Response.ContentType = response.Content.Headers.ContentType.ToString();
            if (response.Content.Headers.ContentDisposition is not null)
                context.Response.Headers.ContentDisposition = response.Content.Headers.ContentDisposition.ToString();

            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The browser closed or replaced the request. This is not an Agent API failure.
        }
        catch (IOException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Some server implementations surface a disconnected downstream client as IO.
        }
        catch (HttpRequestException)
        {
            await HandleProxyTransportFailure(context);
        }
        catch (IOException)
        {
            await HandleProxyTransportFailure(context);
        }
    });
}

static async Task HandleProxyTransportFailure(HttpContext context)
{
    if (context.RequestAborted.IsCancellationRequested)
        return;

    if (context.Response.HasStarted)
    {
        context.Abort();
        return;
    }

    context.Response.Clear();
    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
    await context.Response.WriteAsJsonAsync(
        new { error = "MEŽS Agent API response was interrupted." });
}

static string[] RequireLoopbackUrls(string value, string setting)
{
    var urls = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (urls.Length == 0)
        throw new InvalidOperationException($"{setting} must contain at least one URL.");
    foreach (var url in urls)
        _ = RequireLoopbackUri(url, setting);
    return urls;
}

static Uri RequireLoopbackUri(string value, string setting)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        throw new InvalidOperationException($"{setting} must be an absolute HTTP or HTTPS URL.");
    if (!uri.IsLoopback)
        throw new InvalidOperationException($"{setting} must use a loopback address.");
    return uri;
}

static string FindFrontendPath()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        var candidate = Path.Combine(directory.FullName, "dist");
        if (File.Exists(Path.Combine(candidate, "index.html")))
            return candidate;
        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException(
        "The Mezhs.Agent.Web frontend build was not found. Build the project before starting it.");
}
