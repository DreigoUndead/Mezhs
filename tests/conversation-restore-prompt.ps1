$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('mezhs-conversation-restore-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null

try {
    $abstractionsProject = Join-Path $root 'src\Mezhs.Integration.Abstractions\Mezhs.Integration.Abstractions.csproj'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$abstractionsProject" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $temp 'Test.csproj') -Encoding UTF8

    @'
using Mezhs.Integrations;

var now = DateTimeOffset.UtcNow;
var chat = new IntegrationChatContext("chat", "connection");
var current = new IntegrationMessageContext("current", "user", "latest", false, now);
var history = new IntegrationMessageContext[]
{
    new("user-1", "user", "hello", true, now.AddMinutes(-2)),
    new("assistant-1", "assistant", "hi there", true, now.AddMinutes(-1))
};

var continuation = new IntegrationSendContext(chat, current, history, []);
Assert(continuation.Prompt == "latest", "Synchronized continuation did not keep the latest message unchanged.");

var restore = new IntegrationSendContext(chat, current, history, [], RestoreConversation: true);
var expected = "Continue the conversation below. Reply only to the latest user message.\n\n" +
               "[User]\nhello\n\n" +
               "[Assistant]\nhi there\n\n" +
               "[User]\nlatest";
Assert(restore.Prompt == expected, "Restored conversation prompt did not contain canonical history and latest message.");

var firstMessage = new IntegrationSendContext(chat, current, [], [], RestoreConversation: true);
Assert(firstMessage.Prompt == "latest", "A new conversation without history should send only the latest message.");

Console.WriteLine("PASS: conversation restore prompt is canonical and leaves normal continuations unchanged.");

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
