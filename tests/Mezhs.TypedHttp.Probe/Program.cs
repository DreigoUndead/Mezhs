using Mezhs.Browser;
using Mezhs.Browser.Electron;
using Mezhs.Integrations;
using Mezhs.Integrations.Browser;
using Mezhs.Integrations.ChatGpt;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: dotnet run --project tests/Mezhs.TypedHttp.Probe -- <existing-connection-root> [electron-directory]");
    return 2;
}

var connectionRoot = Path.GetFullPath(args[0]);
var electronDirectory = Path.GetFullPath(args.Length == 2
    ? args[1] : Path.Combine(Environment.CurrentDirectory, "electron"));
if (!Directory.Exists(Path.Combine(connectionRoot, "profile")))
{
    Console.Error.WriteLine("Existing account profile directory was not found. Probe does not initiate a new login.");
    return 2;
}

using var cancelled = new CancellationTokenSource();
Console.CancelKeyPress += (_, ev) =>
{
    ev.Cancel = true;
    cancelled.Cancel();
};
var connection = new IntegrationConnection(
    "typed-http-probe", "Typed HTTP Probe", "chatgpt-web-account",
    new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
var host = new ProbeBrowserHost(connectionRoot, electronDirectory);
await using var integration = new ChatGptAccountIntegration(connection, host);
try
{
    var catalog = await integration.ProbeTypedHttpAsync(
        progress => Console.WriteLine("activity=" + progress.State + " " + progress.Detail),
        cancelled.Token);
    var count = (catalog.Versions?.Count ?? 0) + (catalog.Models?.Count ?? 0);
    if (count == 0)
        throw new InvalidOperationException("Authenticated typed models response contains no catalog entries.");
    Console.WriteLine("PASS: authenticated typed Electron HTTP model catalog, entries=" + count);
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("CANCELLED: local typed HTTP operation aborted.");
    return 3;
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL: " + error);
    return 1;
}

sealed class ProbeBrowserHost(string connectionRoot, string electronDirectory) : IBrowserIntegrationHost
{
    public int BrowserIdleMinutes => 0;
    public string GetConnectionRoot(string connectionId) => connectionRoot;
    public IChatBrowserTransport CreateBrowserTransport() =>
        new ElectronBrowserTransport(electronDirectory);
}
