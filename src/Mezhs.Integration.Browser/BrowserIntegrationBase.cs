using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Mezhs.Browser;

namespace Mezhs.Integrations.Browser;

public abstract class BrowserIntegrationBase(
    IntegrationConnection connection,
    IIntegrationHost host) : ChatIntegrationBase(connection)
{
    private BrowserAccountSession? _accountSession;
    private BrowserDiagnosticsModule? _diagnostics;

    protected IBrowserIntegrationHost Host { get; } = BrowserIntegrationHost.Require(host);
    protected abstract Assembly BrowserModuleAssembly { get; }
    protected abstract string BrowserModuleResourceName { get; }
    protected virtual string? BrowserModuleEntryFileName => null;

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

    protected BrowserAccountSession CreateAccountSession() => _accountSession ??= new(
        Host,
        (showBrowser, requireAuthorization) => TransportOptions(
            PersistentProfilePath,
            showBrowser,
            requireAuthorization));

    public override IIntegrationDiagnosticsModule? Diagnostics =>
        _accountSession is null ? null : _diagnostics ??= new BrowserDiagnosticsModule(_accountSession);

    public override ValueTask DisposeAsync() =>
        _accountSession?.DisposeAsync() ?? ValueTask.CompletedTask;

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
                new { context.Prompt, NewChat = true },
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
        var assembly = BrowserModuleAssembly;
        var entryFileName = BrowserModuleEntryFileName ?? $"{assembly.GetName().Name}.js";
        var prefix = BrowserModuleResourceName + ".";
        string[] resourceNames = BrowserModuleEntryFileName is null
            ? [BrowserModuleResourceName]
            : assembly.GetManifestResourceNames()
                .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        if (resourceNames.Length == 0)
            throw new InvalidOperationException($"No browser module resources found under '{prefix}'.");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = new List<(string Name, byte[] Contents)>(resourceNames.Length);
        foreach (var resourceName in resourceNames)
        {
            using var source = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Browser module resource '{resourceName}' was not found in '{assembly.GetName().Name}'.");
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            var name = BrowserModuleEntryFileName is null ? entryFileName : resourceName[prefix.Length..];
            var contents = buffer.ToArray();
            files.Add((name, contents));
            hash.AppendData(Encoding.UTF8.GetBytes(name));
            hash.AppendData([0]);
            hash.AppendData(SHA256.HashData(contents));
        }
        if (!files.Any(file => file.Name == entryFileName))
            throw new InvalidOperationException($"Browser module entry '{entryFileName}' was not embedded.");

        var parent = Path.Combine(Host.GetConnectionRoot(Connection.Id), "modules", assembly.GetName().Name!);
        var directory = Path.Combine(parent, Convert.ToHexString(hash.GetHashAndReset()));
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(parent);
            var staging = Path.Combine(parent, $".{Guid.NewGuid():N}.tmp");
            Directory.CreateDirectory(staging);
            try
            {
                foreach (var file in files)
                    File.WriteAllBytes(Path.Combine(staging, file.Name), file.Contents);
                try
                {
                    Directory.Move(staging, directory);
                }
                catch (IOException) when (Directory.Exists(directory))
                {
                    // Another instance published the same complete version first.
                }
            }
            finally
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
        }

        foreach (var file in files)
        {
            var path = Path.Combine(directory, file.Name);
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(file.Contents))
                throw new IOException($"Published browser module is missing or differs from embedded resource: '{path}'.");
        }
        return Path.Combine(directory, entryFileName);
    }

    private sealed record BrowserSendResult(
        bool Ok,
        string Text = "",
        string? Error = null);
}
