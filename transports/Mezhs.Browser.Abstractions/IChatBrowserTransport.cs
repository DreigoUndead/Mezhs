using System.Text.Json;

namespace Mezhs.Browser;

public sealed class BrowserAuthorizationRequiredException(string message)
    : InvalidOperationException(message);
public sealed class BrowserStreamProtocolException(string message)
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

    // Ordered raw protocol events, delivered while the browser operation is running.
    // A transport without event support must fail, never silently drop events.
    Task<TResult> InvokeWithEventsAsync<TResult>(
        string operation,
        object? arguments,
        Action<JsonElement> reportEvent,
        Action<BrowserOperationProgress>? reportProgress = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This browser transport does not support ordered protocol events.");

    Task ShowAsync(CancellationToken cancellationToken = default);

    // The host is shared; this view binds one operation to a local conversation.
    IChatBrowserTransport ForContext(string chatId, bool ephemeral = false) => this;

    // Stop only this chat/request in the already-running host. Never create a session
    // to stop a turn and never treat a transport abort as remote confirmation.
    Task<BrowserStopOutcome> StopTurnAsync(string chatId, string messageId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(BrowserStopOutcome.Unsupported);

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

public enum BrowserStopOutcome { NotActive, Unsupported, Attempted, ProviderConfirmed }
