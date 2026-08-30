var frontendPath = FindFrontendPath();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = frontendPath
});

var app = builder.Build();

app.MapGet("/app-config", (IConfiguration configuration) => Results.Ok(new
{
    agentApiBaseUrl = configuration["Agent:BaseUrl"] ?? "http://127.0.0.1:5060"
}));

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
