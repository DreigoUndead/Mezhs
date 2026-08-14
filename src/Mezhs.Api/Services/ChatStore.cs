using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mezhs.Configuration;
using Mezhs.Models;

namespace Mezhs.Services;

public sealed class ChatStore(MezhsOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _root = options.Storage.Root;
    private readonly ConcurrentDictionary<string, ChatRecord> _chats = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, StoredMessage> _messages = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CategoryRecord> _categories = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "connections"));
        var categoriesFile = Path.Combine(_root, "categories.json");
        if (File.Exists(categoriesFile))
        {
            try
            {
                var categories = JsonSerializer.Deserialize<CategoryRecord[]>(
                    await File.ReadAllTextAsync(categoriesFile),
                    JsonOptions) ?? [];
                foreach (var category in categories)
                    _categories[category.CategoryId] = category;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not load categories: {ex.Message}");
            }
        }

        foreach (var chatFile in Directory.EnumerateFiles(
                     Path.Combine(_root, "connections"),
                     "chat.json",
                     SearchOption.AllDirectories))
        {
            try
            {
                var chat = JsonSerializer.Deserialize<ChatRecord>(
                    await File.ReadAllTextAsync(chatFile),
                    JsonOptions);
                if (chat is null) continue;
                _chats[chat.ChatId] = chat;

                var messageFile = Path.Combine(Path.GetDirectoryName(chatFile)!, "messages.jsonl");
                if (!File.Exists(messageFile)) continue;
                foreach (var line in await File.ReadAllLinesAsync(messageFile))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var message = JsonSerializer.Deserialize<StoredMessage>(line, JsonOptions);
                    if (message is not null)
                        _messages[message.MessageId] = message;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not load chat log '{chatFile}': {ex.Message}");
            }
        }

        foreach (var interrupted in _messages.Values.Where(message =>
                     message.Role == "user" &&
                     message.Status is MessageStatus.Queued or MessageStatus.Running))
        {
            interrupted.Status = MessageStatus.Failed;
            interrupted.Error = "MEŽS restarted before this message completed.";
            interrupted.CompletedAt = DateTimeOffset.UtcNow;
            await SaveMessageAsync(interrupted);
        }
    }

    public string GetConnectionRoot(string connectionId) =>
        Path.Combine(_root, "connections", connectionId);

    public async Task<ChatRecord> CreateChatAsync(
        string connectionId,
        string? categoryId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(categoryId) && !_categories.ContainsKey(categoryId))
            throw new KeyNotFoundException($"Category '{categoryId}' was not found.");

        var chat = new ChatRecord
        {
            ChatId = NewId("chat"),
            ConnectionId = connectionId,
            CategoryId = categoryId
        };
        _chats[chat.ChatId] = chat;
        await SaveChatAsync(chat, cancellationToken);
        return chat;
    }

    public ChatRecord? GetChat(string chatId) =>
        _chats.TryGetValue(chatId, out var chat) ? chat : null;

    public IReadOnlyList<ChatRecord> GetChats(string? connectionId = null) =>
        _chats.Values
            .Where(chat => string.IsNullOrWhiteSpace(connectionId) ||
                string.Equals(chat.ConnectionId, connectionId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(chat => chat.UpdatedAt)
            .ToArray();

    public IReadOnlyList<CategoryRecord> GetCategories() =>
        _categories.Values
            .OrderBy(category => category.CreatedAt)
            .ThenBy(category => category.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async Task<CategoryRecord> CreateCategoryAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Category name is required.");
        if (_categories.Values.Any(category =>
            string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Category '{name}' already exists.");

        string[] colors = ["#d7ff64", "#72d7c8", "#ffad66", "#bba2ff", "#ff8fa3", "#7bb8ff"];
        var category = new CategoryRecord
        {
            CategoryId = NewId("cat"),
            Name = name,
            Color = colors[_categories.Count % colors.Length]
        };
        _categories[category.CategoryId] = category;
        await SaveCategoriesAsync(cancellationToken);
        return category;
    }

    public async Task<CategoryRecord> RenameCategoryAsync(
        string categoryId,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (!_categories.TryGetValue(categoryId, out var category))
            throw new KeyNotFoundException($"Category '{categoryId}' was not found.");
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Category name is required.");
        if (_categories.Values.Any(item =>
            item.CategoryId != categoryId &&
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Category '{name}' already exists.");

        category.Name = name;
        await SaveCategoriesAsync(cancellationToken);
        return category;
    }

    public async Task DeleteCategoryAsync(
        string categoryId,
        CancellationToken cancellationToken = default)
    {
        if (!_categories.TryRemove(categoryId, out _))
            throw new KeyNotFoundException($"Category '{categoryId}' was not found.");
        await SaveCategoriesAsync(cancellationToken);

        foreach (var chat in _chats.Values.Where(chat =>
                     string.Equals(chat.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase)))
        {
            chat.CategoryId = null;
            await SaveChatAsync(chat, cancellationToken);
        }
    }

    public async Task<ChatRecord> SetChatCategoryAsync(
        string chatId,
        string? categoryId,
        CancellationToken cancellationToken = default)
    {
        var chat = GetChat(chatId)
            ?? throw new KeyNotFoundException($"Chat '{chatId}' was not found.");
        if (!string.IsNullOrWhiteSpace(categoryId) && !_categories.ContainsKey(categoryId))
            throw new KeyNotFoundException($"Category '{categoryId}' was not found.");

        chat.CategoryId = string.IsNullOrWhiteSpace(categoryId) ? null : categoryId;
        await SaveChatAsync(chat, cancellationToken);
        return chat;
    }

    public StoredMessage? GetMessage(string messageId) =>
        _messages.TryGetValue(messageId, out var message) ? message : null;

    public IReadOnlyList<StoredMessage> GetMessages(string chatId) =>
        _messages.Values
            .Where(message => string.Equals(message.ChatId, chatId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.MessageId, StringComparer.Ordinal)
            .ToArray();

    public async Task SaveChatAsync(ChatRecord chat, CancellationToken cancellationToken = default)
    {
        chat.UpdatedAt = DateTimeOffset.UtcNow;
        _chats[chat.ChatId] = chat;
        var directory = GetChatDirectory(chat);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "chat.json");
        var temporary = target + ".tmp";
        var json = JsonSerializer.Serialize(chat, JsonOptions);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.WriteAllTextAsync(temporary, json, cancellationToken);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SaveMessageAsync(
        StoredMessage message,
        CancellationToken cancellationToken = default)
    {
        var chat = GetChat(message.ChatId)
            ?? throw new KeyNotFoundException($"Chat '{message.ChatId}' was not found.");
        _messages[message.MessageId] = message;
        var directory = GetChatDirectory(chat);
        Directory.CreateDirectory(directory);
        var line = JsonSerializer.Serialize(message, JsonOptions) + Environment.NewLine;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(
                Path.Combine(directory, "messages.jsonl"),
                line,
                cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    private async Task SaveCategoriesAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, "categories.json");
        var temporary = target + ".tmp";
        var json = JsonSerializer.Serialize(GetCategories(), JsonOptions);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.WriteAllTextAsync(temporary, json, cancellationToken);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private string GetChatDirectory(ChatRecord chat) => Path.Combine(
        GetConnectionRoot(chat.ConnectionId),
        "chats",
        chat.ChatId);
}
