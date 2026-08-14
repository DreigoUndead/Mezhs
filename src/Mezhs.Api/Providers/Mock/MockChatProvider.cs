using Mezhs.Configuration;
using Mezhs.Services;

namespace Mezhs.Providers.Mock;

public sealed class MockChatProvider(ConnectionOptions connection) : ChatProviderBase(connection)
{
    public override string Name => "Mock";
    public override ProviderCapabilities Capabilities => new(
        FileInput: true,
        ImageInput: true,
        FileOutput: true,
        ImageOutput: true);

    public override async Task<ProviderSendResult> SendMessageAsync(
        ProviderSendContext context,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(25, cancellationToken);
        var outputFiles = context.Files.Select(file => new ProviderOutputFile(
            file.Path,
            $"echo-{file.Name}",
            file.ContentType,
            DeleteAfterImport: false)).ToArray();
        return new ProviderSendResult($"Echo: {context.Message.Content}", Files: outputFiles);
    }

    public sealed class Factory : ChatProviderFactory
    {
        public Factory() : base("mock") { }

        public override void Validate(ConnectionOptions connection)
        {
            if (!string.IsNullOrWhiteSpace(connection.Workspace))
                throw new InvalidOperationException(
                    $"workspace is not supported by connection '{connection.Id}'.");
        }

        public override IChatProvider Create(
            ConnectionOptions connection,
            MezhsOptions options,
            ChatStore store) => new MockChatProvider(connection);
    }
}
