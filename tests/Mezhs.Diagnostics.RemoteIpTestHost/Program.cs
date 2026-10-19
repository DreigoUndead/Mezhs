using System.Net;
using Mezhs;
using Mezhs.Configuration;

var options = MezhsConfigLoader.Load(args[1]);
var builder = WebApplication.CreateBuilder(args);
builder.AddMezhsApi(options);
var app = builder.Build();
app.UseMezhsApi();

// Exercise the actual shared route with a synthetic non-loopback peer address.
// This avoids relying on the developer machine's inbound firewall settings.
app.Use((context, next) =>
{
    context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.123");
    return next();
});
app.MapMezhsApi();
await app.RunAsync();
