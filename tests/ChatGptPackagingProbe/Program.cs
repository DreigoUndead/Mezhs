using System.Diagnostics;
using System.Reflection;
using Mezhs.Browser;
using Mezhs.Integrations;
using Mezhs.Integrations.Browser;
using Mezhs.Integrations.ChatGpt;

if (args.Length != 1)
    throw new ArgumentException("Provide an isolated temporary extraction directory.");
var root = Path.GetFullPath(args[0]);
Directory.CreateDirectory(root);
var connection = new IntegrationConnection("isolated-test", "ChatGPT", "chatgpt-web",
    new Dictionary<string, string?>());
var host = new ProbeHost(root);
var integration = new ChatGptWebIntegration(connection, host);
var extract = typeof(BrowserIntegrationBase).GetMethod("GetBrowserModulePath",
    BindingFlags.Instance | BindingFlags.NonPublic)!;
string ModulePath(BrowserIntegrationBase instance) =>
    (string)extract.Invoke(instance, null)!;
var entry = ModulePath(integration);
var moduleDirectory = Path.GetDirectoryName(entry)!;
if (!string.Equals(Path.GetFileName(entry), "entry.js", StringComparison.Ordinal))
    throw new Exception($"Incorrect extracted entry: {entry}");
if (!Path.GetFullPath(entry).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new Exception($"Module escaped isolated extraction directory: {entry}");

const string prefix = "Mezhs.Integrations.ChatGpt.BrowserModule.";
var assembly = typeof(ChatGptWebIntegration).Assembly;
var names = assembly.GetManifestResourceNames()
    .Where(resource => resource.StartsWith(prefix, StringComparison.Ordinal))
    .OrderBy(resource => resource, StringComparer.Ordinal).ToArray();
if (names.Length != 8)
    throw new Exception($"Expected eight ChatGPT module resources, got {names.Length}.");
void CheckResources()
{
    foreach (var resource in names)
    {
        var file = Path.Combine(moduleDirectory, resource[prefix.Length..]);
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        if (!File.Exists(file) || !File.ReadAllBytes(file).AsSpan().SequenceEqual(buffer.ToArray()))
            throw new Exception($"Extracted file is missing or differs from resource: {file}");
    }
}
CheckResources();
var unchanged = Path.Combine(moduleDirectory, "account-files.js");
var oldTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
File.SetLastWriteTimeUtc(unchanged, oldTime);
if (ModulePath(integration) != entry || ModulePath(new ChatGptWebIntegration(connection, host)) != entry)
    throw new Exception("Repeated extraction changed the entry path.");
if (File.GetLastWriteTimeUtc(unchanged) != oldTime)
    throw new Exception("Repeated extraction rewrote an unchanged existing module.");
CheckResources();
File.Delete(unchanged);
ExpectCorruptedVersion();
if (File.Exists(unchanged))
    throw new Exception("Extraction modified an already published version.");
Directory.Delete(moduleDirectory, recursive: true);
if (ModulePath(new ChatGptWebIntegration(connection, host)) != entry)
    throw new Exception("Recreated content-addressed publication changed the path.");
CheckResources();
File.WriteAllText(unchanged, "stale resource");
ExpectCorruptedVersion();
if (File.ReadAllText(unchanged) != "stale resource")
    throw new Exception("Extraction overwrote a published version.");
Directory.Delete(moduleDirectory, recursive: true);
ModulePath(new ChatGptWebIntegration(connection, host));
CheckResources();

void ExpectCorruptedVersion()
{
    try
    {
        ModulePath(new ChatGptWebIntegration(connection, host));
        throw new Exception("Corrupted published version was silently accepted.");
    }
    catch (TargetInvocationException ex) when (ex.InnerException is IOException)
    {
        // Never repair a module underneath a transport that may already be using it.
    }
}

var node = new ProcessStartInfo("node")
{
    WorkingDirectory = root,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false
};
node.ArgumentList.Add("-e");
node.ArgumentList.Add("const assert = require('node:assert/strict'); const m = require(process.argv[1]); assert.equal(m.name, 'ChatGPT'); for (const op of ['getModels','getProjects','newChat','send','sendPrompt']) assert.equal(typeof m.operations[op], 'function'); console.log('NATIVE_REQUIRE_OK');");
node.ArgumentList.Add(entry);
using var process = Process.Start(node) ?? throw new Exception("Could not launch Node.");
var output = process.StandardOutput.ReadToEnd();
var error = process.StandardError.ReadToEnd();
process.WaitForExit();
if (process.ExitCode != 0 || !output.Contains("NATIVE_REQUIRE_OK", StringComparison.Ordinal))
    throw new Exception($"Node require failed ({process.ExitCode}): {error} {output}");
Console.WriteLine($"PASS: {names.Length} published assembly resources; extracted entry={entry}; unchanged versions reused and corrupted versions rejected; {output.Trim()}");

// Every transport must receive one immutable CommonJS module set.
// The old shared directory is deterministically wrong for two versions even without a timing race.
var versionRoot = Path.Combine(root, "version-probe");
var versionHost = new ProbeHost(versionRoot);
string VersionModule(string resource, bool singleFile = false) =>
    ModulePath(new VersionProbe(connection, versionHost, resource, singleFile));

const string one = "Mezhs.Tests.VersionOne";
const string two = "Mezhs.Tests.VersionTwo";
var firstEntry = VersionModule(one);
var secondEntry = VersionModule(two);
if (firstEntry == secondEntry ||
    Path.GetDirectoryName(firstEntry) == Path.GetDirectoryName(secondEntry))
    throw new Exception("Two embedded versions share a mutable module directory.");

void CheckVersion(string path, string expected)
{
    var directory = Path.GetDirectoryName(path)!;
    var entrySource = File.ReadAllText(path);
    var dependencySource = File.ReadAllText(Path.Combine(directory, "shared.js"));
    if (!entrySource.Contains($"'{expected}'", StringComparison.Ordinal) ||
        !dependencySource.Contains($"'{expected}'", StringComparison.Ordinal))
        throw new Exception($"Mixed or incomplete published CommonJS modules at {path}.");
}
CheckVersion(firstEntry, "one");
CheckVersion(secondEntry, "two");

var publishedParent = Path.GetDirectoryName(Path.GetDirectoryName(firstEntry)!)!;
Directory.Delete(Path.GetDirectoryName(firstEntry)!, recursive: true);
Directory.Delete(Path.GetDirectoryName(secondEntry)!, recursive: true);
using var monitorCancellation = new CancellationTokenSource();
var monitor = Task.Run(async () =>
{
    while (!monitorCancellation.IsCancellationRequested)
    {
        foreach (var candidate in Directory.EnumerateDirectories(publishedParent))
        {
            if (Path.GetFileName(candidate).Length != 64)
                continue; // .guid.tmp staging directories must not look published.
            var candidateEntry = Path.Combine(candidate, "entry.js");
            if (!File.Exists(candidateEntry))
                continue;
            var entrySource = File.ReadAllText(candidateEntry);
            var expected = entrySource.Contains("'one'", StringComparison.Ordinal) ? "one" : "two";
            CheckVersion(candidateEntry, expected);
        }
        await Task.Yield();
    }
});
try
{
    var concurrent = Enumerable.Range(0, 96).Select(i => Task.Run(() =>
    {
        var expected = i % 2 == 0 ? "one" : "two";
        var path = VersionModule(i % 2 == 0 ? one : two);
        CheckVersion(path, expected);
        return path;
    })).ToArray();
    var paths = await Task.WhenAll(concurrent);
    for (var i = 0; i < paths.Length; i++)
        if (paths[i] != (i % 2 == 0 ? firstEntry : secondEntry))
            throw new Exception("The same module version returned inconsistent paths.");
}
finally
{
    monitorCancellation.Cancel();
    await monitor;
}

var singleEntry = VersionModule("Mezhs.Tests.SingleModule", singleFile: true);
var singles = await Task.WhenAll(Enumerable.Range(0, 48)
    .Select(_ => Task.Run(() => VersionModule("Mezhs.Tests.SingleModule", singleFile: true))));
if (singles.Any(path => path != singleEntry) ||
    File.ReadAllText(singleEntry).Trim() != "module.exports = { version: 'single' };")
    throw new Exception("Concurrent single-file publication did not preserve its embedded source.");

var versionNode = new ProcessStartInfo("node")
{
    WorkingDirectory = root,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false
};
versionNode.ArgumentList.Add("-e");
versionNode.ArgumentList.Add(
    "for (const [version, file] of [['one', process.argv[1]], ['two', process.argv[2]]]) { const m = require(file); if (m.version !== version || m.dependency !== version) throw Error('Mixed CommonJS modules: ' + version); } console.log('VERSIONED_REQUIRE_OK');");
versionNode.ArgumentList.Add(firstEntry);
versionNode.ArgumentList.Add(secondEntry);
using var versionProcess = Process.Start(versionNode) ?? throw new Exception("Could not launch Node.");
var versionOutput = versionProcess.StandardOutput.ReadToEnd();
var versionError = versionProcess.StandardError.ReadToEnd();
versionProcess.WaitForExit();
if (versionProcess.ExitCode != 0 || !versionOutput.Contains("VERSIONED_REQUIRE_OK", StringComparison.Ordinal))
    throw new Exception($"Native require of two concurrently available versions failed: {versionError} {versionOutput}");
Console.WriteLine($"PASS: immutable versioned publication; 96 concurrent initializations; single-file publication; {versionOutput.Trim()}");

sealed class VersionProbe(
    IntegrationConnection connection,
    IBrowserIntegrationHost host,
    string resourceName,
    bool singleFile = false) : BrowserIntegrationBase(connection, host)
{
    protected override Assembly BrowserModuleAssembly => typeof(VersionProbe).Assembly;
    protected override string BrowserModuleResourceName => resourceName;
    protected override string? BrowserModuleEntryFileName => singleFile ? null : "entry.js";

    public override Task<IntegrationSendResult> SendMessageAsync(
        IntegrationSendContext context, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
sealed class ProbeHost(string root) : IBrowserIntegrationHost
{
    public int BrowserIdleMinutes => 10;
    public string GetConnectionRoot(string connectionId) => Path.Combine(root, connectionId);
    public IChatBrowserTransport CreateBrowserTransport() => throw new NotSupportedException();
}
