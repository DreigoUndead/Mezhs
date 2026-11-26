using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mezhs.Browser;
using Mezhs.Integrations.Browser;

namespace Mezhs.Integrations.ChatGpt;

internal static class ChatGptJson
{
    public static JsonElement Get(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) ? property : default;

    public static string? String(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            var item = Get(value, name);
            if (item.ValueKind == JsonValueKind.String &&
                item.GetString() is { Length: > 0 } text)
                return text.Trim();
        }
        return null;
    }

    public static double? Number(JsonElement value, string name)
    {
        var item = Get(value, name);
        if (item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var number))
            return double.IsFinite(number) ? number : null;
        if (item.ValueKind == JsonValueKind.String &&
            double.TryParse(item.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
            double.IsFinite(parsed))
            return parsed;
        return null;
    }

    public static bool True(JsonElement value, string name) =>
        Get(value, name).ValueKind == JsonValueKind.True;

    public static IEnumerable<JsonElement> Children(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) yield return item;
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) yield return property.Value;
    }

    public static string? MessageText(JsonElement message)
    {
        var parts = Get(Get(message, "content"), "parts");
        if (parts.ValueKind != JsonValueKind.Array) return null;
        var text = string.Join("\n", parts.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()));
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}

internal sealed class ChatGptExecution
{
    public string? RequestedModel { get; set; }
    public string? RequestedThinkingEffort { get; set; }
    public string? InitializedDefaultModel { get; set; }
    public string? Model { get; set; }
    public string? ThinkingEffort { get; set; }
    public string? ReasoningStatus { get; set; }
    public string? RequestedModelExperience { get; set; }
    public double? ServerTtfvtMs { get; set; }
    public double? ReasoningStart { get; set; }
    public double? ReasoningEnd { get; set; }
    public bool ReasoningObserved { get; set; }
    public List<string> Tools { get; } = [];

    public void AddTool(string? tool)
    {
        if (string.IsNullOrWhiteSpace(tool)) return;
        var value = tool.Trim();
        if (!Tools.Contains(value, StringComparer.Ordinal)) Tools.Add(value);
    }

    public void ObserveMetadata(JsonElement metadata, bool includeModel = true)
    {
        var model = ChatGptJson.String(metadata, "resolved_model_slug", "model_slug");
        if (includeModel)
        {
            if (model is not null) Model ??= model;
            ThinkingEffort ??= ChatGptJson.String(metadata, "thinking_effort");
        }
        ReasoningStatus ??= ChatGptJson.String(metadata, "reasoning_status");
        var start = ChatGptJson.Number(metadata, "reasoning_start_time");
        var end = ChatGptJson.Number(metadata, "reasoning_end_time");
        if (ReasoningStatus is not null || start is not null || end is not null)
            ReasoningObserved = true;
        if (start is not null) ReasoningStart = ReasoningStart is null
            ? start : Math.Min(ReasoningStart.Value, start.Value);
        if (end is not null) ReasoningEnd = ReasoningEnd is null
            ? end : Math.Max(ReasoningEnd.Value, end.Value);
    }

    public void ObserveMessage(JsonElement message)
    {
        var author = ChatGptJson.Get(message, "author");
        var role = ChatGptJson.String(author, "role")?.ToLowerInvariant();
        var metadata = ChatGptJson.Get(message, "metadata");
        var recipient = ChatGptJson.String(message, "recipient");
        if (role == "assistant" && !string.IsNullOrWhiteSpace(recipient) &&
            !string.Equals(recipient, "all", StringComparison.OrdinalIgnoreCase))
            AddTool(recipient);
        if (role == "tool") AddTool(ChatGptJson.String(author, "name"));
        if (ChatGptJson.Get(metadata, "tool_invoked").ValueKind != JsonValueKind.False)
            AddTool(ChatGptJson.String(metadata, "tool_name"));
        if (role == "assistant")
        {
            ObserveMetadata(metadata, !ChatGptJson.True(metadata, "is_visually_hidden_from_conversation") &&
                ChatGptJson.Number(message, "weight") != 0);
            if (ChatGptJson.String(ChatGptJson.Get(message, "content"), "content_type") ==
                "reasoning_recap") ReasoningObserved = true;
        }
    }

