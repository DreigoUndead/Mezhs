using System.Text.Json;
using Mezhs.Browser;

namespace Mezhs.Integrations.Browser;

// Keep browser diagnostic allowlisting at the authenticated session boundary;
// the public API also checks loopback + origin before invoking this helper.
internal static class BrowserDiagnostics
{
    private static readonly HashSet<string> Operations = new(StringComparer.Ordinal)
    {
        "snapshot",
        "inspectPoint",
        "click",
        "type",
        "key",
        "screenshot",
        "networkTrace",
        "inspectRspackExports",
        "show"
    };

    internal static Task<JsonElement> InvokeAsync(
        BrowserAccountSession session,
        string operation,
        JsonElement arguments,
        CancellationToken cancellationToken)
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
