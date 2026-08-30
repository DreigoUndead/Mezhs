namespace Mezhs.Integrations.Mock;

[Integration("mock")]
public class MockIntegration : ChatIntegrationBase
{
    private static readonly IModelModule ModelModule = new DeterministicModelModule();

    public MockIntegration(IntegrationConnection connection, IIntegrationHost host) : base(connection)
    {
        _ = host;
        Validate(connection);
    }

    public override IntegrationCapabilities Capabilities => new(
        FileInput: true,
        ImageInput: true,
        FileOutput: true,
        ImageOutput: true);
    public override IModelModule Models => ModelModule;

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
        return new IntegrationSendResult(
            $"Echo: {context.Message.Content}",
            Files: outputFiles,
            Model: "mock-served");
    }

    protected static void Validate(IntegrationConnection connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.GetSetting("workspace")))
            throw new InvalidOperationException(
                $"workspace is not supported by connection '{connection.Id}'.");
    }

    private sealed class DeterministicModelModule : IModelModule
    {
        private static readonly IReadOnlyList<IntegrationModel> Models =
        [
            new("mock-fast", "Mock Fast"),
            new("mock-deep", "Mock Deep")
        ];

        public Task<IReadOnlyList<IntegrationModel>> GetModelsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Models);
    }
}

[Integration("mock-login")]
public sealed class MockLoginIntegration : MockIntegration
{
    private static readonly ILoginModule LoginModule = new NoOpLoginModule();

    public MockLoginIntegration(IntegrationConnection connection, IIntegrationHost host)
        : base(connection, host)
    {
    }

    public override ILoginModule Login => LoginModule;

    private sealed class NoOpLoginModule : ILoginModule
    {
        public Task LoginAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
