namespace Mezhs.Services;

public interface IChatDeletionHandler
{
    Task<IReadOnlyList<string>> DeleteChatsAsync(
        IEnumerable<string> chatIds,
        CancellationToken cancellationToken = default);
}

public sealed class ChatDeletionHandler(ChatStore chats) : IChatDeletionHandler
{
    public Task<IReadOnlyList<string>> DeleteChatsAsync(
        IEnumerable<string> chatIds,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(chats.DeleteChats(chatIds));
}
