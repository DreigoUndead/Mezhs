$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('mezhs-conversation-restore-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null

try {
    $browserProject = Join-Path $root 'src\Mezhs.Integration.Browser\Mezhs.Integration.Browser.csproj'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$browserProject" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $temp 'Test.csproj') -Encoding UTF8

    @'
using System.Reflection;
using Mezhs.Integrations;
using Mezhs.Integrations.Browser;

var now = DateTimeOffset.UtcNow;
var history = new ChatMessage[]
{
    new("user-1", "first", "user", "hello", true, now.AddMinutes(-2)),
    new("assistant-1", "second", "assistant", "hi there", true, now.AddMinutes(-1)),
    new("unfinished", "first", "user", "unsubmitted draft", false, now)
};
var historyQueries = 0;
var chat = new ChatContext("chat", "connection", null,
    (limit, offset, fromStart, ct) =>
    {
        ct.ThrowIfCancellationRequested();
        Assert(limit is null && offset is null && fromStart, "Restore history query must use full chronological history.");
        historyQueries++;
        return Task.FromResult<IReadOnlyList<ChatMessage>>(history);
    }, restoreConversation: true);
var expected = "Continue the conversation below. Reply only to the latest user message.\n\n" +
               "[User]\nhello\n\n[Assistant]\nhi there\n\n[User]\nlatest";
Assert(await RestoreAsync(chat, "latest") == expected, "Restore prompt did not contain canonical cross-provider history and latest message.");
Assert(historyQueries == 1, "Restoration must query history only when requested.");

var empty = new ChatContext("new", "connection", null,
    (_, _, _, _) => Task.FromResult<IReadOnlyList<ChatMessage>>([]),
    restoreConversation: true);
Assert(await RestoreAsync(empty, "latest") == "latest", "New conversation without history must send the original text.");

var normal = new ChatContext("normal", "connection", "remote",
    (_, _, _, _) => throw new Exception("Normal continuation should not fetch history."));
Assert(!normal.RestoreConversation, "A normal continuation must not request restoration.");

Console.WriteLine("PASS: browser restoration uses on-demand cross-provider history, canonical prompt and empty-history fallback.");

static async Task<string> RestoreAsync(ChatContext chat, string text)
{
    var method = typeof(BrowserIntegrationBase).GetMethod(
        "BuildRestorePromptAsync", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Restore prompt builder not found.");
    return await (Task<string>)method.Invoke(null, [chat, text, CancellationToken.None])!;
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
'@ | Set-Content -LiteralPath (Join-Path $temp 'Program.cs') -Encoding UTF8

    dotnet run --project (Join-Path $temp 'Test.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw "Conversation restore prompt test failed with exit code $LASTEXITCODE." }
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