    public void ObserveStream(JsonElement value)
    {
        var metadata = ChatGptJson.Get(value, "metadata");
        // Message metadata must pass through ObserveMessage so hidden/zero-weight
        // assistant nodes cannot contribute served-model or thinking-effort metadata.
        if (metadata.ValueKind == JsonValueKind.Object &&
            ChatGptJson.Get(value, "author").ValueKind != JsonValueKind.Object)
            ObserveMetadata(metadata);
        ObserveMessage(value);
        if (ChatGptJson.String(value, "type") == "server_ste_metadata")
        {
            RequestedModelExperience ??= ChatGptJson.String(metadata, "requested_model_experience");
            ServerTtfvtMs ??= ChatGptJson.Number(metadata, "server_ttfvt_ms");
        }
        foreach (var child in ChatGptJson.Children(value))
            if (child.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                ObserveStream(child);
    }

    public void MergeFinal(ChatGptExecution other)
    {
        // Final conversation state is authoritative for the served model/effort.
        if (other.Model is not null) { Model = other.Model; ThinkingEffort = other.ThinkingEffort; }
        else ThinkingEffort ??= other.ThinkingEffort;
        ReasoningStatus ??= other.ReasoningStatus;
        RequestedModelExperience ??= other.RequestedModelExperience;
        ServerTtfvtMs ??= other.ServerTtfvtMs;
        ReasoningObserved |= other.ReasoningObserved;
        if (other.ReasoningStart is not null) ReasoningStart = ReasoningStart is null
            ? other.ReasoningStart : Math.Min(ReasoningStart.Value, other.ReasoningStart.Value);
        if (other.ReasoningEnd is not null) ReasoningEnd = ReasoningEnd is null
            ? other.ReasoningEnd : Math.Max(ReasoningEnd.Value, other.ReasoningEnd.Value);
        foreach (var tool in other.Tools) AddTool(tool);
    }

    public string CompletionDetail()
    {
        var facts = new List<string>();
        if (Model is not null) facts.Add("served model " + Model);
        if (RequestedModel is not null && Model is not null && RequestedModel != Model)
            facts.Add("requested model " + RequestedModel);
        if (ThinkingEffort is not null) facts.Add("effort " + ThinkingEffort);
        else if (RequestedThinkingEffort is not null)
            facts.Add("requested effort " + RequestedThinkingEffort + ", not confirmed by provider");
        if (ReasoningStart is not null && ReasoningEnd >= ReasoningStart)
            facts.Add("reasoning " + (ReasoningEnd.Value - ReasoningStart.Value).ToString(
                "F1", System.Globalization.CultureInfo.InvariantCulture) + "s");
        else if (ReasoningObserved) facts.Add("reasoning observed");
        if (Tools.Count > 0) facts.Add("tools " + string.Join(", ", Tools));
        return facts.Count == 0
            ? "Model response received."
            : "Model response received (" + string.Join(", ", facts) + ").";
    }
}

internal sealed class ChatGptStreamState(
    ChatGptExecution execution, Action<BrowserOperationProgress>? progress)
{
    public string? ConversationId { get; private set; }
    private bool _thinking;
    private bool _responding;

    public void OnEvent(string data)
    {
        if (string.IsNullOrWhiteSpace(data) || data == "[DONE]") return;
        JsonDocument document;
        try { document = JsonDocument.Parse(data); }
        catch (JsonException)
        {
            throw new BrowserStreamProtocolException(
                "ChatGPT stream contained malformed JSON in an ordered event.");
        }
        using (document)
        {
        var value = document.RootElement;
        if (ConversationId is null) ConversationId = FindConversationId(value);
        execution.ObserveStream(value);
        if (ChatGptJson.String(value, "type") != "message_marker") return;
        switch (ChatGptJson.String(value, "marker"))
        {
            case "cot_token" when !_thinking:
                _thinking = true;
                execution.ReasoningObserved = true;
                progress?.Invoke(new("thinking", "ChatGPT reported reasoning activity."));
                break;
            case "final_channel_token" when !_responding:
                _responding = true;
                progress?.Invoke(new("responding", "ChatGPT is generating the visible response."));
                break;
        }
        }
    }

    private static string? FindConversationId(JsonElement value)
    {
        var id = ChatGptJson.String(value, "conversation_id");
        if (id is not null) return id;
        foreach (var child in ChatGptJson.Children(value))
            if (child.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                if (FindConversationId(child) is { } found) return found;
        return null;
    }
}

internal sealed record ChatGptConversationResult(
    string Text, string ParentMessageId, string? ProjectId, string? Model,
    IReadOnlyDictionary<string, string> Files, IReadOnlyList<string> SandboxPaths);

internal static class ChatGptConversationState
{
    private const int IntervalMs = 2000;
    private const int WatchdogMs = 20000;

    public static async Task<ChatGptConversationResult> WaitAsync(
        BrowserHttpExecutor http, string conversationId, string requestMessageId,
        ChatGptExecution execution, Action<BrowserOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var endpoint = new HttpEndpoint<EmptyHttpRequest, JsonElement>(
            HttpMethod.Get, ChatGptAccountProtocol.ConversationPath(conversationId));
        var rateLimits = 0;
        var inactiveWait = 0;
        string? lastProgressKey = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement conversation;
            try
            {
                conversation = await http.CallAsync(endpoint, request: null,
                    cancellationToken: cancellationToken);
                rateLimits = 0;
            }
            catch (IntegrationHttpException error) when (error.StatusCode ==
                HttpStatusCode.TooManyRequests)
            {
                var fallback = Math.Min(30000, IntervalMs * (1 << Math.Min(rateLimits, 4)));
                var delay = Math.Max(IntervalMs, error.RetryAfterMs ?? fallback);
                rateLimits++;
                progress?.Invoke(new("rate-limited", "ChatGPT rate limited state checks; " +
                    "retrying in " + Math.Ceiling(delay / 1000d) + "s."));
                await Task.Delay(delay, cancellationToken);
                continue;
            }
            var turn = Inspect(conversation, requestMessageId);
            if (turn.Reply is not null)
            {
                execution.MergeFinal(turn.Execution);
                var model = execution.Model is null ? turn.Reply.Model
                    : ChatGptModelSelection.Id(execution.Model, execution.ThinkingEffort);
                progress?.Invoke(new("completed", execution.CompletionDetail(), turn.Analysis));
                return turn.Reply with { Model = model };
            }

            progress?.Invoke(new(turn.State, turn.Detail, turn.Analysis));
            var progressed = turn.ProgressKey is not null &&
                turn.ProgressKey != lastProgressKey;
            if (progressed) lastProgressKey = turn.ProgressKey;
            if (turn.Active || progressed) inactiveWait = 0;
            else if (inactiveWait >= WatchdogMs)
                throw new InvalidOperationException(
                    "ChatGPT showed no active generation for 20s after the native request completed.");
            await Task.Delay(IntervalMs, cancellationToken);
            if (!turn.Active) inactiveWait += IntervalMs;
        }
    }

    private sealed record TurnInspection(ChatGptConversationResult? Reply,
        ChatGptExecution Execution, bool Active, string? ProgressKey,
        string State, string Detail, string? Analysis);

    private static TurnInspection Inspect(JsonElement conversation, string requestMessageId)
    {
        var mapping = ChatGptJson.Get(conversation, "mapping");
        var current = ChatGptJson.String(conversation, "current_node");
        var progressKey = current;
        var nodeId = current;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var messages = new List<JsonElement>();
        var foundRequest = false;
        for (var count = 0; count < 10000 && nodeId is not null && visited.Add(nodeId); count++)
        {
            if (mapping.ValueKind != JsonValueKind.Object ||
                !mapping.TryGetProperty(nodeId, out var node)) break;
            var message = ChatGptJson.Get(node, "message");
            if (message.ValueKind == JsonValueKind.Object)
            {
                if (ChatGptJson.String(message, "id") == requestMessageId)
                {
                    foundRequest = true;
                    messages.Add(message);
                    break;
                }
                messages.Add(message);
            }
            nodeId = ChatGptJson.String(node, "parent");
        }

        var analysis = string.Join("\n\n", messages.Where(m =>
            ChatGptJson.String(ChatGptJson.Get(m, "author"), "role") == "assistant" &&
            ChatGptJson.String(m, "channel") == "analysis")
            .Select(ChatGptJson.MessageText).Where(x => x is not null).Reverse()).Trim();
        var analysisText = analysis.Length == 0 ? null : analysis;
        var resultExecution = new ChatGptExecution();
        foreach (var m in messages) resultExecution.ObserveMessage(m);

        if (!foundRequest)
            return new(null, resultExecution, false, null, "waiting",
                "Waiting for the submitted prompt to become the active ChatGPT turn.", null);

        var assistant = messages.Take(messages.Count - 1)
            .FirstOrDefault(IsVisibleFinal);
        if (assistant.ValueKind == JsonValueKind.Object)
        {
            // The visible final reply is authoritative; hidden or tool activity must not
            // determine its reported model or thinking effort.
            var finalMetadata = ChatGptJson.Get(assistant, "metadata");
            var finalModel = ChatGptJson.String(finalMetadata, "resolved_model_slug", "model_slug");
            if (finalModel is not null)
            {
                resultExecution.Model = finalModel;
                if (ChatGptJson.String(finalMetadata, "thinking_effort") is { } finalEffort)
                    resultExecution.ThinkingEffort = finalEffort;
            }
            else if (ChatGptJson.String(finalMetadata, "thinking_effort") is { } finalEffort)
                resultExecution.ThinkingEffort = finalEffort;
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var message in messages.Take(messages.Count - 1))
                CollectFileRefs(message, files);
            var request = messages[^1];
            var resolved = resultExecution.Model ??
                ChatGptJson.String(ChatGptJson.Get(request, "metadata"), "resolved_model_slug");
            var model = resolved is null ? null
                : ChatGptModelSelection.Id(resolved, resultExecution.ThinkingEffort);
            var text = CitedText(assistant);
            var reply = new ChatGptConversationResult(
                text,
                ChatGptJson.String(assistant, "id")!,
                ChatGptJson.String(conversation, "gizmo_id"),
                model, files, ChatGptAccountFiles.FindSandboxPaths(text));
            return new(reply, resultExecution, false, progressKey, "responding",
                "Model response completed.", analysisText);
        }

        var inProgress = messages.Take(messages.Count - 1)
            .Where(m => ChatGptJson.String(ChatGptJson.Get(m, "author"), "role") == "assistant")
            .FirstOrDefault(m => ChatGptJson.String(m, "status") == "in_progress");
        if (inProgress.ValueKind == JsonValueKind.Object)
        {
            var meta = ChatGptJson.Get(inProgress, "metadata");
            var thinking = ChatGptJson.String(inProgress, "channel") == "analysis" ||
                ChatGptJson.String(ChatGptJson.Get(inProgress, "content"), "content_type") ==
                    "reasoning_recap" ||
                ChatGptJson.String(meta, "reasoning_status") is not null ||
                ChatGptJson.Number(meta, "reasoning_start_time") is not null ||
                ChatGptJson.Number(meta, "reasoning_end_time") is not null;
            return new(null, resultExecution, true, progressKey,
                thinking ? "thinking" : "responding",
                thinking ? "Model is thinking." : "Model is generating a response.", analysisText);
        }

        var assistantSeen = messages.Take(messages.Count - 1).Any(m =>
            ChatGptJson.String(ChatGptJson.Get(m, "author"), "role") == "assistant");
        return new(null, resultExecution, false, progressKey, "waiting",
            assistantSeen
                ? "Model activity was observed, but no active generation is currently detected."
                : "Prompt is present, but no model activity has started yet.",
            analysisText);
    }

