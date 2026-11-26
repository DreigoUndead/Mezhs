using System.Buffers.Binary;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mezhs.Browser;
using Mezhs.Integrations.Browser;

namespace Mezhs.Integrations.ChatGpt;

internal sealed record ChatGptUploadedFile(
    string Id, string Name, string ContentType, long Size, int? Width, int? Height);

/// <summary>ChatGPT file metadata mapping; signed binary I/O belongs to shared Electron transport.</summary>
internal static class ChatGptAccountFiles
{
    private const string FilesEndpoint = "/backend-api/files";
    private const long MaxFileBytes = 100L * 1024 * 1024;

    public static async Task<IReadOnlyList<ChatGptUploadedFile>> UploadAsync(
        BrowserHttpExecutor http,
        IReadOnlyList<ChatGptInputFile> files, CancellationToken cancellationToken)
    {
        var uploads = new List<ChatGptUploadedFile>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file.Path);
            if (!info.Exists || info.Length > MaxFileBytes)
                throw new InvalidOperationException("ChatGPT input file is missing or exceeds 100 MiB.");
            var contentType = string.IsNullOrWhiteSpace(file.ContentType)
                ? "application/octet-stream" : file.ContentType;
            var (width, height) = await ReadImageDimensions(file.Path, contentType, cancellationToken);
            if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                (width is null || height is null || width < 1 || height < 1))
                throw new InvalidOperationException("Unsupported or invalid image format or dimensions.");
            var useCase = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                ? "multimodal" : "my_files";

            var created = await http.CallAsync(new HttpEndpoint<JsonObject, JsonElement>(
                HttpMethod.Post, FilesEndpoint), new JsonObject {
                    ["file_name"] = file.Name, ["file_size"] = info.Length,
                    ["use_case"] = useCase, ["mime_type"] = contentType,
                    ["entry_surface"] = "chat_composer",
                    ["timezone_offset_min"] = -(int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes
                }, cancellationToken: cancellationToken);
            if (ChatGptJson.String(created, "status") == "error" ||
                ChatGptJson.String(created, "file_id") is not { } fileId ||
                ChatGptJson.String(created, "upload_url") is not { } uploadUrl)
                throw new InvalidOperationException("ChatGPT did not provide a usable file upload destination.");

            var strategy = ChatGptJson.Get(created, "direct_library_upload_strategy");
            if (ChatGptJson.String(strategy, "kind") == "direct_azure_multipart" &&
                ChatGptJson.Number(strategy, "part_count") >= 2)
                throw new NotSupportedException("ChatGPT multipart file uploads are not supported.");

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sourceHeaders = ChatGptJson.Get(created, "upload_headers");
            if (sourceHeaders.ValueKind == JsonValueKind.Object)
                foreach (var item in sourceHeaders.EnumerateObject())
                    if (item.Value.ValueKind == JsonValueKind.String)
                        headers[item.Name] = item.Value.GetString() ?? "";

            // Binary data travels through a bounded Electron stream; no file content,
            // bearer token, signed destination or Sentinel header crosses to progress/logs.
            await http.TransferAsync(new BrowserBinaryTransferRequest(
                "upload", uploadUrl, LocalPath: file.Path,
                ContentType: contentType, Headers: headers), cancellationToken: cancellationToken);

            var processed = await http.CallAsync(new HttpEndpoint<JsonObject, string>(
                HttpMethod.Post, "/backend-api/files/process_upload_stream",
                HttpResultFormat.Text, Accept: "text/event-stream"),
                new JsonObject {
                    ["file_id"] = fileId,
                    ["use_case"] = useCase,
                    ["index_for_retrieval"] = useCase == "my_files",
                    ["file_name"] = file.Name,
                    ["entry_surface"] = "chat_composer",
                    ["mime_type"] = contentType
                }, cancellationToken: cancellationToken);
            VerifyProcessing(processed, fileId);
            uploads.Add(new ChatGptUploadedFile(fileId, file.Name, contentType,
                info.Length, width, height));
        }
        return uploads;
    }

    // Sandbox links do not contain a library file ID. Resolve only current-turn
    // Markdown download links beneath /mnt/data, not arbitrary URLs or paths.
    internal static IReadOnlyList<string> FindSandboxPaths(string text)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(text,
            @"\]\(\s*(?:<sandbox:(?<path>/mnt/data/[^<>\r\n]+)>|sandbox:(?<path>/mnt/data/[^\s()<>]+))\s*\)",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
        {
            var encoded = match.Groups["path"].Value;
            if (encoded.Length > 2048) continue;
            var path = Uri.UnescapeDataString(encoded);
            if (!path.StartsWith("/mnt/data/", StringComparison.Ordinal) ||
                path.Any(c => char.IsControl(c) || c is '\\' or ':' or '?' or '#' or '%') ||
                path["/mnt/data/".Length..].Split('/').Any(part => part is "" or "." or ".."))
                continue;
            if (seen.Add(path)) paths.Add(path);
        }
        return paths;
    }

    public static async Task<IReadOnlyList<BrowserArtifact>> DownloadAsync(
        BrowserHttpExecutor http,
        IReadOnlyDictionary<string, string> refs, string conversationId,
        string assistantMessageId, IReadOnlyList<string> sandboxPaths,
        Action<BrowserOperationProgress>? progress, CancellationToken cancellationToken)
    {
        var artifacts = new List<BrowserArtifact>();
        var downloads = refs.Select(pair => (Endpoint:
            FilesEndpoint + "/" + Uri.EscapeDataString(pair.Key) + "/download", Name: pair.Value))
            .Concat(sandboxPaths.Select(path => (Endpoint:
                ChatGptAccountProtocol.ConversationPath(conversationId) + "/interpreter/download" +
                "?message_id=" + Uri.EscapeDataString(assistantMessageId) +
                "&sandbox_path=" + Uri.EscapeDataString(path) + "&download_intent=true",
                Name: Path.GetFileName(path))));
        foreach (var (endpoint, name) in downloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stage = "resolution";
            try
            {
                var response = await http.CallAsync(
                    new HttpEndpoint<EmptyHttpRequest, JsonElement>(HttpMethod.Get, endpoint),
                    request: null, cancellationToken: cancellationToken);
                var url = ChatGptJson.String(response, "download_url");
                if (url is null)
                {
                    progress?.Invoke(new("retrieving", "ChatGPT attachment resolution returned no download URL."));
                    continue;
                }
                // Preserve same-origin relative URLs supported by the old provider.
                // All destinations remain subject to the shared signed-transfer policy.
                if (url.StartsWith("/", StringComparison.Ordinal) &&
                    !url.StartsWith("//", StringComparison.Ordinal))
                    url = new Uri(new Uri("https://chatgpt.com"), url).AbsoluteUri;
                stage = "transfer";
                var safeName = Path.GetFileName(name);
                if (string.IsNullOrWhiteSpace(safeName)) safeName = "download";
                var transferred = await http.TransferAsync(new BrowserBinaryTransferRequest(
                    "download", url, Name: safeName), cancellationToken: cancellationToken);
                if (!string.IsNullOrWhiteSpace(transferred.LocalPath))
                    artifacts.Add(new BrowserArtifact("", safeName,
                        transferred.ContentType, transferred.LocalPath));
                else
                    progress?.Invoke(new("retrieving", "ChatGPT attachment transfer returned no local file."));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (IntegrationHttpException error)
            {
                var detail = error.StatusCode is { } status
                    ? " failed (HTTP " + (int)status + ")."
                    : " failed (HTTP status unavailable).";
                progress?.Invoke(new("retrieving", "ChatGPT attachment " + stage + detail));
            }
            catch
            {
                // Best-effort artifact retrieval; never log a signed URL, secret or response.
                progress?.Invoke(new("retrieving", "ChatGPT attachment " + stage + " failed."));
            }
        }
        return artifacts;
    }

    private static void VerifyProcessing(string body, string fileId)
    {
        var ready = false;
        var failed = false;
        var frameEvent = "";
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                // An SSE event name cannot carry across a frame boundary.
                frameEvent = "";
                continue;
            }
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                frameEvent = line[6..].Trim();
                continue;
            }
            var isSseData = line.StartsWith("data:", StringComparison.Ordinal);
            var text = isSseData ? line[5..].Trim() : line;
            if (!text.StartsWith("{", StringComparison.Ordinal)) continue;
            try
            {
                using var parsed = JsonDocument.Parse(text);
                var value = parsed.RootElement;
                // The endpoint also returns bare NDJSON. Only SSE data lines
                // inherit an SSE event name; bare JSON needs its own event.
                var eventName = ChatGptJson.String(value, "event") ??
                    (isSseData ? frameEvent : "");
                if (ChatGptJson.String(value, "file_id") != fileId) continue;
                if (eventName.EndsWith(".error", StringComparison.Ordinal) ||
                    eventName.EndsWith(".failed", StringComparison.Ordinal) ||
                    eventName.EndsWith(".cancelled", StringComparison.Ordinal) ||
                    eventName.EndsWith(".unknown", StringComparison.Ordinal)) failed = true;
                if (eventName is "file.processing.completed" or "file.processing.file_ready")
                    ready = true;
            }
            catch (JsonException) { /* Ignore a malformed event; require explicit success. */ }
        }
        if (failed || !ready)
            throw new InvalidOperationException(
                "ChatGPT file processing did not confirm success.");
    }

    private static async Task<(int? Width, int? Height)> ReadImageDimensions(
        string path, string contentType, CancellationToken cancellationToken)
    {
        if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return (null, null);
        await using var input = File.OpenRead(path);
        var header = new byte[(int)Math.Min(input.Length, 256 * 1024)];
        var read = 0;
        while (read < header.Length)
        {
            var count = await input.ReadAsync(header.AsMemory(read), cancellationToken);
            if (count == 0) break;
            read += count;
        }
        var bytes = header.AsSpan(0, read);
        if (contentType == "image/png" && bytes.Length >= 24 &&
            bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G')
            return (BinaryPrimitives.ReadInt32BigEndian(bytes[16..20]),
                BinaryPrimitives.ReadInt32BigEndian(bytes[20..24]));
        if (contentType == "image/gif" && bytes.Length >= 10 &&
            bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F')
            return (BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..8]),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..10]));
        if (contentType == "image/jpeg" && bytes.Length >= 10 &&
            bytes[0] == 0xff && bytes[1] == 0xd8)
        {
            for (var index = 2; index + 9 < bytes.Length;)
            {
                if (bytes[index] != 0xff) break;
                var marker = bytes[index + 1];
                if (marker is 0xda or 0xd9) break;
                var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[(index + 2)..]);
                if (length < 2) break;
                if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3)
                    return (BinaryPrimitives.ReadUInt16BigEndian(bytes[(index + 7)..]),
                        BinaryPrimitives.ReadUInt16BigEndian(bytes[(index + 5)..]));
                index += 2 + length;
            }
        }
        if (contentType == "image/webp" && bytes.Length >= 30 &&
            bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B')
        {
            if (bytes[12] == (byte)'V' && bytes[13] == (byte)'P' &&
                bytes[14] == (byte)'8' && bytes[15] == (byte)'X')
            {
                var width = 1 + (bytes[24] | bytes[25] << 8 | bytes[26] << 16);
                var height = 1 + (bytes[27] | bytes[28] << 8 | bytes[29] << 16);
                return (width, height);
            }
            if (bytes[12] == (byte)'V' && bytes[13] == (byte)'P' &&
                bytes[14] == (byte)'8' && bytes[15] == (byte)' ')
                return (BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..]) & 0x3fff,
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..]) & 0x3fff);
            if (bytes[12] == (byte)'V' && bytes[13] == (byte)'P' &&
                bytes[14] == (byte)'8' && bytes[15] == (byte)'L' && bytes[20] == 0x2f)
                return (1 + ((bytes[22] & 0x3f) << 8 | bytes[21]),
                    1 + ((bytes[24] & 0x0f) << 10 | bytes[23] << 2 | bytes[22] >> 6));
        }
        return (null, null);
    }
}
