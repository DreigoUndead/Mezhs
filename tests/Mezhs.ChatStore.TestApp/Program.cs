using System.Text.Json;
using System.Text.Json.Serialization;
using Mezhs.Agent.Configuration;
using Mezhs.Agent.Persistence;
using Mezhs.Api.Contracts;
using Mezhs.Configuration;
using Mezhs.Models;
using Mezhs.Services;
using Mezhs.Sqlite;
using Microsoft.Data.Sqlite;

var root = Path.Combine(Path.GetTempPath(), "mezhs-chat-sqlite-" + Guid.NewGuid().ToString("N"));
var mainRoot = Path.Combine(root, "main");
var badRoot = Path.Combine(root, "failed-import");
var now = DateTimeOffset.UtcNow.AddDays(-1);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
json.Converters.Add(new JsonStringEnumConverter());

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
string Serialize<T>(T item) => JsonSerializer.Serialize(item, json);
void Write(string file, string content)
{
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    File.WriteAllText(file, content);
}
ChatStore Open(string path)
{
    var store = new ChatStore(new MezhsOptions { Storage = new StorageOptions { Root = path } });
    store.Initialize();
    return store;
}
static int Rows(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return Convert.ToInt32(command.ExecuteScalar());
}

try
{
    // Real file-backed layout, including duplicate JSONL message status versions.
    var category = new CategoryRecord
    {
        CategoryId = "cat_fixture", Name = "Project", Color = "#d7ff64", CreatedAt = now
    };
    var modern = new ChatRecord
    {
        ChatId = "chat_modern", Title = "Saved title", CategoryId = category.CategoryId,
        CreatedAt = now, UpdatedAt = now.AddHours(1),
        RemoteStates =
        [
            new ChatConnectionState
            {
                ConnectionId = "mock", RemoteConversationId = "remote-123",
                RemoteParentMessageId = "parent-42", RemoteChatUrl = "https://example.test/chat",
                LastLocalMessageId = "msg_modern"
            }
        ]
    };
    var first = new StoredMessage
    {
        MessageId = "msg_modern", ChatId = modern.ChatId, ConnectionId = "mock",
        Role = "user", Origin = "agent", Content = "Original message", Model = "mock-fast",
        FileIds = ["file_fixture"], CreatedAt = now.AddMinutes(1),
        Status = MessageStatus.Queued
    };
    var final = new StoredMessage
    {
        MessageId = "msg_modern", ChatId = modern.ChatId, ConnectionId = "mock",
        Role = "user", Origin = "agent", Content = "Original message", Model = "mock-fast",
        FileIds = ["file_fixture"], CreatedAt = now.AddMinutes(1),
        StartedAt = now.AddMinutes(2), CompletedAt = now.AddMinutes(3),
        Status = MessageStatus.Completed, ReplyMessageId = "msg_response",
        Activity = "answer complete", Analysis = "persisted analysis"
    };
    Write(Path.Combine(mainRoot, "categories.json"), Serialize(new[] { category }));
    var modernFile = Path.Combine(mainRoot, "chats", modern.ChatId, "chat.json");
    Write(modernFile, Serialize(modern));
    Write(Path.Combine(Path.GetDirectoryName(modernFile)!, "messages.jsonl"),
        Serialize(first) + "\n" + Serialize(final) + "\n");

    var legacyFile = Path.Combine(mainRoot, "connections", "mock", "chats", "chat_legacy", "chat.json");
    Write(legacyFile, Serialize(new
    {
        chatId = "chat_legacy", title = "Legacy title", connectionId = "mock", remoteConversationId = "old-remote",
        remoteParentMessageId = "old-parent", categoryId = category.CategoryId,
        createdAt = now, updatedAt = now.AddMinutes(2)
    }));
    var legacyReply = new StoredMessage
    {
        MessageId = "msg_legacy", ChatId = "chat_legacy", ConnectionId = "mock",
        Role = "assistant", Origin = "assistant", Content = "Legacy answer",
        Status = MessageStatus.Completed, CreatedAt = now.AddMinutes(2)
    };
    Write(Path.Combine(Path.GetDirectoryName(legacyFile)!, "messages.jsonl"),
        Serialize(legacyReply) + "\n");

    var store = Open(mainRoot);
    Check(store.GetChats().Count == 2, "Migration did not import both layouts.");
    Check(store.GetCategories().Single().Name == "Project", "Category lost on migration.");
    var loaded = store.GetChat(modern.ChatId)!;
    Check(loaded.Title == modern.Title && loaded.CreatedAt == now &&
        loaded.RemoteStates.Single().RemoteParentMessageId == "parent-42",
        "Modern chat metadata/remote state lost.");
    var restored = store.GetMessages(modern.ChatId).Single();
    Check(restored.Status == MessageStatus.Completed &&
        restored.ReplyMessageId == "msg_response" &&
        restored.FileIds.SequenceEqual(["file_fixture"]) &&
        restored.Activity == "answer complete" &&
        restored.Analysis == "persisted analysis", "Last JSONL status version or attachments lost.");
    var legacy = store.GetChat("chat_legacy")!;
    Check(legacy.Title == "Legacy title", "Legacy title was not preserved.");
    Check(legacy.RemoteStates.Single().LastLocalMessageId == legacyReply.MessageId,
        "Legacy remote state did not attach to completed assistant reply.");

    // Fail the chat timestamp write and prove neither the new queued message
    // nor its updated timestamp leaks into memory or SQLite.
    var beforeFailedTouch = store.GetChat(modern.ChatId)!.UpdatedAt;
    using (var connection = new SqliteDatabase(Path.Combine(mainRoot, "chats.sqlite")).Open())
    {
        using var trigger = connection.CreateCommand();
        trigger.CommandText = """
            CREATE TRIGGER RejectChatTouch BEFORE UPDATE ON Chats
            WHEN OLD.ChatId = 'chat_modern'
            BEGIN SELECT RAISE(ABORT, 'injected chat update failure'); END;
            """;
        trigger.ExecuteNonQuery();
    }
    try
    {
        store.SaveMessageAndTouchChat(new StoredMessage
        {
            MessageId = "msg_atomic_failure", ChatId = modern.ChatId, ConnectionId = "mock",
            Role = "user", Content = "must roll back", Status = MessageStatus.Queued
        }, loaded);
        throw new InvalidOperationException("Injected message-plus-chat failure was not raised.");
    }
    catch (SqliteException) { }
    Check(store.GetMessage("msg_atomic_failure") is null &&
        loaded.UpdatedAt == beforeFailedTouch,
        "Failed message-plus-chat write leaked into live state.");
    using (var connection = new SqliteDatabase(Path.Combine(mainRoot, "chats.sqlite")).Open())
    {
        Check(Rows(connection, "SELECT COUNT(*) FROM Messages WHERE MessageId = 'msg_atomic_failure';") == 0,
            "Failed message-plus-chat write committed the message row.");
    }

    var titleBeforeFailure = loaded.Title;
    try
    {
        store.RenameChat(modern.ChatId, "Not committed");
        throw new InvalidOperationException("Injected chat rename failure was not raised.");
    }
    catch (SqliteException) { }
    Check(loaded.Title == titleBeforeFailure && loaded.UpdatedAt == beforeFailedTouch,
        "Failed chat update leaked title or timestamp into live state.");
    using (var connection = new SqliteDatabase(Path.Combine(mainRoot, "chats.sqlite")).Open())
    {
        using var drop = connection.CreateCommand();
        drop.CommandText = "DROP TRIGGER RejectChatTouch;";
        drop.ExecuteNonQuery();
    }

    var appended = new StoredMessage
    {
        MessageId = "msg_new", ChatId = modern.ChatId, ConnectionId = "mock",
        Role = "user", Content = "SQLite-only append", CreatedAt = now.AddHours(2)
    };
    store.SaveMessage(appended);
    store.RenameChat(modern.ChatId, "SQLite rename");
    store.RenameCategory(category.CategoryId, "Renamed project");
    Check(!File.ReadAllText(modernFile).Contains("SQLite rename"),
        "New chat state unexpectedly wrote the legacy backup.");

    store.DeleteCategory(category.CategoryId);
    Check(store.GetChats().All(item => item.CategoryId is null),
        "Category removal did not unassign all chats.");
    var made = store.CreateChat(null);

    // Local deletion is atomic even when the SQLite statement fails mid-batch.
    using (var connection = new SqliteDatabase(Path.Combine(mainRoot, "chats.sqlite")).Open())
    {
        using var trigger = connection.CreateCommand();
        trigger.CommandText = """
            CREATE TRIGGER RejectChatDelete BEFORE DELETE ON Chats
            WHEN OLD.ChatId = 'chat_legacy'
            BEGIN SELECT RAISE(ABORT, 'injected delete failure'); END;
            """;
        trigger.ExecuteNonQuery();
    }
    try
    {
        store.DeleteChats([modern.ChatId, "chat_legacy"]);
        throw new InvalidOperationException("Injected deletion failure was not raised.");
    }
    catch (SqliteException) { }
    Check(store.GetChat(modern.ChatId) is not null && store.GetChat("chat_legacy") is not null,
        "Core bulk deletion partially committed.");
    using (var connection = new SqliteDatabase(Path.Combine(mainRoot, "chats.sqlite")).Open())
    {
        Check(Rows(connection, "SELECT COUNT(*) FROM Chats;") == 3,
            "SQLite committed a partial failed bulk deletion.");
        using var drop = connection.CreateCommand();
        drop.CommandText = "DROP TRIGGER RejectChatDelete;";
        drop.ExecuteNonQuery();
    }

    // Simulate the accepted Agent-first/Core-second interruption: the first
    // store has already committed, but Core remains and a retry must work.
    var agentPath = Path.Combine(root, "agent.sqlite");
    var agents = new AgentStore(new AgentOptions { AgentStorage = agentPath });
    agents.Initialize();
    using (var connection = new SqliteDatabase(agentPath).Open())
    {
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO AgentChats (ChatId, PolicyId, OriginSource, CreatedAt, UpdatedAt)
            VALUES ('chat_modern', 'test', 'manual', $at, $at);
            """;
        insert.Parameters.AddWithValue("$at", now.ToString("O"));
        insert.ExecuteNonQuery();
    }
    agents.RemoveChatData([modern.ChatId, "chat_legacy"]);
    Check(agents.GetAgentChat(modern.ChatId) is null, "Agent-first commit did not persist.");
    Check(store.GetChat(modern.ChatId) is not null, "Core should remain after first-store commit.");

    store.DeleteChats([modern.ChatId, "chat_legacy"]);
    var restarted = Open(mainRoot);
    Check(restarted.GetChat(modern.ChatId) is null && restarted.GetChat("chat_legacy") is null,
        "Deleted legacy-backed chat reappeared after restart.");
    Check(restarted.GetChat(made.ChatId) is not null &&
        restarted.GetChats().Count == 1,
        "SQLite-only chat was not preserved across restart.");
    Check(restarted.GetCategories().Count == 0, "Deleted category reappeared.");
    Check(File.Exists(modernFile) && File.Exists(legacyFile),
        "Legacy migration backup was destroyed.");
    using (var connection = new SqliteDatabase(Path.Combine(mainRoot, "chats.sqlite")).Open())
        Check(Rows(connection, "SELECT COUNT(*) FROM MigrationState;") == 1,
            "Legacy import completion marker duplicated.");

    // An invalid JSONL record must abort the complete migration transaction.
    var badFile = Path.Combine(badRoot, "chats", "chat_bad", "chat.json");
    Write(badFile, Serialize(new ChatRecord { ChatId = "chat_bad" }));
    var messagesFile = Path.Combine(Path.GetDirectoryName(badFile)!, "messages.jsonl");
    Write(messagesFile, "{bad-json\n");
    try
    {
        Open(badRoot);
        throw new InvalidOperationException("Invalid JSONL was silently imported.");
    }
    catch (InvalidDataException) { }
    Check(File.ReadAllText(messagesFile) == "{bad-json\n", "Failed import altered legacy files.");
    using (var connection = new SqliteDatabase(Path.Combine(badRoot, "chats.sqlite")).Open())
        Check(Rows(connection, "SELECT COUNT(*) FROM MigrationState;") == 0 &&
              Rows(connection, "SELECT COUNT(*) FROM Chats;") == 0,
            "Failed import incorrectly committed records or completion marker.");

    Write(messagesFile, Serialize(new StoredMessage
    {
        MessageId = "msg_fixed", ChatId = "chat_bad", ConnectionId = "mock",
        Role = "user", Content = "repaired"
    }) + "\n");
    var fixedStore = Open(badRoot);
    Check(fixedStore.GetMessages("chat_bad").Single().Content == "repaired",
        "Corrected import could not recover from failed startup.");
    fixedStore.DeleteChats(["chat_bad"]);
    Check(Open(badRoot).GetChats().Count == 0,
        "Previously failed import resurrected chat on later startup.");

    Console.WriteLine("PASS: SQLite ChatStore migration, JSONL versions, legacy remote metadata, atomic message+chat updates, metadata rollback, category transactions, failed import rollback, Agent-first partial failure, Core bulk rollback, and no resurrection.");
}
finally
{
    SqliteConnection.ClearAllPools();
    if (Directory.Exists(root))
        Directory.Delete(root, recursive: true);
}
