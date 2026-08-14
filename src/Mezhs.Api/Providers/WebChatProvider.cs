using Mezhs.Browser;
using Mezhs.Browser.Electron;
using Mezhs.Configuration;
using Mezhs.Models;
using Mezhs.Services;

namespace Mezhs.Providers;

public abstract class WebChatProvider(
    ConnectionOptions connection,
    MezhsOptions options,
    ChatStore store) : ChatProviderBase(connection)
{
    protected MezhsOptions Options { get; } = options;
    protected ChatStore Store { get; } = store;
    protected abstract string AutomationId { get; }

    protected IChatBrowserTransport CreateTransport() =>
        new ElectronBrowserTransport(Options.Transport.ElectronDirectory);

    protected BrowserTransportOptions TransportOptions(
        string profileDirectory,
        bool showBrowser,
        bool requireLogin) => new(
            profileDirectory,
            AutomationId,
            showBrowser,
            requireLogin);

    protected string PersistentProfilePath =>
        Path.Combine(Store.GetConnectionRoot(Connection.Id), "profile");

    protected string CreateSessionPath()
    {
        var path = Path.Combine(
            Store.GetConnectionRoot(Connection.Id),
            "sessions",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    protected static string ComposeConversation(ProviderSendContext context)
    {
        var history = context.History
            .Where(message => message.Role == "assistant" || message.Status == MessageStatus.Completed)
            .Select(message => $"[{(message.Role == "assistant" ? "Assistant" : "User")}]\n{message.Content}")
            .ToList();
        if (history.Count == 0)
            return context.Message.Content;

        history.Add($"[User]\n{context.Message.Content}");
        return "Continue the conversation below. Reply only to the latest user message.\n\n" +
               string.Join("\n\n", history);
    }

    protected static void TryDeleteSession(string sessionPath)
    {
        try
        {
            var parent = Directory.GetParent(sessionPath)?.FullName;
            if (parent is not null &&
                string.Equals(Path.GetFileName(parent), "sessions", StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(sessionPath))
                Directory.Delete(sessionPath, recursive: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not remove temporary browser session: {ex.Message}");
        }
    }
}
