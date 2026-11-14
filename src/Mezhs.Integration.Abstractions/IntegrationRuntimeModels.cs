using System.Text.Json;

namespace Mezhs.Integrations;

// One context per operation/chat; the connection-scoped integration never owns a mutable ChatId.
// History is queried only by providers that actually need it.
public sealed class ChatContext
{
    private readonly Func<int?, int?, bool, CancellationToken,
        Task<IReadOnlyList<ChatMessage>>> _getHistory;

    public ChatContext(
        string id,
        string connectionId,
        string? providerId,
        Func<int?, int?, bool, CancellationToken, Task<IReadOnlyList<ChatMessage>>> getHistory,
        string? remoteParentMessageId = null,
        string? remoteChatUrl = null,
        bool restoreConversation = false,
        Action<IntegrationActivity>? reportActivity = null)
    {
        Id = id;
        ConnectionId = connectionId;
        ProviderId = providerId;
        RemoteParentMessageId = remoteParentMessageId;
        RemoteChatUrl = remoteChatUrl;
        RestoreConversation = restoreConversation;
        ReportActivity = reportActivity;
        _getHistory = getHistory ?? throw new ArgumentNullException(nameof(getHistory));
    }

    public string Id { get; }
    public string ConnectionId { get; }
    public string? ProviderId { get; }
    // The current ChatGPT continuation still requires a remote parent message in addition
    // to the conversation ID. These are per-chat+connection values, not integration state.
    public string? RemoteParentMessageId { get; }
    public string? RemoteChatUrl { get; }
    public bool RestoreConversation { get; }
    public Action<IntegrationActivity>? ReportActivity { get; }

    public Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(
        int? limit = null,
        int? offset = null,
        bool fromStart = true,
        CancellationToken cancellationToken = default)
    {
        if (limit < 0)
            throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        cancellationToken.ThrowIfCancellationRequested();
        return _getHistory(limit, offset, fromStart, cancellationToken);
    }
}

public sealed record ChatMessage(
    string MessageId,
    string ConnectionId,
    string Role,
    string Content,
    bool Completed,
    DateTimeOffset CreatedAt,
    string? Model = null);

public sealed record PromptRequest(
    string MessageId,
    string Text,
    string? Model,
    IReadOnlyList<IntegrationInputFile> Files);

public sealed record PromptResult(
    string Text,
    string MessageId,
    string Model,
    string? ProviderId = null,
    IReadOnlyList<IntegrationOutputFile>? Files = null,
    string? RemoteParentMessageId = null,
    string? RemoteChatUrl = null);

// No generic persistent state, feature-flag matrix, or second browser/session owner.
public abstract class IntegrationBase(
    IntegrationConnection connection,
    IIntegrationHost host) : IAsyncDisposable
{
    public IntegrationConnection Connection { get; } = connection;
    protected IIntegrationHost IntegrationHost { get; } = host;
    public virtual bool AllowFiles => false;

    // Called before Core persists the outgoing message. Providers can supply IDs whose
    // format is required by their protocol, rather than changing stored IDs after send.
    public virtual string CreateMessageId() => "msg_" + Guid.NewGuid().ToString("N");

    public abstract Task<PromptResult> PromptAsync(
        ChatContext chat,
        PromptRequest request,
        CancellationToken cancellationToken = default);

    public virtual Task<IReadOnlyList<IntegrationModel>> GetModelsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<IntegrationModel>>([new("default", "Default")]);

    protected virtual Task SetModelAsync(
        ChatContext chat,
        string model,
        CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(model) ||
        string.Equals(model, "default", StringComparison.OrdinalIgnoreCase)
            ? Task.CompletedTask
            : Task.FromException(new NotSupportedException(
                $"Connection '{Connection.Id}' does not support model '{model}'."));

    public virtual Task LoginAsync(CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException(
            $"Connection '{Connection.Id}' does not support account login."));

    public virtual Task OpenBrowserAsync(CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException(
            $"Connection '{Connection.Id}' does not expose an account browser."));

    public virtual Task<JsonElement> DiagnoseAsync(
        string operation,
        JsonElement arguments,
        CancellationToken cancellationToken = default) =>
        Task.FromException<JsonElement>(new NotSupportedException(
            $"Connection '{Connection.Id}' does not expose browser diagnostics."));

    // An operation with uncertain remote submission must never be implicitly replayed.
    protected virtual Task<ErrorAction> HandleErrorAsync(
        IntegrationError error,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ErrorAction.Throw);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed record IntegrationError(
    Exception Exception,
    string Operation,
    int Attempt,
    bool SubmissionMayHaveOccurred);

public enum ErrorAction
{
    Throw,
    RetryIfSafe
}

// Deliberately minimal until an actual API or local-model integration requires more.
public abstract class ApiIntegrationBase(
    IntegrationConnection connection, IIntegrationHost host) : IntegrationBase(connection, host);

public abstract class LocalModelIntegrationBase(
    IntegrationConnection connection, IIntegrationHost host) : IntegrationBase(connection, host);
