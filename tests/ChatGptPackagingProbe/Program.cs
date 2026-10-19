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
string ModulePath(ChatGptWebIntegration instance) =>
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
ModulePath(new ChatGptWebIntegration(connection, host));
CheckResources();
File.WriteAllText(unchanged, "stale resource");
ModulePath(new ChatGptWebIntegration(connection, host));
CheckResources();

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
Console.WriteLine($"PASS: {names.Length} published assembly resources; extracted entry={entry}; repeated/missing/stale files repaired; {output.Trim()}");

sealed class ProbeHost(string root) : IBrowserIntegrationHost
{
    public int BrowserIdleMinutes => 10;
    public string GetConnectionRoot(string connectionId) => Path.Combine(root, connectionId);
    public IChatBrowserTransport CreateBrowserTransport() => throw new NotSupportedException();
}
