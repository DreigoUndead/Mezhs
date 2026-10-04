using System.Text.Json;
using Mezhs.Browser;

namespace Mezhs.Integrations.Browser;

public sealed class BrowserDiagnosticsModule(BrowserAccountSession session)
    : IIntegrationDiagnosticsModule
{
    private static readonly HashSet<string> Operations =
    [
        "snapshot",
        "inspectPoint",
        "click",
        "type",
        "key",
        "screenshot",
        "show"
    ];

    public Task<JsonElement> InvokeAsync(
        string operation,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var normalized = operation?.Trim() ?? string.Empty;
        if (!Operations.Contains(normalized))
            throw new InvalidOperationException(
                $"Browser diagnostic operation '{operation}' is not supported.");

        if (normalized == "show")
        {
            return session.UseAuthorizedAsync(async (transport, token) =>
            {
                await transport.ShowAsync(token);
                return JsonSerializer.SerializeToElement(new { shown = true });
            }, cancellationToken);
        }

        return session.UseAuthorizedAsync(
            (transport, token) => transport.InvokeAsync<JsonElement>(
                $"$diagnostics/{normalized}",
                arguments,
                token),
            cancellationToken);
    }
}
