using System.Net.Http;
using System.Text.Json.Serialization;
using Mezhs.Integrations.Browser;

namespace Mezhs.Integrations.ChatGpt;

public sealed record EmptyHttpRequest;

public sealed record ChatGptModelCatalogProbe(
    IReadOnlyList<ChatGptModelVersionProbe>? Versions,
    IReadOnlyList<ChatGptModelProbe>? Models)
{
    // Mirror the provider's existing picker semantics without duplicating HTTP/JSON
    // handling in JavaScript. Preserve native model+effort identifiers and ordering.
    public IReadOnlyList<IntegrationModel> ToModels()
    {
        var result = new List<IntegrationModel>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? id, string? name)
        {
            id = id?.Trim();
            name = name?.Trim();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || !ids.Add(id)) return;
            result.Add(new IntegrationModel(id, name));
        }

        foreach (var version in Versions ?? [])
        {
            if (version.Enabled == false) continue;
            var versionName = FirstNonempty(version.DisplayName, version.DisplayText);
            if (version.IntelligencePresets is { Count: > 0 } presets)
            {
                foreach (var preset in presets)
                {
                    if (preset.PresetType != "available" || preset.Enabled == false) continue;
                    var model = preset.ModelSlug?.Trim();
                    var effort = preset.ThinkingEffort?.Trim();
                    var id = string.IsNullOrEmpty(effort) ? model : model + "::thinking-effort=" + effort;
                    var title = FirstNonempty(preset.SelectedDisplayTitle, preset.Title);
                    Add(id, string.Join(" · ", new[] { versionName, title }
                        .Where(name => !string.IsNullOrEmpty(name))));
                }
            }
            else
            {
                var model = version.Slugs?.Select(s => s?.Trim())
                    .FirstOrDefault(s => !string.IsNullOrEmpty(s));
                Add(model, string.IsNullOrEmpty(versionName) ? model : versionName);
            }
        }
        if (result.Count > 0) return result;

        foreach (var model in Models ?? [])
            Add(FirstNonempty(model.Slug, model.Id),
                FirstNonempty(model.Title, model.DisplayName, model.Name, model.Slug, model.Id));
        return result;
    }

    public IReadOnlyList<IntegrationModel> ToSelectableModels() =>
        [new IntegrationModel("default", "Default"), ..ToModels()
            .Where(model => !string.IsNullOrWhiteSpace(model.Id) &&
                            !string.IsNullOrWhiteSpace(model.Name) &&
                            !string.Equals(model.Id, "default", StringComparison.OrdinalIgnoreCase))];

    private static string FirstNonempty(params string?[] values) =>
        values.Select(value => value?.Trim())
            .FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? "";
}

public sealed record ChatGptModelVersionProbe(
    string? Id,
    [property: JsonPropertyName("display_text_for_intelligence")] string? DisplayName,
    [property: JsonPropertyName("display_text")] string? DisplayText = null,
    bool? Enabled = null,
    IReadOnlyList<string>? Slugs = null,
    [property: JsonPropertyName("intelligence_presets")]
    IReadOnlyList<ChatGptModelPresetProbe>? IntelligencePresets = null);

public sealed record ChatGptModelPresetProbe(
    [property: JsonPropertyName("preset_type")] string? PresetType,
    bool? Enabled,
    [property: JsonPropertyName("model_slug")] string? ModelSlug,
    [property: JsonPropertyName("thinking_effort")] string? ThinkingEffort,
    [property: JsonPropertyName("selected_display_title")] string? SelectedDisplayTitle,
    string? Title);

public sealed record ChatGptModelProbe(
    string? Slug, string? Title,
    string? Id = null,
    [property: JsonPropertyName("display_name")] string? DisplayName = null,
    string? Name = null);

public sealed record ChatGptProjectsPage(
    IReadOnlyList<ChatGptProjectItem>? Items,
    string? Cursor);

public sealed record ChatGptProjectItem(ChatGptProjectNode? Gizmo);

public sealed record ChatGptProjectNode(
    string? Id = null,
    ChatGptProjectDisplay? Display = null,
    ChatGptProjectNode? Gizmo = null);

public sealed record ChatGptProjectDisplay(string? Name);

internal static class ChatGptTypedHttpEndpoints
{
    internal static readonly HttpEndpoint<EmptyHttpRequest, ChatGptModelCatalogProbe> Models =
        new(HttpMethod.Get, "/backend-api/models?history_and_training_disabled=false");

    internal static HttpEndpoint<EmptyHttpRequest, ChatGptProjectsPage> Projects(string? cursor) =>
        new(HttpMethod.Get, "/backend-api/gizmos/snorlax/sidebar?conversations_per_gizmo=0" +
            (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
}
