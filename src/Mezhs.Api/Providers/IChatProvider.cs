using Mezhs.Configuration;
using Mezhs.Models;

namespace Mezhs.Providers;

public interface IChatProvider : IAsyncDisposable
{
    ConnectionOptions Connection { get; }
    string Name { get; }
    bool RequiresLogin { get; }
    ProviderCapabilities Capabilities { get; }

    Task InitializeAsync(bool showBrowser, CancellationToken cancellationToken = default);
    Task<ProviderChat> GetChatAsync(ChatRecord chat, CancellationToken cancellationToken = default);
    Task<ProviderChat> CreateChatAsync(ChatRecord chat, CancellationToken cancellationToken = default);
    Task<ProviderSendResult> SendMessageAsync(
        ProviderSendContext context,
        CancellationToken cancellationToken = default);
    Task<ProviderUploadedFile> UploadFileAsync(
        ProviderInputFile file,
        CancellationToken cancellationToken = default);
    Task<ProviderOutputFile> DownloadFileAsync(
        ProviderDownloadContext context,
        CancellationToken cancellationToken = default);
    Task StopGenerationAsync(
        string chatId,
        string? requestId = null,
        CancellationToken cancellationToken = default);
}

public abstract class ChatProviderBase(ConnectionOptions connection) : IChatProvider
{
    public ConnectionOptions Connection { get; } = connection;
    public abstract string Name { get; }
    public virtual bool RequiresLogin => false;
    public virtual ProviderCapabilities Capabilities => new();

    public virtual Task InitializeAsync(bool showBrowser, CancellationToken cancellationToken = default)
    {
        if (RequiresLogin)
            throw new NotSupportedException($"Provider '{Connection.Provider}' does not implement initialization.");
        return Task.CompletedTask;
    }

    public virtual Task<ProviderChat> GetChatAsync(
        ChatRecord chat,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ProviderChat.From(chat));

    public virtual Task<ProviderChat> CreateChatAsync(
        ChatRecord chat,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ProviderChat.From(chat));

    public abstract Task<ProviderSendResult> SendMessageAsync(
        ProviderSendContext context,
        CancellationToken cancellationToken = default);

    public virtual Task<ProviderUploadedFile> UploadFileAsync(
        ProviderInputFile file,
        CancellationToken cancellationToken = default)
    {
        if (!Capabilities.FileInput)
            throw new NotSupportedException($"Provider '{Connection.Provider}' does not support file input.");
        return Task.FromResult(new ProviderUploadedFile(file.FileId));
    }

    public virtual Task<ProviderOutputFile> DownloadFileAsync(
        ProviderDownloadContext context,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"Provider '{Connection.Provider}' does not support direct file download.");

    public virtual Task StopGenerationAsync(
        string chatId,
        string? requestId = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed record ProviderChat(
    string ChatId,
    string? RemoteChatUrl = null,
    string? RemoteConversationId = null,
    string? RemoteParentMessageId = null)
{
    public static ProviderChat From(ChatRecord chat) => new(
        chat.ChatId,
        chat.RemoteChatUrl,
        chat.RemoteConversationId,
        chat.RemoteParentMessageId);
}

public sealed record ProviderSendContext(
    ChatRecord Chat,
    StoredMessage Message,
    IReadOnlyList<StoredMessage> History,
    IReadOnlyList<ProviderInputFile> Files);

public sealed record ProviderCapabilities(
    bool FileInput = false,
    bool ImageInput = false,
    bool FileOutput = false,
    bool ImageOutput = false,
    bool StopGeneration = false);

public sealed record ProviderInputFile(
    string FileId,
    string Path,
    string Name,
    string ContentType,
    long Size);

public sealed record ProviderUploadedFile(string FileId, string? RemoteFileId = null);

public sealed record ProviderDownloadContext(
    string ChatId,
    string FileId,
    string? RemoteFileId = null);

public sealed record ProviderOutputFile(
    string Path,
    string Name,
    string ContentType,
    bool DeleteAfterImport = true);

public sealed record ProviderSendResult(
    string Text,
    string? RemoteChatUrl = null,
    string? RemoteConversationId = null,
    string? RemoteParentMessageId = null,
    IReadOnlyList<ProviderOutputFile>? Files = null);
