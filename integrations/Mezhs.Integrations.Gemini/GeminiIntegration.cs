using System.Reflection;
using Mezhs.Integrations.Browser;

namespace Mezhs.Integrations.Gemini;

[Integration("gemini-web")]
public sealed class GeminiWebIntegration : BrowserIntegrationBase
{
    public GeminiWebIntegration(
        IntegrationConnection connection,
        IIntegrationHost host) : base(Validate(connection), host)
    {
    }

    protected override Assembly BrowserModuleAssembly => typeof(GeminiWebIntegration).Assembly;
    protected override string BrowserModuleResourceName => "Mezhs.Integrations.Gemini.BrowserModule";

    public override Task<IntegrationSendResult> SendMessageAsync(
        IntegrationSendContext context,
        CancellationToken cancellationToken = default) =>
        SendAnonymousAsync(context, cancellationToken);

    private static IntegrationConnection Validate(IntegrationConnection connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.GetSetting("workspace")))
            throw new InvalidOperationException(
                $"workspace is not supported by connection '{connection.Id}'.");
        return connection;
    }
}
