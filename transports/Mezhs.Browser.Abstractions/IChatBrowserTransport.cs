namespace Mezhs.Browser;

public sealed class BrowserAuthorizationRequiredException(string message)
    : InvalidOperationException(message);

public interface IChatBrowserTransport : IAsyncDisposable
{
    string Name { get; }

    Task InitializeAsync(
        BrowserTransportOptions options,
        CancellationToken cancellationToken = default);

    Task<TResult> InvokeAsync<TResult>(
        string operation,
        object? arguments = null,
        CancellationToken cancellationToken = default);

    Task<TResult> InvokeWithProgressAsync<TResult>(
        string operation,
        object? arguments,
        Action<BrowserOperationProgress>? reportProgress,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<TResult>(operation, arguments, cancellationToken);

    Task ShowAsync(CancellationToken cancellationToken = default);
}

public sealed record BrowserTransportOptions(
    string ProfileDirectory,
    string ModulePath,
    bool ShowBrowser = false,
    bool RequireAuthorization = false);

public sealed record BrowserOperationProgress(
    string State,
    string? Detail = null,
    string? Analysis = null);

public sealed record BrowserArtifact(
    string Url,
    string Name,
    string? ContentType = null,
    string? LocalPath = null);
