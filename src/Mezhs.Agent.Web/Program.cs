using System.Net.Http.Headers;

var frontendPath = FindFrontendPath();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = frontendPath
});

var agentApiBaseUrl = builder.Configuration["Agent:BaseUrl"] ?? "http://127.0.0.1:5060";
builder.Services.AddHttpClient("agent-api", client =>
    client.BaseAddress = new Uri(agentApiBaseUrl));

var app = builder.Build();

app.Map("/v1/{**path}", async context =>
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

    using var response = await client.SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead,
        context.RequestAborted);

    context.Response.StatusCode = (int)response.StatusCode;
    if (response.Content.Headers.ContentType is not null)
        context.Response.ContentType = response.Content.Headers.ContentType.ToString();
    await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

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
