using Mezhs.Browser;
using Mezhs.Browser.Electron;
using Mezhs.Configuration;
using Mezhs.Integrations.Browser;

namespace Mezhs.Services;

public sealed class IntegrationHost(
    MezhsOptions options,
    ChatStore store) : IBrowserIntegrationHost
{
    public int BrowserIdleMinutes => options.Transport.IdleMinutes;

    public string GetConnectionRoot(string connectionId) =>
        store.GetConnectionRoot(connectionId);

    public IChatBrowserTransport CreateBrowserTransport() =>
        new ElectronBrowserTransport(options.Transport.ElectronDirectory);
}
