namespace Mezhs.Browser;

public sealed class BrowserAuthorizationRequiredException(string message)
    : InvalidOperationException(message);

public interface IChatBrowserTransport : IAsyncDisposable
{
    string Name { get; }

    Task InitializeAsync(
        BrowserTransportOptions options,
        CancellationToken cancellationToken = default);

    Task<ChatTransportResponse> SendPromptAsync(
        BrowserPromptRequest request,
        CancellationToken cancellationToken = default);

    Task<TResult> InvokeAsync<TResult>(
        string operation,
        object? arguments = null,
        CancellationToken cancellationToken = default);

    Task ShowAsync(CancellationToken cancellationToken = default);
}

public sealed record BrowserTransportOptions(
    string ProfileDirectory,
    string ModulePath,
    bool ShowBrowser = false,
    bool RequireAuthorization = false);

public sealed record BrowserPromptRequest(
    string Prompt,
    bool NewChat = false,
    string? ChatUrl = null,
    IReadOnlyList<string>? FilePaths = null);

public sealed record ChatTransportResponse(
    bool Ok,
    string Text = "",
    string? Error = null,
    string? ChatUrl = null,
    IReadOnlyList<BrowserArtifact>? Artifacts = null);

public sealed record BrowserArtifact(
    string Url,
    string Name,
    string? ContentType = null,
    string? LocalPath = null);
