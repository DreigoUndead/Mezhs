namespace Mezhs.Integrations.Mock;

// Explicit deterministic Agent protocol fixture; the normal mock remains an echo.
[Integration("mock-agent-scripted")]
public sealed class MockAgentScriptedIntegration(IntegrationConnection connection, IIntegrationHost host)
    : MockIntegration(connection, host)
{
    public override Task<IntegrationSendResult> SendMessageAsync(
        IntegrationSendContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string marker = "\nTask:\n";
        var prompt = context.Message.Content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var index = prompt.LastIndexOf(marker, StringComparison.Ordinal);
        var reply = index >= 0 ? prompt[(index + marker.Length)..].Trim() : "<DONE>";
        return Task.FromResult(new IntegrationSendResult(reply, Model: "mock-scripted"));
    }
}
