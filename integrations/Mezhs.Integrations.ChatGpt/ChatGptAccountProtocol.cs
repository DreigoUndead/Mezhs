using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mezhs.Browser;
using Mezhs.Integrations.Browser;

namespace Mezhs.Integrations.ChatGpt;

internal sealed record ChatGptInputFile(string Path, string Name, string ContentType);
internal sealed record ChatGptSendRequest(
    string Prompt, string MessageId, string? ConversationId, string? ParentMessageId,
    string? ProjectId, string? Model, IReadOnlyList<ChatGptInputFile> Files);
internal sealed record ChatGptSendResponse(
    string Text, string ConversationId, string ParentMessageId,
    string? ProjectId, string? ChatUrl, IReadOnlyList<BrowserArtifact>? Artifacts,
    string? Model = null, bool ConversationUnavailable = false);

internal readonly record struct ChatGptModelSelection(string Model, string? ThinkingEffort)
{
    private const string Separator = "::thinking-effort=";

    public static ChatGptModelSelection Parse(string? value)
    {
        var selected = string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim();
        var marker = selected.LastIndexOf(Separator, StringComparison.Ordinal);
        return marker > 0 ? new(selected[..marker],
            string.IsNullOrWhiteSpace(selected[(marker + Separator.Length)..])
                ? null : selected[(marker + Separator.Length)..].Trim())
            : new(selected, null);
    }

    public static string Id(string model, string? thinkingEffort) =>
        string.IsNullOrWhiteSpace(thinkingEffort) ? model : model + Separator + thinkingEffort;
}

/// <summary>
/// ChatGPT's private API mapping and turn orchestration. Only request-local
/// Sentinel/conduit generation and authenticated fetch stay inside Electron.
/// </summary>
internal static class ChatGptAccountProtocol
{
    private static readonly HttpEndpoint<EmptyHttpRequest, JsonElement> Preference =
        new(HttpMethod.Patch, "/backend-api/settings/user_last_used_model_config",
            Interpret: _ => default);

    private const string StreamEndpoint = "/backend-api/f/conversation";
    private static readonly HttpEndpoint<JsonObject, string> ConversationStream =
        new(HttpMethod.Post, StreamEndpoint, HttpResultFormat.ServerSentEvents);

