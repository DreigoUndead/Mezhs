using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mezhs;
using Mezhs.Api.Contracts;
using Mezhs.Configuration;
using Mezhs.Models;
using Mezhs.Sqlite;
using Microsoft.Data.Sqlite;

namespace Mezhs.Services;

// Chat and message objects remain shared with active provider work; SQLite is
// the durable owner and the common gate serializes mutations and deletion.
public sealed class ChatStore(MezhsOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _root = options.Storage.Root;
    private readonly SqliteDatabase _database = new(Path.Combine(options.Storage.Root, "chats.sqlite"));
    private readonly ConcurrentDictionary<string, ChatRecord> _chats = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, StoredMessage> _messages = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CategoryRecord> _categories = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writeLock = new();

    // Shared with message admission and Agent-host deletion. SQLite transactions
    // deliberately never extend across calls into Agent or Executor stores.
    public object DeletionSync => _writeLock;

    public void Initialize()
    {
        lock (_writeLock)
        {
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(Path.Combine(_root, "connections"));
            Directory.CreateDirectory(Path.Combine(_root, "chats"));
            _database.Initialize("""
                CREATE TABLE IF NOT EXISTS Chats (
                    ChatId TEXT PRIMARY KEY COLLATE NOCASE,
                    Json TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_Chats_UpdatedAt ON Chats(UpdatedAt);
                CREATE TABLE IF NOT EXISTS Messages (
                    MessageId TEXT PRIMARY KEY COLLATE NOCASE,
                    ChatId TEXT NOT NULL COLLATE NOCASE REFERENCES Chats(ChatId) ON DELETE CASCADE,
                    ConnectionId TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    Json TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_Messages_Chat_Created
                    ON Messages(ChatId, CreatedAt, MessageId);
                CREATE INDEX IF NOT EXISTS IX_Messages_Connection ON Messages(ConnectionId, ChatId);
                CREATE TABLE IF NOT EXISTS Categories (
                    CategoryId TEXT PRIMARY KEY COLLATE NOCASE,
                    Json TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS MigrationState (
                    Name TEXT PRIMARY KEY,
                    CompletedAt TEXT NOT NULL
                );
                """);
            ImportLegacyOnce();
            ReadDatabase();

            // Incomplete provider requests from an interrupted API process are
            // failed in SQLite, not appended to the old messages.jsonl backup.
            foreach (var interrupted in _messages.Values.Where(message =>
                         message.Role == "user" &&
                         message.Status is MessageStatus.Queued or MessageStatus.Running).ToArray())
            {
                interrupted.Status = MessageStatus.Failed;
                interrupted.Error = "MEŽS restarted before this message completed.";
                interrupted.CompletedAt = DateTimeOffset.UtcNow;
                SaveMessage(interrupted);
            }
        }
    }

    public string GetConnectionRoot(string connectionId) =>
        Path.Combine(_root, "connections", connectionId);

    public ChatRecord CreateChat(string? categoryId)
    {
        lock (_writeLock)
        {
            if (!string.IsNullOrWhiteSpace(categoryId) && !_categories.ContainsKey(categoryId))
                throw new ResourceNotFoundException($"Category '{categoryId}' was not found.");

            var chat = new ChatRecord { ChatId = NewId("chat"), CategoryId = categoryId };
            using var connection = _database.Open();
            InsertChat(connection, null, chat);
            _chats[chat.ChatId] = chat;
            return chat;
        }
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
        lock (_writeLock)
        {
            name = name.Trim();
            if (string.IsNullOrWhiteSpace(name))
                throw new RequestValidationException("Category name is required.");
            if (_categories.Values.Any(category =>
                string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new RequestValidationException($"Category '{name}' already exists.");

            string[] colors = ["#d7ff64", "#72d7c8", "#ffad66", "#bba2ff", "#ff8fa3", "#7bb8ff"];
            var category = new CategoryRecord
            {
                CategoryId = NewId("cat"),
                Name = name,
                Color = colors[_categories.Count % colors.Length]
            };
            using var connection = _database.Open();
            InsertCategory(connection, null, category);
            _categories[category.CategoryId] = category;
            return category;
        }
    }

    public CategoryRecord RenameCategory(string categoryId, string name)
    {
        lock (_writeLock)
        {
            if (!_categories.TryGetValue(categoryId, out var category))
                throw new ResourceNotFoundException($"Category '{categoryId}' was not found.");
            name = name.Trim();
            if (string.IsNullOrWhiteSpace(name))
                throw new RequestValidationException("Category name is required.");
            if (_categories.Values.Any(item =>
                !string.Equals(item.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new RequestValidationException($"Category '{name}' already exists.");

            var previous = category.Name;
            category.Name = name;
            try
            {
                using var connection = _database.Open();
                InsertCategory(connection, null, category);
            }
            catch
            {
                category.Name = previous;
                throw;
            }
            return category;
        }
    }

    public void DeleteCategory(string categoryId)
    {
        lock (_writeLock)
        {
            if (!_categories.ContainsKey(categoryId))
                throw new ResourceNotFoundException($"Category '{categoryId}' was not found.");

            var members = _chats.Values
                .Where(chat => string.Equals(chat.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var previousUpdates = members.ToDictionary(chat => chat.ChatId, chat => chat.UpdatedAt,
                StringComparer.OrdinalIgnoreCase);
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();
            try
            {
                foreach (var chat in members)
                {
                    chat.CategoryId = null;
                    chat.UpdatedAt = DateTimeOffset.UtcNow;
                    UpdateChat(connection, transaction, chat);
                }
                Execute(connection, transaction,
                    "DELETE FROM Categories WHERE CategoryId = $id;",
                    ("$id", categoryId));
                transaction.Commit();
            }
            catch
            {
                foreach (var chat in members)
                {
                    chat.CategoryId = categoryId;
                    chat.UpdatedAt = previousUpdates[chat.ChatId];
                }
                throw;
            }
            _categories.TryRemove(categoryId, out _);
        }
    }

    public IReadOnlyList<string> DeleteChats(IEnumerable<string> chatIds)
    {
        var entries = chatIds.ToArray();
        if (entries.Length == 0)
            throw new RequestValidationException("At least one chatId is required.");
        if (entries.Any(string.IsNullOrWhiteSpace))
            throw new RequestValidationException("chatIds cannot contain empty chat IDs.");
        var ids = entries.Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        lock (_writeLock)
        {
            var missing = ids.FirstOrDefault(id => !_chats.ContainsKey(id));
            if (missing is not null)
                throw new ResourceNotFoundException($"Chat '{missing}' was not found.");

            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();
            foreach (var id in ids)
                Execute(connection, transaction, "DELETE FROM Chats WHERE ChatId = $id;", ("$id", id));
            transaction.Commit();

            var deleted = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids)
                _chats.TryRemove(id, out _);
            foreach (var message in _messages.Values.Where(m => deleted.Contains(m.ChatId)).ToArray())
                _messages.TryRemove(message.MessageId, out _);
        }
        return ids;
    }

    public ChatRecord RenameChat(string chatId, string title)
    {
        lock (_writeLock)
        {
            title = title.Trim();
            if (title.Length is < 1 or > 200)
                throw new RequestValidationException("Chat title must contain 1 to 200 characters.");

            var chat = GetChat(chatId)
                ?? throw new ResourceNotFoundException($"Chat '{chatId}' was not found.");
            var previous = chat.Title;
            chat.Title = title;
            try { SaveChat(chat); }
            catch { chat.Title = previous; throw; }
            return chat;
        }
    }

    public ChatRecord SetChatCategory(string chatId, string? categoryId)
    {
        lock (_writeLock)
        {
            var chat = GetChat(chatId)
                ?? throw new ResourceNotFoundException($"Chat '{chatId}' was not found.");
            if (!string.IsNullOrWhiteSpace(categoryId) && !_categories.ContainsKey(categoryId))
                throw new ResourceNotFoundException($"Category '{categoryId}' was not found.");
            var previous = chat.CategoryId;
            chat.CategoryId = string.IsNullOrWhiteSpace(categoryId) ? null : categoryId;
            try { SaveChat(chat); }
            catch { chat.CategoryId = previous; throw; }
            return chat;
        }
    }

    public StoredMessage? GetMessage(string messageId) =>
        _messages.TryGetValue(messageId, out var message) ? message : null;

    public IReadOnlyList<StoredMessage> GetMessages(string chatId) =>
        _messages.Values
            .Where(message => string.Equals(message.ChatId, chatId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.MessageId, StringComparer.Ordinal)
            .ToArray();

    public ChatListState? TryGetListState(string chatId)
    {
        var chat = GetChat(chatId);
        if (chat is null) return null;

        StoredMessage? latest = null;
        StoredMessage? firstUser = null;
        foreach (var message in _messages.Values)
        {
            if (!string.Equals(message.ChatId, chatId, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsLater(message, latest)) latest = message;
            if (message.Role == "user" && IsEarlier(message, firstUser)) firstUser = message;
        }

        var title = string.IsNullOrWhiteSpace(chat.Title) ? firstUser?.Content : chat.Title;
        return new ChatListState(
            latest?.ConnectionId,
            title is { Length: > 200 } ? title[..200] : title);
    }

    public ChatRuntimeState GetRuntimeState(string chatId)
    {
        if (!_chats.ContainsKey(chatId))
            throw new ResourceNotFoundException($"Chat '{chatId}' was not found.");
        var count = 0;
        StoredMessage? latest = null;
        StoredMessage? active = null;
        foreach (var message in _messages.Values)
        {
            if (!string.Equals(message.ChatId, chatId, StringComparison.OrdinalIgnoreCase)) continue;
            count++;
            if (IsLater(message, latest)) latest = message;
            if (message.Role == "user" &&
                message.Status is MessageStatus.Queued or MessageStatus.Running &&
                IsLater(message, active))
                active = message;
        }
        return new ChatRuntimeState(
            count, latest?.MessageId, latest?.Status,
            active is null ? null : new MessageRuntimeState(
                active.MessageId, active.Status, active.Activity, active.ActivityDetail, active.ActivityAt));
    }

    public void SaveChat(ChatRecord chat)
    {
        lock (_writeLock)
        {
            if (!_chats.ContainsKey(chat.ChatId))
                throw new ResourceNotFoundException($"Chat '{chat.ChatId}' was not found.");
            chat.UpdatedAt = DateTimeOffset.UtcNow;
            using var connection = _database.Open();
            UpdateChat(connection, null, chat);
        }
    }

    public void SaveMessage(StoredMessage message)
    {
        lock (_writeLock)
        {
            if (!_chats.ContainsKey(message.ChatId))
                throw new ResourceNotFoundException($"Chat '{message.ChatId}' was not found.");
            using var connection = _database.Open();
            InsertMessage(connection, null, message);
            _messages[message.MessageId] = message;
        }
    }

    public static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    private void ImportLegacyOnce()
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        using (var marker = connection.CreateCommand())
        {
            marker.Transaction = transaction;
            marker.CommandText = "SELECT COUNT(*) FROM MigrationState WHERE Name = 'legacy-file-chatstore-v1';";
            if (Convert.ToInt32(marker.ExecuteScalar()) != 0)
                return;
        }

        // Read and validate every supported file BEFORE writing a completion marker.
        // Any invalid record fails startup instead of silently losing chat history.
        var categories = new Dictionary<string, CategoryRecord>(StringComparer.OrdinalIgnoreCase);
        var categoryFile = Path.Combine(_root, "categories.json");
        if (File.Exists(categoryFile))
        {
            var items = JsonSerializer.Deserialize<CategoryRecord[]>(File.ReadAllText(categoryFile), JsonOptions)
                ?? throw new InvalidDataException($"Null category list in '{categoryFile}'.");
            foreach (var item in items)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.CategoryId) ||
                    string.IsNullOrWhiteSpace(item.Name) || !categories.TryAdd(item.CategoryId, item))
                    throw new InvalidDataException($"Invalid or duplicate category in '{categoryFile}'.");
            }
        }

        var chats = new Dictionary<string, ChatRecord>(StringComparer.OrdinalIgnoreCase);
        var messages = new Dictionary<string, StoredMessage>(StringComparer.OrdinalIgnoreCase);
        var chatsRoot = Path.Combine(_root, "chats");
        if (Directory.Exists(chatsRoot))
        {
            foreach (var path in Directory.EnumerateFiles(chatsRoot, "chat.json", SearchOption.AllDirectories))
            {
                var chat = JsonSerializer.Deserialize<ChatRecord>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidDataException($"Null chat record in '{path}'.");
                if (string.IsNullOrWhiteSpace(chat.ChatId) || !chats.TryAdd(chat.ChatId, chat))
                    throw new InvalidDataException($"Invalid or duplicate chat in '{path}'.");
                LoadLegacyMessages(path, chat.ChatId, messages);
            }
        }

        var connectionsRoot = Path.Combine(_root, "connections");
        if (Directory.Exists(connectionsRoot))
        {
            foreach (var connectionDirectory in Directory.EnumerateDirectories(connectionsRoot))
            {
                var legacyRoot = Path.Combine(connectionDirectory, "chats");
                if (!Directory.Exists(legacyRoot)) continue;
                foreach (var path in Directory.EnumerateFiles(legacyRoot, "chat.json", SearchOption.AllDirectories))
                {
                    var legacy = JsonSerializer.Deserialize<LegacyChatRecord>(File.ReadAllText(path), JsonOptions)
                        ?? throw new InvalidDataException($"Null legacy chat record in '{path}'.");
                    if (string.IsNullOrWhiteSpace(legacy.ChatId))
                        throw new InvalidDataException($"Invalid legacy chat ID in '{path}'.");
                    // Modern layout wins where a legacy copy of the same chat remains.
                    if (chats.ContainsKey(legacy.ChatId)) continue;
                    var chat = new ChatRecord
                    {
                        ChatId = legacy.ChatId,
                        CategoryId = legacy.CategoryId,
                        CreatedAt = legacy.CreatedAt,
                        UpdatedAt = legacy.UpdatedAt
                    };
                    if (!chats.TryAdd(chat.ChatId, chat))
                        throw new InvalidDataException($"Duplicate legacy chat ID in '{path}'.");
                    LoadLegacyMessages(path, chat.ChatId, messages);
                    if (!string.IsNullOrWhiteSpace(legacy.ConnectionId) &&
                        (!string.IsNullOrWhiteSpace(legacy.RemoteChatUrl) ||
                         !string.IsNullOrWhiteSpace(legacy.RemoteConversationId) ||
                         !string.IsNullOrWhiteSpace(legacy.RemoteParentMessageId)))
                    {
                        var lastReply = messages.Values
                            .Where(message => string.Equals(message.ChatId, chat.ChatId, StringComparison.OrdinalIgnoreCase) &&
                                message.Role == "assistant" && message.Status == MessageStatus.Completed &&
                                string.Equals(message.ConnectionId, legacy.ConnectionId, StringComparison.OrdinalIgnoreCase))
                            .OrderBy(message => message.CreatedAt)
                            .ThenBy(message => message.MessageId, StringComparer.Ordinal)
                            .LastOrDefault();
                        chat.RemoteStates.Add(new ChatConnectionState
                        {
                            ConnectionId = legacy.ConnectionId,
                            RemoteChatUrl = legacy.RemoteChatUrl,
                            RemoteConversationId = legacy.RemoteConversationId,
                            RemoteParentMessageId = legacy.RemoteParentMessageId,
                            LastLocalMessageId = lastReply?.MessageId
                        });
                    }
                }
            }
        }

        foreach (var category in categories.Values)
            InsertCategory(connection, transaction, category);
        foreach (var chat in chats.Values)
            InsertChat(connection, transaction, chat);
        foreach (var message in messages.Values)
            InsertMessage(connection, transaction, message);
        Execute(connection, transaction,
            "INSERT INTO MigrationState (Name, CompletedAt) VALUES ('legacy-file-chatstore-v1', $at);",
            ("$at", DateTimeOffset.UtcNow.ToString("O")));
        transaction.Commit();
        // Legacy files are intentionally retained, never reread after this marker.
    }

    private static void LoadLegacyMessages(
        string chatFile, string chatId, Dictionary<string, StoredMessage> messages)
    {
        var path = Path.Combine(Path.GetDirectoryName(chatFile)!, "messages.jsonl");
        if (!File.Exists(path)) return;
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            StoredMessage item;
            try
            {
                item = JsonSerializer.Deserialize<StoredMessage>(line, JsonOptions)
                    ?? throw new InvalidDataException("Null message.");
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                throw new InvalidDataException($"Invalid message at '{path}:{lineNumber}'.", ex);
            }
            if (string.IsNullOrWhiteSpace(item.MessageId) ||
                !string.Equals(item.ChatId, chatId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Invalid message ID or chat association at '{path}:{lineNumber}'.");
            // JSONL contains many versions of one message; the last record wins.
            messages[item.MessageId] = item;
        }
    }

    private void ReadDatabase()
    {
        _chats.Clear();
        _messages.Clear();
        _categories.Clear();
        using var connection = _database.Open();
        foreach (var category in ReadRows<CategoryRecord>(connection, "SELECT Json FROM Categories;"))
            _categories[category.CategoryId] = category;
        foreach (var chat in ReadRows<ChatRecord>(connection, "SELECT Json FROM Chats;"))
            _chats[chat.ChatId] = chat;
        foreach (var message in ReadRows<StoredMessage>(connection, "SELECT Json FROM Messages;"))
            _messages[message.MessageId] = message;
    }

    private static IReadOnlyList<T> ReadRows<T>(SqliteConnection connection, string query)
    {
        using var command = connection.CreateCommand();
        command.CommandText = query;
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read())
            result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), JsonOptions)
                ?? throw new InvalidDataException($"Null SQLite {typeof(T).Name} record."));
        return result;
    }

    private static void InsertCategory(SqliteConnection connection, SqliteTransaction? transaction, CategoryRecord category) =>
        Execute(connection, transaction, """
            INSERT INTO Categories(CategoryId, Json) VALUES ($id, $json)
            ON CONFLICT(CategoryId) DO UPDATE SET Json = excluded.Json;
            """, ("$id", category.CategoryId), ("$json", JsonSerializer.Serialize(category, JsonOptions)));

    private static void InsertChat(SqliteConnection connection, SqliteTransaction? transaction, ChatRecord chat) =>
        Execute(connection, transaction, """
            INSERT INTO Chats(ChatId, Json, UpdatedAt) VALUES ($id, $json, $updated);
            """, ("$id", chat.ChatId), ("$json", JsonSerializer.Serialize(chat, JsonOptions)),
            ("$updated", chat.UpdatedAt.ToString("O")));

    private static void UpdateChat(SqliteConnection connection, SqliteTransaction? transaction, ChatRecord chat)
    {
        var affected = Execute(connection, transaction, """
            UPDATE Chats SET Json = $json, UpdatedAt = $updated WHERE ChatId = $id;
            """, ("$id", chat.ChatId), ("$json", JsonSerializer.Serialize(chat, JsonOptions)),
            ("$updated", chat.UpdatedAt.ToString("O")));
        if (affected != 1)
            throw new ResourceNotFoundException($"Chat '{chat.ChatId}' was not found.");
    }

    private static void InsertMessage(SqliteConnection connection, SqliteTransaction? transaction, StoredMessage message) =>
        Execute(connection, transaction, """
            INSERT INTO Messages(MessageId, ChatId, ConnectionId, CreatedAt, Json)
            VALUES ($id, $chat, $connection, $created, $json)
            ON CONFLICT(MessageId) DO UPDATE SET Json = excluded.Json;
            """, ("$id", message.MessageId), ("$chat", message.ChatId),
            ("$connection", message.ConnectionId), ("$created", message.CreatedAt.ToString("O")),
            ("$json", JsonSerializer.Serialize(message, JsonOptions)));

    private static int Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command.ExecuteNonQuery();
    }

    private static bool IsEarlier(StoredMessage candidate, StoredMessage? current)
    {
        if (current is null) return true;
        var time = candidate.CreatedAt.CompareTo(current.CreatedAt);
        return time < 0 ||
               (time == 0 && string.CompareOrdinal(candidate.MessageId, current.MessageId) < 0);
    }

    private static bool IsLater(StoredMessage candidate, StoredMessage? current)
    {
        if (current is null) return true;
        var time = candidate.CreatedAt.CompareTo(current.CreatedAt);
        return time > 0 ||
               (time == 0 && string.CompareOrdinal(candidate.MessageId, current.MessageId) > 0);
    }

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
