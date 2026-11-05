using Mezhs.Agent.Persistence;
using Mezhs.Executor;
using Mezhs.Services;

namespace Mezhs.Agent.Services;

// The Agent host replaces only the normal API's deletion handler. All other
// ChatStore and API behavior is shared with the standalone API.
public sealed class AgentChatDeletionHandler(
    ChatStore chats,
    AgentStore agents,
    ExecutorService executor) : IChatDeletionHandler
{
    public IReadOnlyList<string> DeleteChats(IEnumerable<string> chatIds)
    {
        var entries = chatIds.ToArray();
        if (entries.Length == 0)
            throw new RequestValidationException("At least one chatId is required.");
        if (entries.Any(string.IsNullOrWhiteSpace))
            throw new RequestValidationException("chatIds cannot contain empty chat IDs.");

        var requested = entries.Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (chats.DeletionSync)
        {
            // Resolve canonical IDs while holding the same gate as message admission.
            var ids = requested.Select(id => chats.GetChat(id)?.ChatId
                ?? throw new ResourceNotFoundException($"Chat '{id}' was not found.")).ToArray();
            agents.BeginChatDeletion(ids);
            var executorBlocked = false;
            var committed = false;
            try
            {
                // Both persisted guards precede validation. An Agent/Executor
                // admission racing with a guard either becomes visible to the
                // validation query, or is denied by its own SQLite store.
                executor.BlockChats(ids);
                executorBlocked = true;
                foreach (var id in ids)
                {
                    if (chats.GetRuntimeState(id).ActiveMessage is not null ||
                        agents.HasActiveChatExecutions(id) ||
                        executor.HasActiveChatWork(id))
                        throw new RequestValidationException(
                            $"Chat '{id}' has ongoing work. Finish or cancel it before deleting.");
                }

                // Commit intent before touching the core file store. The durable
                // guards survive crashes; restart reconciliation resumes cleanup.
                agents.CommitChatDeletion(ids);
                committed = true;
                chats.DeleteChats(ids);
                agents.RemoveChatData(ids);
                return ids;
            }
            catch
            {
                if (!committed)
                {
                    if (executorBlocked)
                        executor.UnblockChats(ids);
                    agents.ReleaseChatDeletion(ids);
                }
                throw;
            }
        }
    }

    public void Reconcile()
    {
        lock (chats.DeletionSync)
        {
            var guards = agents.GetDeletionGuards();
            var preparing = guards.Where(guard => guard.State == "Preparing")
                .Select(guard => guard.ChatId).ToArray();
            if (preparing.Length > 0)
            {
                // No destructive operation can precede the Deleting state.
                executor.UnblockChats(preparing);
                agents.ReleaseChatDeletion(preparing);
            }

            var deleting = guards.Where(guard => guard.State == "Deleting")
                .Select(guard => guard.ChatId).ToArray();
            if (deleting.Length == 0)
                return;

            executor.BlockChats(deleting);
            var remaining = deleting.Where(id => chats.GetChat(id) is not null).ToArray();
            if (remaining.Length > 0)
                chats.DeleteChats(remaining);
            agents.RemoveChatData(deleting);
            // Leave both tombstones: retained Executor audit rows must never
            // be restarted, including from a separate Executor process.
        }
    }
}
