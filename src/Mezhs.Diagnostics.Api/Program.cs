using Mezhs;
using Mezhs.Configuration;
using Mezhs.Diagnostics;

var configPath = ConfigPath.Find(args, "mezhs.yaml");
var options = MezhsConfigLoader.Load(configPath);

if (!Uri.TryCreate(options.Server.Listen, UriKind.Absolute, out var listen) ||
    !listen.IsLoopback)
    throw new InvalidOperationException(
        "Diagnostics API must listen on a loopback address.");

var builder = WebApplication.CreateBuilder(args);
builder.AddMezhsApi(options);
builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy =>
    policy.SetIsOriginAllowed(origin =>
            Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback)
        .AllowAnyHeader()
        .AllowAnyMethod()));

var app = builder.Build();
app.UseMezhsApi();
app.UseCors();

app.MapGet("/", () => Results.Ok(new
{
    name = "MEŽS Integration Diagnostics",
    version = 1,
    note = "Do not run a second host against the same connection profile.",
    endpoints = new[]
    {
        "/v1/connections",
        "/v1/connections/{connectionId}/login",
        "/v1/connections/{connectionId}/browser",
        "/v1/diagnostics/connections/{connectionId}/browser/{operation}"
    }
}));
app.MapMezhsApi();
app.MapMezhsDiagnostics();

Console.WriteLine($"MEŽS Diagnostics config: {configPath}");
Console.WriteLine($"MEŽS Diagnostics listening: {options.Server.Listen}");
await app.RunAsync();
