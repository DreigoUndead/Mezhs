using System.Reflection;
using Mezhs.Browser;

namespace Mezhs.Integrations.Browser;

public abstract class BrowserIntegrationBase(
    IntegrationConnection connection,
    IIntegrationHost host) : ChatIntegrationBase(connection)
{
    private string? _browserModulePath;

    protected IBrowserIntegrationHost Host { get; } = BrowserIntegrationHost.Require(host);
    protected abstract Assembly BrowserModuleAssembly { get; }
    protected abstract string BrowserModuleResourceName { get; }

    protected string PersistentProfilePath =>
        Path.Combine(Host.GetConnectionRoot(Connection.Id), "profile");

    protected BrowserTransportOptions TransportOptions(
        string profileDirectory,
        bool showBrowser,
        bool requireAuthorization) => new(
            profileDirectory,
            GetBrowserModulePath(),
            showBrowser,
            requireAuthorization);

    protected async Task<IntegrationSendResult> SendAnonymousAsync(
        IntegrationSendContext context,
        CancellationToken cancellationToken)
    {
        var sessionPath = CreateSessionPath();
        var transport = Host.CreateBrowserTransport();
        try
        {
            await transport.InitializeAsync(TransportOptions(
                sessionPath,
                showBrowser: false,
                requireAuthorization: false), cancellationToken);
            var response = await transport.InvokeAsync<BrowserSendResult>(
                "sendPrompt",
                new { Prompt = ComposeConversation(context), NewChat = true },
                cancellationToken);
            if (!response.Ok)
                throw new InvalidOperationException(response.Error ?? "Anonymous browser request failed.");
            return new IntegrationSendResult(response.Text);
        }
        finally
        {
            await transport.DisposeAsync();
            TryDeleteSession(sessionPath);
        }
    }

    protected static string ComposeConversation(IntegrationSendContext context)
    {
        var history = context.History
            .Where(message => message.Role == "assistant" || message.Completed)
            .Select(message => $"[{(message.Role == "assistant" ? "Assistant" : "User")}]\n{message.Content}")
            .ToList();
        if (history.Count == 0)
            return context.Message.Content;

        history.Add($"[User]\n{context.Message.Content}");
        return "Continue the conversation below. Reply only to the latest user message.\n\n" +
               string.Join("\n\n", history);
    }

    protected string CreateSessionPath()
    {
        var path = Path.Combine(
            Host.GetConnectionRoot(Connection.Id),
            "sessions",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
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

    private string GetBrowserModulePath()
    {
        if (_browserModulePath is not null && File.Exists(_browserModulePath))
            return _browserModulePath;

        var directory = Path.Combine(Host.GetConnectionRoot(Connection.Id), "modules");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{BrowserModuleAssembly.GetName().Name}.js");
        using var source = BrowserModuleAssembly.GetManifestResourceStream(BrowserModuleResourceName)
            ?? throw new InvalidOperationException(
                $"Browser module resource '{BrowserModuleResourceName}' was not found in '{BrowserModuleAssembly.GetName().Name}'.");
        using var target = File.Create(path);
        source.CopyTo(target);
        _browserModulePath = path;
        return path;
    }

    private sealed record BrowserSendResult(
        bool Ok,
        string Text = "",
        string? Error = null);
}
