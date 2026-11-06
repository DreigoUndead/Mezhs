using Mezhs.Agent.Persistence;
using Mezhs.Services;

namespace Mezhs.Agent.Services;

// Core never knows about Agent or Executor; only Agent host coordinates
// references in its own store before deleting the Core chat records.
public sealed class AgentChatDeletionHandler(
    ChatStore chats,
    AgentStore agents,
    AgentWorker worker) : IChatDeletionHandler
{
    public async Task<IReadOnlyList<string>> DeleteChatsAsync(
        IEnumerable<string> chatIds,
        CancellationToken cancellationToken = default)
    {
        var entries = chatIds.ToArray();
        if (entries.Length == 0)
            throw new RequestValidationException("At least one chatId is required.");
        if (entries.Any(string.IsNullOrWhiteSpace))
            throw new RequestValidationException("chatIds cannot contain empty chat IDs.");
        var requested = entries.Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        string[] ids;
        lock (chats.DeletionSync)
        {
            ids = requested.Select(id => chats.GetChat(id)?.ChatId
                ?? throw new ResourceNotFoundException($"Chat '{id}' was not found.")).ToArray();
            worker.BeginChatDeletion(ids);
        }

        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stop.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                // Agent reasoning joins, but Executor shells keep running independently.
                await worker.WaitForChatStopAsync(ids, stop.Token);
                while (true)
                {
                    bool active;
                    lock (chats.DeletionSync)
                        active = ids.Any(id => chats.GetRuntimeState(id).ActiveMessage is not null);
                    if (!active) break;
                    await Task.Delay(50, stop.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RequestValidationException(
                    "Agent or chat processing did not stop in time. No chat data was deleted.");
            }

            lock (chats.DeletionSync)
            {
                if (ids.Any(id => chats.GetRuntimeState(id).ActiveMessage is not null))
                    throw new RequestValidationException(
                        "Chat processing resumed before deletion. No chat data was deleted.");
                agents.RemoveChatData(ids); // Agent SQLite transaction first.
                chats.DeleteChats(ids);      // Core SQLite transaction second.
                return ids;
            }
        }
        finally
        {
            worker.EndChatDeletion(ids);
        }
    }
}
