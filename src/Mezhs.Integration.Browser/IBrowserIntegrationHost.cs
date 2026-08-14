using Mezhs.Browser;

namespace Mezhs.Integrations.Browser;

public interface IBrowserIntegrationHost : IIntegrationHost
{
    int BrowserIdleMinutes { get; }
    IChatBrowserTransport CreateBrowserTransport();
}

public static class BrowserIntegrationHost
{
    public static IBrowserIntegrationHost Require(IIntegrationHost host) =>
        host as IBrowserIntegrationHost
        ?? throw new InvalidOperationException(
            "This integration requires a browser-capable MEŽS host.");
}
