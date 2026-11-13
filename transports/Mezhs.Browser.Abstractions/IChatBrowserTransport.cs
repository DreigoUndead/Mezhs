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

    // The host is shared; this view binds one operation to a local conversation.
    IChatBrowserTransport ForContext(string chatId, bool ephemeral = false) => this;

    Task ReleaseContextAsync(string chatId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    Task EnsureAuthorizedAsync(bool interactive, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed record BrowserTransportOptions(
    string ProfileDirectory,
    string ModulePath,
    bool ShowBrowser = false,
    bool RequireAuthorization = false,
    bool Guest = false);

public sealed record BrowserOperationProgress(
    string State,
    string? Detail = null,
    string? Analysis = null);

public sealed record BrowserArtifact(
    string Url,
    string Name,
    string? ContentType = null,
    string? LocalPath = null);
