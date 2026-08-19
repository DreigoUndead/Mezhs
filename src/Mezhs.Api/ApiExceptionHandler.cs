using Microsoft.AspNetCore.Diagnostics;

namespace Mezhs;

public sealed class ApiExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var statusCode = exception switch
        {
            RequestValidationException => StatusCodes.Status400BadRequest,
            ResourceNotFoundException => StatusCodes.Status404NotFound,
            _ => (int?)null
        };

        if (statusCode is null)
            return false;

        httpContext.Response.StatusCode = statusCode.Value;
        await httpContext.Response.WriteAsJsonAsync(
            new { error = exception.Message },
            cancellationToken);
        return true;
    }
}
