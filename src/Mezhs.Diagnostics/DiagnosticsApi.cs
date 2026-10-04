using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using Mezhs.Services;
using Mezhs.Integrations;

namespace Mezhs.Diagnostics;

public static class DiagnosticsApi
{
    public static WebApplication MapMezhsDiagnostics(this WebApplication app)
    {
        app.MapPost(
            "/v1/diagnostics/connections/{connectionId}/browser/{operation}",
            async (
                string connectionId,
                string operation,
                JsonElement? arguments,
                IntegrationRegistry integrations,
                CancellationToken cancellationToken) =>
            {
                if (!integrations.TryGet(connectionId, out var integration))
                    return Results.NotFound(new
                    {
                        error = $"Connection '{connectionId}' was not found."
                    });
                if (integration.Diagnostics is null)
                    return Results.BadRequest(new
                    {
                        error = $"Connection '{connectionId}' does not expose live diagnostics."
                    });

                try
                {
                    var result = await integration.Diagnostics.InvokeAsync(
                        operation,
                        arguments ?? JsonSerializer.SerializeToElement(new { }),
                        cancellationToken);
                    return Results.Ok(result);
                }
                catch (IntegrationAuthorizationRequiredException ex)
                {
                    return Results.Json(
                        new { error = ex.Message },
                        statusCode: StatusCodes.Status401Unauthorized);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

        return app;
    }
}
