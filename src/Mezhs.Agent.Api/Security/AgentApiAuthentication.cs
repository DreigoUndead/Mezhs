using System.Security.Cryptography;
using System.Text;

namespace Mezhs.Agent.Security;

public static class AgentApiAuthentication
{
    public const string ApiKeyEnvironmentVariable = "MEZHS_AGENT_API_KEY";
    public const string RequesterHeader = "X-MEZHS-Requester";

    public static string RequireApiKey()
    {
        var value = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"{ApiKeyEnvironmentVariable} must be set before starting MEŽS Agent.");
        return value;
    }

    public static bool IsAuthorized(HttpRequest request, string expectedApiKey)
    {
        var authorization = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var supplied = authorization[prefix.Length..].Trim();
        if (supplied.Length == 0)
            return false;

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expectedApiKey));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }

    public static string GetRequester(HttpContext context)
    {
        var requester = context.Request.Headers[RequesterHeader].ToString().Trim();
        if (requester.Length == 0)
            return "authenticated-api";
        if (requester.Length > 128 || requester.Any(char.IsControl))
            throw new RequestValidationException($"{RequesterHeader} must be at most 128 printable characters.");
        return requester;
    }
}
