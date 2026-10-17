using System.Collections.Concurrent;

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
    private readonly ConcurrentDictionary<string, string> _uploadPaths = new();
    public override IFileUploadModule Files => new MockUpload(_uploadPaths);

    private sealed class MockUpload(ConcurrentDictionary<string, string> paths) : IFileUploadModule
    {
        public Task<IntegrationUploadedFile> UploadFileAsync(string path, CancellationToken cancellationToken = default)
        {
            var file = new FileInfo(path);
            if (!file.Exists) throw new FileNotFoundException("File not found.", path);
            var id = "mock-" + Guid.NewGuid().ToString("N");
            paths[id] = path;
            var contentType = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".txt" => "text/plain",
                ".png" => "image/png",
                ".pdf" => "application/pdf",
                _ => "application/octet-stream"
            };
            return Task.FromResult(new IntegrationUploadedFile(id, file.Name, contentType, file.Length));
        }
    }

    public override async Task<IntegrationSendResult> SendMessageAsync(
        IntegrationSendContext context,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(25, cancellationToken);
        var outputFiles = context.Files
            .Where(file => _uploadPaths.TryGetValue(file.FileId, out var path) && File.Exists(path))
            .Select(file => new IntegrationOutputFile(
                _uploadPaths[file.FileId], $"echo-{file.Name}", file.ContentType, DeleteAfterImport: false))
            .ToArray();
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

[Integration("mock-text-only")]
public sealed class MockTextOnlyIntegration : MockIntegration
{
    public MockTextOnlyIntegration(IntegrationConnection connection, IIntegrationHost host)
        : base(connection, host) { }

    public override IntegrationCapabilities Capabilities => new(
        FileInput: true, ImageInput: false, FileOutput: true, ImageOutput: false);
}

[Integration("mock-no-files")]
public sealed class MockNoFilesIntegration : MockIntegration
{
    public MockNoFilesIntegration(IntegrationConnection connection, IIntegrationHost host)
        : base(connection, host) { }

    public override IntegrationCapabilities Capabilities => new(
        FileInput: false, ImageInput: false, FileOutput: false, ImageOutput: false);
}
