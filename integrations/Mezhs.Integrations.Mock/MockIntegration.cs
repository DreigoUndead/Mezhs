namespace Mezhs.Integrations.Mock;

public class MockIntegration(IntegrationConnection connection) : ChatIntegrationBase(connection)
{
    public override string Name => "Mock";
    public override IntegrationCapabilities Capabilities => new(
        FileInput: true,
        ImageInput: true,
        FileOutput: true,
        ImageOutput: true);

    public override async Task<IntegrationSendResult> SendMessageAsync(
        IntegrationSendContext context,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(25, cancellationToken);
        var outputFiles = context.Files.Select(file => new IntegrationOutputFile(
            file.Path,
            $"echo-{file.Name}",
            file.ContentType,
            DeleteAfterImport: false)).ToArray();
        return new IntegrationSendResult($"Echo: {context.Message.Content}", Files: outputFiles);
    }
}

public sealed class MockLoginIntegration(IntegrationConnection connection) : MockIntegration(connection)
{
    private static readonly ILoginModule LoginModule = new NoOpLoginModule();

    public override string Name => "Mock Login";
    public override ILoginModule Login => LoginModule;

    private sealed class NoOpLoginModule : ILoginModule
    {
        public Task LoginAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}

public sealed class MockIntegrationFactory() : IntegrationFactory("mock", "mock-login")
{
    public override void Validate(IntegrationConnection connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.GetSetting("workspace")))
            throw new InvalidOperationException(
                $"workspace is not supported by connection '{connection.Id}'.");
    }

    public override IChatIntegration Create(
        IntegrationConnection connection,
        IIntegrationHost host) => connection.Type.ToLowerInvariant() switch
        {
            "mock" => new MockIntegration(connection),
            "mock-login" => new MockLoginIntegration(connection),
            _ => throw new InvalidOperationException($"Unsupported mock integration type '{connection.Type}'.")
        };
}
