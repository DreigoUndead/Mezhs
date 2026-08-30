using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mezhs;
using Mezhs.Configuration;
using Mezhs.Models;

namespace Mezhs.Services;

public sealed class FileStore(MezhsOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _root = options.Storage.Root;
    private readonly ConcurrentDictionary<string, StoredFile> _files =
        new(StringComparer.OrdinalIgnoreCase);

    public void Initialize()
    {
        var connectionsRoot = Path.Combine(_root, "connections");
        Directory.CreateDirectory(connectionsRoot);
        foreach (var connectionDirectory in Directory.EnumerateDirectories(connectionsRoot))
        {
            var filesRoot = Path.Combine(connectionDirectory, "files");
            if (!Directory.Exists(filesRoot)) continue;
            foreach (var metadataPath in Directory.EnumerateFiles(
                         filesRoot,
                         "file.json",
                         SearchOption.AllDirectories))
            {
                try
                {
                    var file = JsonSerializer.Deserialize<StoredFile>(
                        File.ReadAllText(metadataPath),
                        JsonOptions);
                    if (file is not null && File.Exists(GetContentPath(file)))
                        _files[file.FileId] = file;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Could not load file metadata '{metadataPath}': {ex.Message}");
                }
            }
        }
    }

    public StoredFile? Get(string fileId) =>
        _files.TryGetValue(fileId, out var file) ? file : null;

    public IReadOnlyList<StoredFile> GetMany(IEnumerable<string>? fileIds)
    {
        var result = new List<StoredFile>();
        foreach (var fileId in (fileIds ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var file = Get(fileId)
                ?? throw new ResourceNotFoundException($"File '{fileId}' was not found.");
            result.Add(file);
        }
        return result;
    }

    public async Task<StoredFile> CreateAsync(
        string connectionId,
        string name,
        string? contentType,
        Stream content,
        FileSource source,
        CancellationToken cancellationToken = default)
    {
        name = Path.GetFileName(name.Trim());
        if (string.IsNullOrWhiteSpace(name))
            name = "file";

        var fileId = ChatStore.NewId("file");
        var directory = GetFileDirectory(connectionId, fileId);
        Directory.CreateDirectory(directory);
        var contentPath = Path.Combine(directory, "content");
        var temporaryContentPath = contentPath + ".tmp";
        await using (var target = new FileStream(
                         temporaryContentPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         81920,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await content.CopyToAsync(target, cancellationToken);
        }
        File.Move(temporaryContentPath, contentPath);

        var file = new StoredFile
        {
            FileId = fileId,
            ConnectionId = connectionId,
            Name = name,
            ContentType = string.IsNullOrWhiteSpace(contentType)
                ? "application/octet-stream"
                : contentType,
            Size = new FileInfo(contentPath).Length,
            Source = source
        };
        SaveMetadata(file);
        _files[file.FileId] = file;
        return file;
    }

    public async Task<StoredFile> ImportAsync(
        string connectionId,
        string path,
        string name,
        string? contentType,
        FileSource source,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await CreateAsync(
            connectionId,
            name,
            contentType,
            stream,
            source,
            cancellationToken);
    }

    public string GetContentPath(StoredFile file) =>
        Path.Combine(GetFileDirectory(file.ConnectionId, file.FileId), "content");

    public static ApiFile ToApi(StoredFile file) => new(
        file.FileId,
        file.ConnectionId,
        file.Name,
        file.ContentType,
        file.Size,
        file.Source,
        file.CreatedAt,
        $"/v1/files/{file.FileId}/content",
        $"/v1/files/{file.FileId}/content?download=true");

    private string GetFileDirectory(string connectionId, string fileId) =>
        Path.Combine(_root, "connections", connectionId, "files", fileId);

    private void SaveMetadata(StoredFile file)
    {
        var directory = GetFileDirectory(file.ConnectionId, file.FileId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "file.json");
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(file, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
