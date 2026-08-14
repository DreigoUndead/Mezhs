using System.Reflection;
using Mezhs.Integrations.Browser;

namespace Mezhs.Integrations.Gemini;

public sealed class GeminiWebIntegration(
    IntegrationConnection connection,
    IBrowserIntegrationHost host) : BrowserIntegrationBase(connection, host)
{
    public override string Name => "Gemini Web";
    protected override Assembly BrowserModuleAssembly => typeof(GeminiWebIntegration).Assembly;
    protected override string BrowserModuleResourceName => "Mezhs.Integrations.Gemini.BrowserModule";

    public override Task<IntegrationSendResult> SendMessageAsync(
        IntegrationSendContext context,
        CancellationToken cancellationToken = default) =>
        SendAnonymousAsync(context, cancellationToken);
}

public sealed class GeminiIntegrationFactory() : IntegrationFactory("gemini-web")
{
    public override void Validate(IntegrationConnection connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.GetSetting("workspace")))
            throw new InvalidOperationException(
                $"workspace is not supported by connection '{connection.Id}'.");
    }

    public override IChatIntegration Create(
        IntegrationConnection connection,
        IIntegrationHost host) =>
        new GeminiWebIntegration(connection, BrowserIntegrationHost.Require(host));
}
