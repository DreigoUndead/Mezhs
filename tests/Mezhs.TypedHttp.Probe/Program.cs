using Mezhs.Browser;
using Mezhs.Browser.Electron;
using Mezhs.Integrations;
using Mezhs.Integrations.Browser;
using Mezhs.Integrations.ChatGpt;

// Interactive login is opt-in and intended only for a separate test profile.
// Never point --interactive at the active MEZS connection profile.
var interactive = args.Contains("--interactive", StringComparer.Ordinal);
var positional = args.Where(arg => arg != "--interactive").ToArray();
if (positional.Length is < 1 or > 2 ||
    args.Length != positional.Length + (interactive ? 1 : 0))
{
    Console.Error.WriteLine(
        "Usage: dotnet run --project tests/Mezhs.TypedHttp.Probe -- " +
        "<connection-root> [electron-directory] [--interactive]");
    Console.Error.WriteLine(
        "--interactive opens a login window for a SEPARATE test profile. " +
        "Do not point it at an active MEZS connection root.");
    return 2;
}

var connectionRoot = Path.GetFullPath(positional[0]);
var electronDirectory = Path.GetFullPath(positional.Length == 2
    ? positional[1] : Path.Combine(Environment.CurrentDirectory, "electron"));
var profilePath = Path.Combine(connectionRoot, "profile");
if (!Directory.Exists(profilePath))
{
    if (!interactive)
    {
        Console.Error.WriteLine(
            "Existing account profile directory was not found. " +
            "Use --interactive only with a separate test profile for a user-driven login.");
        return 2;
    }
    Directory.CreateDirectory(profilePath);
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
    if (interactive)
    {
        Console.WriteLine(
            "INTERACTIVE_LOGIN: Complete sign-in in the separate ChatGPT browser window. " +
            "Only the isolated profile will be used.");
        // An unattended live probe must not wait indefinitely for someone to sign in.
        cancelled.CancelAfter(TimeSpan.FromMinutes(5));
        await (integration.Login?.LoginAsync(cancelled.Token)
            ?? throw new InvalidOperationException("Account login module is unavailable."));
        cancelled.CancelAfter(Timeout.InfiniteTimeSpan);
        Console.WriteLine("INTERACTIVE_LOGIN=PASS");
    }

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
    Console.Error.WriteLine("CANCELLED: isolated account login or local typed HTTP probe aborted.");
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