    public static async Task<ChatGptSendResponse> SendAsync(
        IChatBrowserTransport transport, ChatGptSendRequest request, bool isNew,
        Action<BrowserOperationProgress>? progress, CancellationToken cancellationToken)
    {
        var http = new BrowserHttpExecutor(transport);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke(new("preparing", "Checking ChatGPT account session."));
        var selection = ChatGptModelSelection.Parse(request.Model);
        if (selection.Model != "auto")
        {
            progress?.Invoke(new("preparing", "Applying ChatGPT model selection."));
            var path = Preference.Path + "?model_slug=" + Uri.EscapeDataString(selection.Model) +
                (selection.ThinkingEffort is null ? "" :
                    "&thinking_effort=" + Uri.EscapeDataString(selection.ThinkingEffort));
            await http.CallAsync(new HttpEndpoint<EmptyHttpRequest, JsonElement>(
                HttpMethod.Patch, path, Interpret: _ => default),
                request: null, cancellationToken: cancellationToken);
        }

        if (!isNew)
        {
            progress?.Invoke(new("preparing", "Checking existing ChatGPT conversation."));
            try
            {
                await http.CallAsync(new HttpEndpoint<EmptyHttpRequest, JsonElement>(
                    HttpMethod.Get, ConversationPath(request.ConversationId!)),
                    request: null, cancellationToken: cancellationToken);
            }
            catch (IntegrationHttpException error) when (
                error.StatusCode == HttpStatusCode.NotFound &&
                error.ProviderCode == "conversation_inaccessible")
            {
                return new ChatGptSendResponse("", "", "", null, null, null,
                    ConversationUnavailable: true);
            }
        }

        progress?.Invoke(new("preparing", "Uploading files to ChatGPT."));
        var uploads = await ChatGptAccountFiles.UploadAsync(http, transport, request.Files,
            cancellationToken);
        var payload = BuildPayload(request, isNew, selection, uploads);
        var sessionId = Guid.NewGuid().ToString();
        var execution = new ChatGptExecution {
            RequestedModel = selection.Model == "auto" ? null : selection.Model,
            RequestedThinkingEffort = selection.ThinkingEffort
        };
        if (isNew && request.ProjectId is not null)
        {
            var initPayload = new JsonObject {
                ["gizmo_id"] = request.ProjectId,
                ["requested_default_model"] = null,
                ["conversation_id"] = null,
                ["timezone"] = Timezone(),
                ["timezone_offset_min"] = OffsetMinutes(),
                ["conversation_origin"] = null
            };
            var initialized = await http.CallAsync(new HttpEndpoint<JsonObject, JsonElement>(
                HttpMethod.Post, "/backend-api/conversation/init",
                Accept: "*/*", NativeHook: "chatgptWeb",
                NativeArgs: new { ClientSessionId = sessionId }),
                initPayload, cancellationToken: cancellationToken);
            execution.InitializedDefaultModel = ChatGptJson.String(initialized,
                "intended_default_model_slug", "default_model_slug");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParse(request.MessageId, out _))
            throw new InvalidOperationException("ChatGPT request requires a persisted UUID message id.");

        var stream = new ChatGptStreamState(execution, progress);
        var prepare = BuildPreparePayload(payload);
        progress?.Invoke(new("preparing", "Preparing ChatGPT turn and security requirements."));
        try
        {
            // One POST. Native tokens live only inside the same browser operation.
            await http.CallStreamAsync(ConversationStream, payload, stream.OnEvent,
                nativeHook: "chatgptTurn",
                nativeArgs: new { ClientSessionId = sessionId, Prepare = prepare },
                reportProgress: progress, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (BrowserAuthorizationRequiredException) { throw; }
        catch (IntegrationAuthorizationRequiredException) { throw; }
        catch (IntegrationHttpException) { throw; }
        catch (Exception error)
        {
            if (stream.ConversationId is null && request.ConversationId is null)
                throw new InvalidOperationException(
                    "ChatGPT stream failed after submission; delivery may have succeeded, " +
                    "so the turn must not be resent.", error);
            progress?.Invoke(new("waiting", "Stream interrupted; reconciling existing conversation."));
        }

        var conversationId = stream.ConversationId ?? request.ConversationId;
        if (string.IsNullOrEmpty(conversationId))
            throw new InvalidOperationException("ChatGPT did not return a conversation id. " +
                "The submission outcome is uncertain and must not be replayed.");

        progress?.Invoke(new("retrieving", "Retrieving final ChatGPT response."));
        var result = await ChatGptConversationState.WaitAsync(http, conversationId,
            request.MessageId, execution, progress, cancellationToken);
        IReadOnlyList<BrowserArtifact> artifacts = [];
        if (result.Files.Count > 0)
        {
            progress?.Invoke(new("retrieving", "Downloading ChatGPT response attachments."));
            artifacts = await ChatGptAccountFiles.DownloadAsync(http, transport,
                result.Files, cancellationToken);
        }
        return new ChatGptSendResponse(result.Text, conversationId,
            result.ParentMessageId, result.ProjectId,
            "https://chatgpt.com/c/" + Uri.EscapeDataString(conversationId),
            artifacts, result.Model);
    }

    internal static string ConversationPath(string id) =>
        "/backend-api/conversation/" + Uri.EscapeDataString(id);

    private static JsonObject BuildPayload(ChatGptSendRequest request, bool isNew,
        ChatGptModelSelection selection, IReadOnlyList<ChatGptUploadedFile> uploaded)
    {
        var parts = new JsonArray();
        var attachments = new JsonArray();
        foreach (var file in uploaded)
        {
            if (file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                parts.Add(new JsonObject {
                    ["content_type"] = "image_asset_pointer",
                    ["asset_pointer"] = "file-service://" + file.Id,
                    ["size_bytes"] = file.Size,
                    ["width"] = file.Width,
                    ["height"] = file.Height
                });
            var attachment = new JsonObject {
                ["id"] = file.Id, ["name"] = file.Name,
                ["mimeType"] = file.ContentType, ["size"] = file.Size
            };
            if (file.Width is not null)
            {
                attachment["width"] = file.Width;
                attachment["height"] = file.Height;
            }
            attachments.Add(attachment);
        }
        parts.Add(request.Prompt);
        var metadata = new JsonObject {
            ["selected_sources"] = new JsonArray(),
            ["serialization_metadata"] = new JsonObject {
                ["custom_symbol_offsets"] = new JsonArray()
            },
            ["submission_mode"] = "manual_send"
        };
        if (attachments.Count > 0) metadata["attachments"] = attachments;

        var payload = new JsonObject {
            ["action"] = "next",
            ["messages"] = new JsonArray(new JsonObject {
                ["id"] = request.MessageId,
                ["author"] = new JsonObject { ["role"] = "user" },
                ["create_time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d,
                ["content"] = new JsonObject {
                    ["content_type"] = uploaded.Any(f => f.ContentType.StartsWith("image/",
                        StringComparison.OrdinalIgnoreCase)) ? "multimodal_text" : "text",
                    ["parts"] = parts
                },
                ["metadata"] = metadata
            }),
            ["model"] = selection.Model,
            ["parent_message_id"] = isNew ? "client-created-root" : request.ParentMessageId,
            ["client_prepare_state"] = "success",
            ["timezone_offset_min"] = OffsetMinutes(),
            ["timezone"] = Timezone(),
            ["enable_message_followups"] = true,
            ["system_hints"] = new JsonArray(),
            ["supports_buffering"] = true,
            ["supported_encodings"] = new JsonArray("v1"),
            ["client_contextual_info"] = ClientContext(),
            ["paragen_cot_summary_display_override"] = "allow",
            ["force_parallel_switch"] = "auto",
            ["local_function_names"] = new JsonArray("local.continue_in_work")
        };
        if (!isNew) payload["conversation_id"] = request.ConversationId;
        if (isNew)
            payload["conversation_mode"] = request.ProjectId is null
                ? new JsonObject { ["kind"] = "primary_assistant" }
                : new JsonObject { ["kind"] = "gizmo_interaction",
                    ["gizmo_id"] = request.ProjectId };
        if (selection.ThinkingEffort is not null)
            payload["thinking_effort"] = selection.ThinkingEffort;
        return payload;
    }

    private static JsonObject BuildPreparePayload(JsonObject payload)
    {
        var message = payload["messages"]?[0];
        var rawParts = message?["content"]?["parts"]?.AsArray();
        var preparedParts = new JsonArray();
        foreach (var part in rawParts ?? [])
            if (part is JsonValue value && value.TryGetValue<string>(out var text))
                preparedParts.Add(text);
        var result = new JsonObject {
            ["action"] = payload["action"]?.DeepClone(),
            ["parent_message_id"] = payload["parent_message_id"]?.DeepClone(),
            ["model"] = payload["model"]?.DeepClone(),
            ["client_prepare_state"] = "none",
            ["client_prepare_dispatch"] = "debounced",
            ["client_prepare_source"] = "composer_editor_state",
            ["timezone_offset_min"] = payload["timezone_offset_min"]?.DeepClone(),
            ["timezone"] = payload["timezone"]?.DeepClone(),
            ["conversation_mode"] = payload["conversation_mode"]?.DeepClone() ??
                new JsonObject { ["kind"] = "primary_assistant" },
            ["system_hints"] = new JsonArray(),
            ["partial_query"] = new JsonObject {
                ["id"] = message?["id"]?.DeepClone(),
                ["author"] = message?["author"]?.DeepClone(),
                ["content"] = new JsonObject {
                    ["content_type"] = "text", ["parts"] = preparedParts
                }
            },
            ["supports_buffering"] = payload["supports_buffering"]?.DeepClone(),
            ["supported_encodings"] = payload["supported_encodings"]?.DeepClone(),
            ["client_contextual_info"] = ClientContext(),
            ["local_function_names"] = payload["local_function_names"]?.DeepClone()
        };
        if (payload["conversation_id"] is { } id)
            result["conversation_id"] = id.DeepClone();
        if (payload["thinking_effort"] is { } effort)
            result["thinking_effort"] = effort.DeepClone();
        return result;
    }

    private static JsonObject ClientContext() => new() {
        ["app_name"] = "chatgpt.com",
        ["has_web_push_capabilities"] = false,
        ["web_push_notification_permission"] = "default"
    };

    private static int OffsetMinutes() => -(int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes;
    private static string Timezone()
    {
        var id = TimeZoneInfo.Local.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) ? iana : id;
    }
}
