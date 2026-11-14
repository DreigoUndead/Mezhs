using System.Net.Http;
using System.Text.Json.Serialization;
using Mezhs.Integrations.Browser;

namespace Mezhs.Integrations.ChatGpt;

public sealed record EmptyHttpRequest;
public sealed record ChatGptModelCatalogProbe(
    IReadOnlyList<ChatGptModelVersionProbe>? Versions,
    IReadOnlyList<ChatGptModelProbe>? Models);

public sealed record ChatGptModelVersionProbe(
    string? Id,
    [property: JsonPropertyName("display_text_for_intelligence")] string? DisplayName);

public sealed record ChatGptModelProbe(string? Slug, string? Title);

internal static class ChatGptTypedHttpEndpoints
{
    // Read-only endpoint avoids a prompt-submit replay hazard during the gate.
    internal static readonly HttpEndpoint<EmptyHttpRequest, ChatGptModelCatalogProbe> Models =
        new(HttpMethod.Get, "/backend-api/models?history_and_training_disabled=false");
}
