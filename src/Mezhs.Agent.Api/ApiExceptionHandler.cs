using Mezhs.Executor;
using Microsoft.AspNetCore.Diagnostics;

namespace Mezhs.Agent;

public sealed class AgentApiExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var statusCode = exception switch
        {
            ExecutionNotFoundException => StatusCodes.Status404NotFound,
            AgentCapacityExceededException => StatusCodes.Status429TooManyRequests,
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
