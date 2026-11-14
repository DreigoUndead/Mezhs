using System.Text.Json;

namespace Mezhs.Integrations.Mock;

[Integration("mock")]
public class MockIntegration : IntegrationBase
{
    public MockIntegration(IntegrationConnection connection, IIntegrationHost host)
        : base(connection, host) => Validate(connection);

    public override bool AllowFiles => true;

    public override Task<IReadOnlyList<IntegrationModel>> GetModelsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<IntegrationModel>>([
            new("default", "Default"),
            new("mock-fast", "Mock Fast"),
            new("mock-deep", "Mock Deep")
        ]);

    public override async Task<PromptResult> PromptAsync(
        ChatContext chat,
        PromptRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Text.Contains("MEZHS_TEST_INTEGRATION_FAILURE", StringComparison.Ordinal))
            throw new InvalidOperationException("Intentional mock integration failure.");
        await Task.Delay(25, cancellationToken);
        var outputFiles = request.Files.Select(file => new IntegrationOutputFile(
            file.Path, $"echo-{file.Name}", file.ContentType, DeleteAfterImport: false)).ToArray();
        return new PromptResult(
            $"Echo: {request.Text}", CreateMessageId(), "mock-served", Files: outputFiles);
    }

    protected override Task SetModelAsync(
        ChatContext chat, string model, CancellationToken cancellationToken = default) =>
        model is "mock-fast" or "mock-deep" or "default"
            ? Task.CompletedTask
            : base.SetModelAsync(chat, model, cancellationToken);

    protected static void Validate(IntegrationConnection connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.GetSetting("workspace")))
            throw new InvalidOperationException(
                $"workspace is not supported by connection '{connection.Id}'.");
    }
}

[Integration("mock-login")]
public sealed class MockLoginIntegration : MockIntegration
{
    public MockLoginIntegration(IntegrationConnection connection, IIntegrationHost host)
        : base(connection, host) { }

    public override Task LoginAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

[Integration("mock-diagnostics")]
public sealed class MockDiagnosticsIntegration : MockIntegration
{
    public MockDiagnosticsIntegration(IntegrationConnection connection, IIntegrationHost host)
        : base(connection, host) { }

    public override Task<JsonElement> DiagnoseAsync(
        string operation, JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (operation != "echo")
            throw new InvalidOperationException(
                $"Browser diagnostic operation '{operation}' is not supported.");
        return Task.FromResult(JsonSerializer.SerializeToElement(new { operation, arguments }));
    }
}