    private static bool IsVisibleFinal(JsonElement message)
    {
        if (ChatGptJson.String(ChatGptJson.Get(message, "author"), "role") != "assistant" ||
            ChatGptJson.String(message, "status") == "in_progress")
            return false;
        var metadata = ChatGptJson.Get(message, "metadata");
        if (ChatGptJson.True(metadata, "is_visually_hidden_from_conversation") ||
            ChatGptJson.Number(message, "weight") == 0)
            return false;
        var channel = ChatGptJson.String(message, "channel");
        if (channel is not null && !channel.Equals("final", StringComparison.OrdinalIgnoreCase))
            return false;
        var recipient = ChatGptJson.String(message, "recipient");
        if (recipient is not null && !recipient.Equals("all", StringComparison.OrdinalIgnoreCase))
            return false;
        var type = ChatGptJson.String(ChatGptJson.Get(message, "content"), "content_type");
        return (type is null or "text" or "multimodal_text") &&
            ChatGptJson.MessageText(message) is not null;
    }

    private static string CitedText(JsonElement message)
    {
        var text = ChatGptJson.MessageText(message) ?? "";
        var refs = ChatGptJson.Get(ChatGptJson.Get(message, "metadata"), "content_references");
        if (refs.ValueKind == JsonValueKind.Array)
            foreach (var reference in refs.EnumerateArray())
            {
                var marker = ChatGptJson.String(reference, "matched_text");
                var alt = ChatGptJson.String(reference, "alt");
                if (marker is not null && marker.IndexOfAny(['\uE200','\uE201','\uE202','\uE203']) >= 0 &&
                    alt is not null)
                    text = text.Replace(marker, alt, StringComparison.Ordinal);
            }
        return Regex.Replace(Regex.Replace(text,
                "\uE200cite\uE202[^\uE201]*\uE201", "[citation unavailable]"),
            "\uE200memcite\uE201", "").Trim();
    }

    private static void CollectFileRefs(JsonElement value, Dictionary<string, string> refs)
    {
        var pointer = ChatGptJson.String(value, "asset_pointer");
        if (pointer is not null)
        {
            var match = Regex.Match(pointer, @"^(?:file-service|sediment)://(.+)$");
            if (match.Success) refs[match.Groups[1].Value] =
                ChatGptJson.String(value, "name") ?? "download";
        }
        var attachments = ChatGptJson.Get(value, "attachments");
        if (attachments.ValueKind == JsonValueKind.Array)
            foreach (var attachment in attachments.EnumerateArray())
                if (ChatGptJson.String(attachment, "id") is { } id)
                    refs[id] = ChatGptJson.String(attachment, "name") ?? "download";
        foreach (var child in ChatGptJson.Children(value))
            if (child.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                CollectFileRefs(child, refs);
    }
}
