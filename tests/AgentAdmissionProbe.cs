using System.Reflection;
using Mezhs.Integrations;
using Mezhs.Integrations.Browser;

namespace Mezhs.Integrations.AdmissionProbe;

// Test-only provider. It uses the actual BrowserIntegrationBase admission gates,
// but does not create Electron transports or access a real account.
[Integration("admission-probe")]
public sealed class ProbeIntegration(IntegrationConnection connection, IIntegrationHost host)
    : BrowserIntegrationBase(connection, host)
{
    protected override Assembly BrowserModuleAssembly => typeof(ProbeIntegration).Assembly;
    protected override string BrowserModuleResourceName => "not-used";

    protected override async Task<IntegrationSendResult> SendCoreAsync(
        IntegrationSendContext context, CancellationToken cancellationToken)
    {
        var workspace = Connection.GetSetting("workspace")
            ?? throw new InvalidOperationException("Probe workspace missing.");
        var prefix = Path.Combine(workspace, Connection.Id + "-" + context.Message.MessageId);
        File.WriteAllText(prefix + ".started", "started");
        context.ReportActivity?.Invoke(new IntegrationActivity("waiting", "Provider-side processing"));
        while (!File.Exists(prefix + ".release") && !File.Exists(prefix + ".fail"))
            await Task.Delay(25, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(prefix + ".fail"))
            throw new InvalidOperationException("Deterministic probe provider failure.");
        File.WriteAllText(prefix + ".finished", "finished");
        return new IntegrationSendResult("Probe response completed.");
    }
}
