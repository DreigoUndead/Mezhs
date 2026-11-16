namespace Mezhs.Integrations.Mock;

// Explicit deterministic Agent protocol fixture; the normal mock remains an echo.
[Integration("mock-agent-scripted")]
public sealed class MockAgentScriptedIntegration(IntegrationConnection connection, IIntegrationHost host)
    : MockIntegration(connection, host)
{
    public override Task<PromptResult> PromptAsync(
        ChatContext chat, PromptRequest request, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<PromptResult>(cancellationToken);
        const string marker = "\nTask:\n";
        var prompt = request.Text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var index = prompt.LastIndexOf(marker, StringComparison.Ordinal);
        var reply = index >= 0 ? prompt[(index + marker.Length)..].Trim() : "<DONE>";
        return Task.FromResult(new PromptResult(reply, CreateMessageId(), "mock-scripted"));
    }
}
