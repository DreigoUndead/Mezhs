using Mezhs.Agent.Commands;
using Mezhs.Agent.Configuration;
using Microsoft.AspNetCore.StaticFiles;
using Mezhs.Agent.Models;
using Mezhs.Agent.Persistence;
using Mezhs.Api.Contracts;
using Mezhs.Services;

namespace Mezhs.Agent.Services;

// An ATTACH command uploads into the normal MEŽS file store, but does not send a message.
// Only the owning AgentWorker may consume its execution-scoped staged IDs.
public sealed class AgentAttachmentStaging(
    AgentStore store,
    FileStore files,
    IntegrationRegistry integrations,
    AgentOptions options)
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public async Task<Result> StageAsync(
        Mezhs.Agent.Commands.ExecutionContext context,
        string body,
        CancellationToken cancellationToken)
    {
        const string name = "ATTACH";
        var execution = context.ParentExecution;
        try
        {
            var existing = store.GetStagedAttachments(execution.ExecutionId)
                .FirstOrDefault(item =>
                    item.TriggerMessageId == context.TriggerMessageId &&
                    item.CommandIndex == context.CommandIndex);
            if (existing is not null)
                return new Result(name, null, true, 0,
                    $"Already staged: {string.Join(", ", existing.FileIds)}", null);

            var paths = body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(path => path.Trim().Trim('"'))
                .Where(path => path.Length > 0)
                .ToArray();
            if (paths.Length == 0)
                throw new RequestValidationException("ATTACH needs at least one local file path.");

            var capabilities = integrations.Get(execution.ConnectionId).Capabilities;
            if (!capabilities.FileInput)
                throw new RequestValidationException(
                    $"Connection '{execution.ConnectionId}' does not support file input.");

            // Validate *all* paths before importing any file.
            var imports = paths.Select(path =>
            {
                var fullPath = Path.GetFullPath(path, options.Workspace);
                if (!File.Exists(fullPath))
                    throw new FileNotFoundException("Attachment file does not exist.", fullPath);
                if (!ContentTypes.TryGetContentType(fullPath, out var contentType))
                    contentType = "application/octet-stream";
                if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                    !capabilities.ImageInput)
                    throw new RequestValidationException(
                        $"Connection '{execution.ConnectionId}' does not support image input.");
                return (Path: fullPath, ContentType: contentType);
            }).ToArray();

            var ids = new List<string>();
            foreach (var item in imports)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var uploaded = await files.ImportAsync(
                    execution.ConnectionId,
                    item.Path,
                    Path.GetFileName(item.Path),
                    item.ContentType,
                    FileSource.User,
                    cancellationToken);
                ids.Add(uploaded.FileId);
            }

            cancellationToken.ThrowIfCancellationRequested();
            store.StageAttachments(execution.ExecutionId,
                context.TriggerMessageId, context.CommandIndex, ids);
            return new Result(name, null, true, 0,
              $"Staged for this execution's next message: {string.Join(", ", ids)}", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Result(name, null, false, null, null, ex.Message);
        }
    }

    public IReadOnlyList<string> Pending(string executionId) =>
        store.GetStagedAttachments(executionId)
            .Where(item => item.ConsumedByMessageId is null)
            .SelectMany(item => item.FileIds)
            .ToArray();

    public void MarkSubmitted(string executionId, ApiMessage message) =>
        store.ConsumeStagedAttachments(executionId, message.MessageId, message.Files.Select(file => file.FileId));

    // A crash can happen between persistence of the normal user message and acknowledgement
    // of staged attachments. Reconcile against durable message history before another send.
    public void Reconcile(string executionId, IReadOnlyList<ApiChatHistoryMessage> messages)
    {
        var pending = Pending(executionId);
        if (pending.Count == 0)
            return;

        var submitted = messages
            .Where(message => message.Role == "user" &&
                              message.FileIds.Count == pending.Count &&
                              message.FileIds.SequenceEqual(pending, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(message => message.CreatedAt)
            .FirstOrDefault();
        if (submitted is not null)
            store.ConsumeStagedAttachments(executionId, submitted.MessageId, submitted.FileIds);
    }
}