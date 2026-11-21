$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dir = Join-Path ([IO.Path]::GetTempPath()) ('mezhs-chatgpt-typed-protocol-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir -Force | Out-Null
try {
  $integration = Join-Path $root 'integrations\Mezhs.Integrations.ChatGpt\Mezhs.Integrations.ChatGpt.csproj'
  @"
<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="$integration" /></ItemGroup>
</Project>
"@ | Set-Content (Join-Path $dir 'Protocol.csproj') -Encoding UTF8

@'
using System.Net.Http;
using System.Text.Json;
using Mezhs.Browser;
using Mezhs.Integrations;
using Mezhs.Integrations.ChatGpt;
using Mezhs.Integrations.Browser;

await TestNewChatMetadataAndCitations();
await TestInaccessibleContinuationRestoration();
await TestPostFailureNeverReplays();
await TestKnownConversationStreamInterruptionReconciles();
await TestAuthorizationFailureNeverReplays();
await TestImageUploadAndDownload();
Console.WriteLine("PASS: typed C# ChatGPT protocol - metadata, citations, restoration, no-replay, interruption, auth and file transfers");

static async Task TestNewChatMetadataAndCitations()
{
    var host = new FakeHost();
    await using var integration = NewIntegration(host);
    var activities = new List<IntegrationActivity>();
    var id = integration.CreateMessageId();
    var result = await integration.PromptAsync(Chat(activities.Add),
        new PromptRequest(id, "hello typed protocol",
            "requested-model::thinking-effort=extended", []));
    Verify(result.MessageId == "assistant-final", "wrong final assistant id");
    Verify(result.Text.Contains("[Example](https://example.org)"), "known citation not substituted");
    Verify(result.Text.Contains("[citation unavailable]"), "unknown citation was hidden");
    Verify(result.Model == "served-model::thinking-effort=high", "provider-confirmed served model/effort lost");
    Verify(host.Posts == 1 && host.SubmittedMessageId == id, "message id changed or prompt was replayed");
    var request = JsonDocument.Parse(host.SubmittedBody!);
    Verify(request.RootElement.GetProperty("messages")[0].GetProperty("content")
        .GetProperty("parts")[0].GetString() == "hello typed protocol", "prompt body changed");
    Verify(request.RootElement.GetProperty("model").GetString() == "requested-model", "requested model lost");
    Verify(request.RootElement.GetProperty("thinking_effort").GetString() == "extended", "requested effort lost");
    Verify(request.RootElement.GetProperty("parent_message_id").GetString() == "client-created-root",
        "new conversation ancestry changed");
    Verify(host.Calls.Any(c => c.Path.Contains("user_last_used_model_config") &&
        c.Path.Contains("thinking_effort=extended")), "model-preference PATCH was dropped");
    Verify(host.SubmittedNativeHook == "chatgptTurn" && host.SubmittedMessageId == id,
        "native per-turn security hook or scoped Stop identity missing");
    Verify(activities.Any(a => a.State == "thinking"), "stream reasoning progress missing");
    Verify(activities.Any(a => a.State == "responding"), "stream final-channel progress missing");
    Verify(activities.Any(a => a.State == "completed" && a.Analysis == "Analysis summary"),
        "final analysis propagation missing");
    Verify(activities.Any(a => a.Detail?.Contains("reasoning 3.5s") == true),
        "stream reasoning metadata not merged");
    Verify(activities.Any(a => a.Detail?.Contains("tools web.run") == true),
        "stream tool usage metadata not merged");
}

static async Task TestInaccessibleContinuationRestoration()
{
    var host = new FakeHost { InaccessibleFirst = true };
    await using var integration = NewIntegration(host);
    var now = DateTimeOffset.UtcNow;
    var history = new ChatMessage[] {
        new("old-user", "account", "user", "older question", true, now.AddMinutes(-2)),
        new("old-answer", "account", "assistant", "older answer", true, now.AddMinutes(-1))
    };
    var chat = new ChatContext("chat", "account", "stale-conversation",
        (_, _, _, token) => Task.FromResult<IReadOnlyList<ChatMessage>>(history),
        remoteParentMessageId: "old-parent", restoreConversation: true);
    var result = await integration.PromptAsync(chat,
        new PromptRequest(integration.CreateMessageId(), "current question", null, []));
    Verify(result.Text.Contains("Example"), "restoration result missing");
    Verify(host.Posts == 1, "inaccessible conversation was submitted or replayed");
    Verify(host.Calls.Count(c => c.Path == "/backend-api/conversation/stale-conversation") == 1,
        "stale conversation was not checked exactly once before sending");
    Verify(host.SubmittedBody!.Contains("older question") && host.SubmittedBody.Contains("older answer")
        && host.SubmittedBody.Contains("current question"), "restoration omitted history");
}

static async Task TestPostFailureNeverReplays()
{
    var host = new FakeHost { FailPost = true };
    await using var integration = NewIntegration(host);
    try {
        await integration.PromptAsync(Chat(), new PromptRequest(
            integration.CreateMessageId(), "submit once", null, []));
        throw new Exception("ambiguous POST did not fail");
    } catch (InvalidOperationException e) when (e.Message.Contains("must not be resent")) { }
    Verify(host.Posts == 1, "ambiguous first POST was replayed");
}

static async Task TestKnownConversationStreamInterruptionReconciles()
{
    var host = new FakeHost { InterruptAfterId = true };
    await using var integration = NewIntegration(host);
    var result = await integration.PromptAsync(Chat(), new PromptRequest(
        integration.CreateMessageId(), "recover after id", null, []));
    Verify(host.Posts == 1, "stream interruption replayed the POST");
    Verify(result.MessageId == "assistant-final", "conversation was not reconciled");
}

static async Task TestAuthorizationFailureNeverReplays()
{
    var host = new FakeHost { AuthorizationPostFailure = true };
    await using var integration = NewIntegration(host);
    try {
        await integration.PromptAsync(Chat(), new PromptRequest(
            integration.CreateMessageId(), "blocked auth", null, []));
        throw new Exception("authorization failure did not propagate");
    } catch (BrowserAuthorizationRequiredException) { }
    Verify(host.Posts == 1, "authorization failure prompted a replay");
}

static async Task TestImageUploadAndDownload()
{
    var host = new FakeHost { WithFiles = true };
    var image = Path.Combine(Path.GetTempPath(), "mezhs-test-" + Guid.NewGuid().ToString("N") + ".png");
    var output = Path.Combine(Path.GetTempPath(), "mezhs-test-output-" + Guid.NewGuid().ToString("N"));
    var bytes = new byte[24];
    bytes[1] = (byte)'P'; bytes[2] = (byte)'N'; bytes[3] = (byte)'G';
    bytes[19] = 2; bytes[23] = 3;
    await File.WriteAllBytesAsync(image, bytes);
    await File.WriteAllTextAsync(output, "sample artifact");
    host.DownloadPath = output;
    try {
        await using var integration = NewIntegration(host);
        var files = new[] { new IntegrationInputFile("local-test-file", image, "screenshot.png", "image/png", bytes.Length) };
        var result = await integration.PromptAsync(Chat(), new PromptRequest(
            integration.CreateMessageId(), "analyze screenshot", null, files));
        Verify(host.Transfers.Count == 2, "expected signed upload and output download");
        Verify(host.Transfers[0].Action == "upload" &&
            host.Transfers[0].ContentType == "image/png", "wrong uploaded file");
        Verify(host.Transfers[1].Action == "download", "output was not fetched");
        Verify(host.SubmittedBody!.Contains("image_asset_pointer") &&
            host.SubmittedBody.Contains("file-service://file-1"), "multimodal pointer missing");
        Verify(result.Files is { Count: 1 } && result.Files[0].Path == output,
            "download artifact path was not propagated");
    }
    finally { File.Delete(image); File.Delete(output); }
}

static ChatGptAccountIntegration NewIntegration(FakeHost host) =>
    new(new IntegrationConnection("account", "Account", "chatgpt-web-account",
        new Dictionary<string,string?>()), host);

static ChatContext Chat(Action<IntegrationActivity>? report = null) =>
    new("chat", "account", null,
        (_, _, _, _) => Task.FromResult<IReadOnlyList<ChatMessage>>([]), reportActivity: report);

static void Verify(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class FakeHost : IBrowserIntegrationHost
{
    public int BrowserIdleMinutes => 0;
    public bool InaccessibleFirst, FailPost, InterruptAfterId, AuthorizationPostFailure, WithFiles;
    public string? DownloadPath, SubmittedBody, SubmittedMessageId, SubmittedNativeHook;
    public int Posts;
    public List<HttpTransportRequest> Calls { get; } = [];
    public List<Transfer> Transfers { get; } = [];
    public string GetConnectionRoot(string _) => Path.GetTempPath();
    public IChatBrowserTransport CreateBrowserTransport() => new FakeTransport(this);
}
record Transfer(string Action, string Url, string? ContentType);

sealed class FakeTransport(FakeHost host) : IChatBrowserTransport
{
    private static void Verify(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Name => "ProtocolFake";
    public Task InitializeAsync(BrowserTransportOptions _, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ShowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task<TResult> InvokeAsync<TResult>(string operation,
        object? arguments = null, CancellationToken cancellationToken = default)
    {
        if (operation == "typedHttp")
        {
            var req = (HttpTransportRequest)arguments!;
            host.Calls.Add(req);
            return Result<TResult>(Http(req));
        }
        if (operation == "binaryTransfer")
        {
            var transfer = (Mezhs.Integrations.Browser.BrowserBinaryTransferRequest)arguments!;
            host.Transfers.Add(new Transfer(transfer.Action, transfer.Url, transfer.ContentType));
            var result = new Mezhs.Integrations.Browser.BrowserBinaryTransferResult(
                24, 200, transfer.Action == "download" ? host.DownloadPath : null, "text/plain");
            return Result<TResult>(result);
        }
        throw new Exception("Unexpected browser operation: " + operation);
    }

    public Task<TResult> InvokeWithEventsAsync<TResult>(string operation,
        object? arguments, Action<JsonElement> reportEvent,
        Action<BrowserOperationProgress>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        var req = (HttpTransportRequest)arguments!;
        host.Calls.Add(req);
        host.Posts++;
        host.SubmittedBody = req.Body;
        host.SubmittedMessageId = req.TurnMessageId;
        host.SubmittedNativeHook = req.NativeHook;
        if (host.AuthorizationPostFailure)
            throw new BrowserAuthorizationRequiredException("Authorization required.");
        if (host.FailPost)
            throw new InvalidOperationException("Connection interrupted.");
        using var parsed = JsonDocument.Parse(req.Body!);
        Verify(req.Stream && req.Path == "/backend-api/f/conversation" &&
            req.NativeHook == "chatgptTurn", "wrong stream transport mapping");
        Verify(req.TurnMessageId == parsed.RootElement.GetProperty("messages")[0]
            .GetProperty("id").GetString(), "stream Stop message id differs from payload id");
        if (host.WithFiles) {
            var prepare = JsonSerializer.Serialize(req.NativeArgs, Json);
            Verify(prepare.Contains("partial_query") && !prepare.Contains("image_asset_pointer"),
                "prepare payload must omit binary image pointers");
        }

        void Event(string text) => reportEvent(JsonSerializer.SerializeToElement(text));
        Event("""{"type":"message_marker","marker":"cot_token","conversation_id":"conv-1"}""");
        Event("""{"type":"message_marker","marker":"final_channel_token"}""");
        Event("""{"v":{"message":{"author":{"role":"assistant"},"metadata":{"model_slug":"served-model","thinking_effort":"high","reasoning_start_time":100.25,"reasoning_end_time":103.75}}}}""");
        if (host.InterruptAfterId)
            throw new InvalidOperationException("Stream interrupted after conversation id.");
        return Result<TResult>(new HttpTransportResponse(200, "", "text/event-stream"));
    }

    private HttpTransportResponse Http(HttpTransportRequest req)
    {
        var path = req.Path;
        if (path.StartsWith("/backend-api/settings/user_last_used_model_config", StringComparison.Ordinal))
            return new(200, "", "application/json");
        if (path == "/backend-api/conversation/stale-conversation" && host.InaccessibleFirst)
            return new(404, "", ProviderCode: "conversation_inaccessible");
        if (path == "/backend-api/files")
            return new(200, """{"file_id":"file-1","upload_url":"https://test.blob.core.windows.net/container/name","upload_headers":{"x-ms-meta-test":"ok"}}""");
        if (path == "/backend-api/files/process_upload_stream")
            return new(200, "{\"event\":\"file.processing.completed\",\"file_id\":\"file-1\"}\n",
                "text/event-stream");
        if (path == "/backend-api/files/file-output/download")
            return new(200, """{"download_url":"https://test.blob.core.windows.net/out/file"}""");
        if (path == "/backend-api/conversation/conv-1")
            return new(200, CompleteConversation(), "application/json");
        if (path.StartsWith("/backend-api/conversation/", StringComparison.Ordinal))
            return new(200, """{"conversation_id":"existing","mapping":{}}""");
        throw new Exception("Unexpected HTTP endpoint: " + path);
    }

    private string CompleteConversation()
    {
        var marker = "\uE200cite\uE202turn0search0\uE201";
        var missing = "\uE200cite\uE202turn0search4\uE201";
        object m1 = new {
            id = "assistant-final", author = new { role = "assistant" }, channel = "final",
            status = "finished_successfully",
            content = new { content_type = "text", parts = new [] { "See " + marker + " and " + missing } },
            metadata = new { model_slug = "served-model", thinking_effort = "high",
                content_references = new [] { new { matched_text = marker,
                    alt = "[Example](https://example.org)" } },
                attachments = host.WithFiles ? new [] { new { id = "file-output", name = "answer.txt" } } : null }
        };
        object m2 = new {
            id = "analysis", author = new { role = "assistant" }, channel = "analysis",
            status = "finished_successfully",
            content = new { content_type = "text", parts = new [] { "Analysis summary" } },
            metadata = new { tool_name = "web.run" }
        };
        object m3 = new {
            id = host.SubmittedMessageId, author = new { role = "user" },
            status = "finished_successfully",
            content = new { content_type = "text", parts = new [] { "hello" } },
            metadata = new { resolved_model_slug = "served-model" }
        };
        return JsonSerializer.Serialize(new {
            conversation_id = "conv-1", current_node = "assistant-final",
            mapping = new Dictionary<string,object> {
                ["assistant-final"] = new { parent = "analysis", message = m1 },
                ["analysis"] = new { parent = "user", message = m2 },
                ["user"] = new { parent = (string?)null, message = m3 }
            }
        }, Json);
    }

    private static Task<TResult> Result<TResult>(object value) =>
        Task.FromResult(JsonSerializer.Deserialize<TResult>(
            JsonSerializer.Serialize(value, Json), Json)!);
}
'@ | Set-Content (Join-Path $dir 'Program.cs') -Encoding UTF8

  dotnet run --project (Join-Path $dir 'Protocol.csproj') -c Release
  if ($LASTEXITCODE -ne 0) { throw "ChatGPT typed C# protocol probe failed: $LASTEXITCODE" }
}
finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
