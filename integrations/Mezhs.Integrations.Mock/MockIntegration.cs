namespace Mezhs.Integrations.Mock;

[Integration("mock")]
public class MockIntegration : ChatIntegrationBase
{
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
