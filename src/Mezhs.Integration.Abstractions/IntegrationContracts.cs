namespace Mezhs.Integrations;

public sealed class IntegrationLimits
{
    public int MaxParallel { get; set; } = 1;
    public int MinInterval { get; set; }
}

public sealed record IntegrationConnection(
    string Id,
    string Name,
    string Type,
    IReadOnlyDictionary<string, string?> Settings,
    IntegrationLimits? Limits = null)
{
    public string? GetSetting(string name) =>
        Settings.TryGetValue(name, out var value) ? value : null;
}

public sealed record IntegrationModel(string? Id, string Name);

public sealed class IntegrationAuthorizationRequiredException(string message)
    : InvalidOperationException(message);

public interface IIntegrationHost
{
    string GetConnectionRoot(string connectionId);
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class IntegrationAttribute(string type) : Attribute
{
    public string Type { get; } = type;
}

public interface IConversationContextOwner
{
    Task RetainConversationAsync(string chatId);
    Task ReleaseConversationAsync(string chatId);
}

public sealed record IntegrationInputFile(
    string FileId,
    string Path,
    string Name,
    string ContentType,
    long Size);

public sealed record IntegrationOutputFile(
    string Path,
    string Name,
    string ContentType,
    bool DeleteAfterImport = true);

public sealed record IntegrationActivity(
    string State,
    string? Detail = null,
    string? Analysis = null);
