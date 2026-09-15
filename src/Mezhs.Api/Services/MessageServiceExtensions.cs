using Mezhs.Api.Contracts;

namespace Mezhs.Services;

public static class MessageServiceExtensions
{
    public static async Task<ApiMessage> SendWithReplyAsync(
        this MessageService messages,
        PostMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        var created = messages.Post(request);
        return await messages.WaitForReplyAsync(created.MessageId, cancellationToken);
    }

    public static async Task<ApiMessage> WaitForReplyAsync(
        this MessageService messages,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var message = messages.Get(messageId)
                ?? throw new ResourceNotFoundException($"Message '{messageId}' was not found.");

            switch (message.Status)
            {
                case MessageStatus.Completed:
                    return message.Reply
                        ?? throw new InvalidOperationException("MEŽS completed without an assistant reply.");
                case MessageStatus.Failed:
                case MessageStatus.Cancelled:
                    throw new InvalidOperationException(
                        message.Error ?? $"MEŽS message ended with status {message.Status}.");
                default:
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                    break;
            }
        }
    }
}
