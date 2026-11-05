namespace Mezhs.Services;

public interface IChatDeletionHandler
{
    IReadOnlyList<string> DeleteChats(IEnumerable<string> chatIds);
}

public sealed class ChatDeletionHandler(ChatStore chats) : IChatDeletionHandler
{
    public IReadOnlyList<string> DeleteChats(IEnumerable<string> chatIds) => chats.DeleteChats(chatIds);
}
