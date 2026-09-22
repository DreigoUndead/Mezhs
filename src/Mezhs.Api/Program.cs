using Mezhs;
using Mezhs.Configuration;

var configPath = ConfigPath.Find(args, "mezhs.yaml");
var options = MezhsConfigLoader.Load(configPath);

var builder = WebApplication.CreateBuilder(args);
builder.AddMezhsApi(options);
builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseMezhsApi();
app.UseCors();

app.MapGet("/", () => Results.Ok(new
{
    name = "MEŽS",
    version = 1,
    endpoints = new[] { "/v1/connections", "/v1/messages", "/v1/chats", "/v1/categories", "/v1/files" }
}));
app.MapMezhsApi();

Console.WriteLine($"MEŽS config: {configPath}");
Console.WriteLine($"MEŽS listening: {options.Server.Listen}");
await app.RunAsync();
