using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mezhs.Api.Contracts;

namespace Mezhs.Agent.Services;

public sealed class MezhsClient(HttpClient client)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync("/health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async Task<string> CreateChatAsync(
        string connectionId,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            "/v1/chats",
            new CreateChatRequest(connectionId),
            Json,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var chat = await response.Content.ReadFromJsonAsync<ApiChat>(
            Json,
            cancellationToken)
            ?? throw new InvalidOperationException("MEŽS returned an empty chat response.");
        return chat.ChatId;
    }

    public async Task<bool> ChatExistsAsync(string chatId, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            $"/v1/chats/{Uri.EscapeDataString(chatId)}",
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        await EnsureSuccessAsync(response, cancellationToken);
        return true;
    }

    public async Task<ApiChat?> TryGetChatAsync(
        string chatId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(
                $"/v1/chats/{Uri.EscapeDataString(chatId)}",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadFromJsonAsync<ApiChat>(Json, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ApiChatHistoryMessage>> GetMessagesAsync(
        string chatId,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            $"/v1/chats/{Uri.EscapeDataString(chatId)}/messages",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ApiChatHistoryMessage[]>(Json, cancellationToken)
            ?? [];
    }

    public async Task<string> SendMessageAsync(
        string chatId,
        string connectionId,
        string content,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            "/v1/messages",
            new PostMessageRequest(
                Content: content,
                ConnectionId: connectionId,
                ChatId: chatId),
            Json,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var created = await response.Content.ReadFromJsonAsync<ApiMessage>(
            Json,
            cancellationToken)
            ?? throw new InvalidOperationException("MEŽS returned an empty message response.");
        return await WaitForReplyAsync(created.MessageId, cancellationToken);
    }

    private async Task<string> WaitForReplyAsync(
        string messageId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var response = await client.GetAsync(
                $"/v1/messages/{Uri.EscapeDataString(messageId)}",
                cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var message = await response.Content.ReadFromJsonAsync<ApiMessage>(
                Json,
                cancellationToken)
                ?? throw new InvalidOperationException("MEŽS returned an empty message response.");

            switch (message.Status)
            {
                case MessageStatus.Completed:
                    return message.Reply?.Content
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

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"MEŽS API returned HTTP {(int)response.StatusCode}: {body}");
    }
}
