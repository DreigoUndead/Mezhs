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
    private readonly object _writeLock = new();

    public void Initialize()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "connections"));
        Directory.CreateDirectory(Path.Combine(_root, "chats"));
        LoadCategories();

        foreach (var chatFile in Directory.EnumerateFiles(
                     Path.Combine(_root, "chats"),
                     "chat.json",
                     SearchOption.AllDirectories))
            LoadChat(chatFile);

        foreach (var chatFile in Directory.EnumerateFiles(
                     Path.Combine(_root, "connections"),
                     "chat.json",
                     SearchOption.AllDirectories))
            MigrateLegacyChat(chatFile);

        foreach (var interrupted in _messages.Values.Where(message =>
                     message.Role == "user" &&
                     message.Status is MessageStatus.Queued or MessageStatus.Running))
        {
            interrupted.Status = MessageStatus.Failed;
            interrupted.Error = "MEŽS restarted before this message completed.";
            interrupted.CompletedAt = DateTimeOffset.UtcNow;
            SaveMessage(interrupted);
        }
    }

    public string GetConnectionRoot(string connectionId) =>
        Path.Combine(_root, "connections", connectionId);

    public ChatRecord CreateChat(string? categoryId)
    {
        if (!string.IsNullOrWhiteSpace(categoryId) && !_categories.ContainsKey(categoryId))
            throw new KeyNotFoundException($"Category '{categoryId}' was not found.");

        var chat = new ChatRecord
        {
            ChatId = NewId("chat"),
            CategoryId = categoryId
        };
        _chats[chat.ChatId] = chat;
        PersistChat(chat);
        return chat;
    }

    public ChatRecord? GetChat(string chatId) =>
        _chats.TryGetValue(chatId, out var chat) ? chat : null;

    public IReadOnlyList<ChatRecord> GetChats(string? connectionId = null) =>
        _chats.Values
            .Where(chat => string.IsNullOrWhiteSpace(connectionId) ||
                _messages.Values.Any(message =>
                    string.Equals(message.ChatId, chat.ChatId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(message.ConnectionId, connectionId, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(chat => chat.UpdatedAt)
            .ToArray();

    public IReadOnlyList<CategoryRecord> GetCategories() =>
        _categories.Values
            .OrderBy(category => category.CreatedAt)
            .ThenBy(category => category.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public CategoryRecord CreateCategory(string name)
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
        SaveCategories();
        return category;
    }

    public CategoryRecord RenameCategory(string categoryId, string name)
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
        SaveCategories();
        return category;
    }

    public void DeleteCategory(string categoryId)
    {
        if (!_categories.TryRemove(categoryId, out _))
            throw new KeyNotFoundException($"Category '{categoryId}' was not found.");
        SaveCategories();

        foreach (var chat in _chats.Values.Where(chat =>
                     string.Equals(chat.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase)))
        {
            chat.CategoryId = null;
            SaveChat(chat);
        }
    }

    public ChatRecord SetChatCategory(string chatId, string? categoryId)
    {
        var chat = GetChat(chatId)
            ?? throw new KeyNotFoundException($"Chat '{chatId}' was not found.");
        if (!string.IsNullOrWhiteSpace(categoryId) && !_categories.ContainsKey(categoryId))
            throw new KeyNotFoundException($"Category '{categoryId}' was not found.");

        chat.CategoryId = string.IsNullOrWhiteSpace(categoryId) ? null : categoryId;
        SaveChat(chat);
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

    public void SaveChat(ChatRecord chat)
    {
        chat.UpdatedAt = DateTimeOffset.UtcNow;
        _chats[chat.ChatId] = chat;
        PersistChat(chat);
    }

    public void SaveMessage(StoredMessage message)
    {
        var chat = GetChat(message.ChatId)
            ?? throw new KeyNotFoundException($"Chat '{message.ChatId}' was not found.");
        _messages[message.MessageId] = message;
        var directory = GetChatDirectory(chat.ChatId);
        Directory.CreateDirectory(directory);
        var line = JsonSerializer.Serialize(message, JsonOptions) + Environment.NewLine;

        lock (_writeLock)
            File.AppendAllText(Path.Combine(directory, "messages.jsonl"), line);
    }

    public static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    private void LoadCategories()
    {
        var path = Path.Combine(_root, "categories.json");
        if (!File.Exists(path)) return;
        try
        {
            var categories = JsonSerializer.Deserialize<CategoryRecord[]>(File.ReadAllText(path), JsonOptions) ?? [];
            foreach (var category in categories)
                _categories[category.CategoryId] = category;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not load categories: {ex.Message}");
        }
    }

    private void LoadChat(string chatFile)
    {
        try
        {
            var chat = JsonSerializer.Deserialize<ChatRecord>(File.ReadAllText(chatFile), JsonOptions);
            if (chat is null) return;
            _chats[chat.ChatId] = chat;
            LoadMessages(Path.GetDirectoryName(chatFile)!);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not load chat log '{chatFile}': {ex.Message}");
        }
    }

    private void MigrateLegacyChat(string chatFile)
    {
        try
        {
            var legacy = JsonSerializer.Deserialize<LegacyChatRecord>(File.ReadAllText(chatFile), JsonOptions);
            if (legacy is null || string.IsNullOrWhiteSpace(legacy.ChatId) || _chats.ContainsKey(legacy.ChatId))
                return;

            var chat = new ChatRecord
            {
                ChatId = legacy.ChatId,
                CategoryId = legacy.CategoryId,
                CreatedAt = legacy.CreatedAt,
                UpdatedAt = legacy.UpdatedAt
            };
            _chats[chat.ChatId] = chat;
            var legacyDirectory = Path.GetDirectoryName(chatFile)!;
            LoadMessages(legacyDirectory);

            if (!string.IsNullOrWhiteSpace(legacy.ConnectionId) &&
                (!string.IsNullOrWhiteSpace(legacy.RemoteChatUrl) ||
                 !string.IsNullOrWhiteSpace(legacy.RemoteConversationId) ||
                 !string.IsNullOrWhiteSpace(legacy.RemoteParentMessageId)))
            {
                var lastReply = GetMessages(chat.ChatId).LastOrDefault(message =>
                    message.Role == "assistant" &&
                    message.Status == MessageStatus.Completed &&
                    string.Equals(message.ConnectionId, legacy.ConnectionId, StringComparison.OrdinalIgnoreCase));
                chat.RemoteStates.Add(new ChatConnectionState
                {
                    ConnectionId = legacy.ConnectionId,
                    RemoteChatUrl = legacy.RemoteChatUrl,
                    RemoteConversationId = legacy.RemoteConversationId,
                    RemoteParentMessageId = legacy.RemoteParentMessageId,
                    LastLocalMessageId = lastReply?.MessageId
                });
            }

            var targetDirectory = GetChatDirectory(chat.ChatId);
            Directory.CreateDirectory(targetDirectory);
            var legacyMessages = Path.Combine(legacyDirectory, "messages.jsonl");
            var targetMessages = Path.Combine(targetDirectory, "messages.jsonl");
            lock (_writeLock)
            {
                if (File.Exists(legacyMessages) && !File.Exists(targetMessages))
                    File.Copy(legacyMessages, targetMessages);
            }
            PersistChat(chat);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not migrate legacy chat log '{chatFile}': {ex.Message}");
        }
    }

    private void LoadMessages(string directory)
    {
        var path = Path.Combine(directory, "messages.jsonl");
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var message = JsonSerializer.Deserialize<StoredMessage>(line, JsonOptions);
            if (message is not null)
                _messages[message.MessageId] = message;
        }
    }

    private void SaveCategories()
    {
        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, "categories.json");
        var temporary = target + ".tmp";
        var json = JsonSerializer.Serialize(GetCategories(), JsonOptions);

        lock (_writeLock)
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, target, overwrite: true);
        }
    }

    private void PersistChat(ChatRecord chat)
    {
        var directory = GetChatDirectory(chat.ChatId);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "chat.json");
        var temporary = target + ".tmp";
        var json = JsonSerializer.Serialize(chat, JsonOptions);

        lock (_writeLock)
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, target, overwrite: true);
        }
    }

    private string GetChatDirectory(string chatId) =>
        Path.Combine(_root, "chats", chatId);

    private sealed class LegacyChatRecord
    {
        public string ChatId { get; init; } = "";
        public string ConnectionId { get; init; } = "";
        public string? RemoteChatUrl { get; init; }
        public string? RemoteConversationId { get; init; }
        public string? RemoteParentMessageId { get; init; }
        public string? CategoryId { get; init; }
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    }
}
