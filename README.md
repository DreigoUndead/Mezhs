# MEŽS

MEŽS exposes logged-in and anonymous web AI sessions through one asynchronous HTTP API and a separate React frontend. The current providers are ChatGPT Web and Gemini Web, with Electron as the global cross-platform browser transport. The original WebView2 implementation remains as a Windows-only backup.

## Solution

```text
Mezhs.sln
|- src/Mezhs.Api                  ASP.NET Core API
|- src/Mezhs.Web                  React UI with a small ASP.NET host
|- transports/Mezhs.Browser.Abstractions
|- transports/Mezhs.Browser.Electron
`- transports/Mezhs.Browser.WebView2
```

The UI includes connection selection, browser login, global conversation history,
connection labels, search, persistent groups, asynchronous response polling, chat
continuation, and request replay. Models remain out of the UI until the provider
model contract is added. Supported connections also expose attachment selection,
image previews, and file downloads in the composer and message history.

## Provider architecture

```text
IChatProvider                         neutral C# contract
├── ChatGptSubscriptionProvider      one C# implementation per chat type
├── ChatGptFreeProvider
├── ChatGptGuestProvider
└── GeminiGuestProvider

ChatProvider                          neutral TypeScript contract
├── chatgptSubscription.provider.ts  one TypeScript module per chat type
├── chatgptFree.provider.ts
├── chatgptGuest.provider.ts
└── geminiGuest.provider.ts
```

Both registries discover provider implementations automatically. Core API, browser transport,
Electron, and React files contain no provider-specific AI references. Logged ChatGPT
connections keep persistent browser profiles and remember remote chat URLs. Anonymous
providers create fresh browser sessions and reconstruct conversations from MEŽS's local log.

## Configuration

Edit `mezhs.yaml`:

```yaml
version: 1

server:
  listen: http://127.0.0.1:5050

transport:
  type: electron
  idleMinutes: 15
  electronDirectory: electron

storage:
  root: data

connections:
  - id: chatgpt-sub
    name: ChatGPT Subscription
    provider: chatgpt-web-subscription

  - id: chatgpt-free
    name: ChatGPT Free
    provider: chatgpt-web-free

  - id: chatgpt-guest
    name: ChatGPT Guest
    provider: chatgpt-web

  - id: gemini-guest
    name: Gemini Guest
    provider: gemini-web
```

`workspace` is optional provider-specific configuration. For a logged ChatGPT connection it
is the exact name of the workspace/project in which new chats should be created. There is
deliberately no general project API.

## Run in Visual Studio

Open `Mezhs.sln`. On current Visual Studio versions, select the shared
`API + Web` solution launch profile and press `F5`. If the profile selector is
not available, right-click the solution, choose **Configure Startup Projects**,
select **Multiple startup projects**, and set both `Mezhs.Api` and `Mezhs.Web`
to **Start**.

The applications use:

```text
Web UI: http://127.0.0.1:5173
API:    http://127.0.0.1:5050
```

## Run from the terminal

Install Electron once:

```powershell
cd electron
npm install
cd ..
```

Start the API and web host in separate terminals:

```powershell
dotnet run --project src/Mezhs.Api/Mezhs.Api.csproj
dotnet run --project src/Mezhs.Web/Mezhs.Web.csproj
```

Use another configuration:

```powershell
dotnet run --project src/Mezhs.Api/Mezhs.Api.csproj -- --config path\to\mezhs.yaml
```

The API enables CORS so a separately hosted React application can call it directly during development.

## Login

Open and authorize a persistent ChatGPT connection:

```http
POST /v1/connections/chatgpt-sub/login
```

The request completes when the Electron profile is authenticated. Cookies and browser
storage are flushed to `data/connections/{connectionId}/profile`, so authorization is
reused after Electron or the API restarts.

## Messages

Create a new chat and submit its first message:

```http
POST /v1/messages
Content-Type: application/json

{
  "connectionId": "gemini-guest",
  "content": "Hello"
}
```

The API immediately returns HTTP `202 Accepted`:

```json
{
  "messageId": "msg_...",
  "chatId": "chat_...",
  "status": "Queued"
}
```

Continue an existing chat:

```http
POST /v1/messages
Content-Type: application/json

{
  "chatId": "chat_...",
  "content": "Continue"
}
```

Poll the request and retrieve its reply:

```http
GET /v1/messages/{messageId}
```

Statuses are `Queued`, `Running`, `Completed`, `Failed`, and `Cancelled`. A completed user message contains a separately addressable assistant message in `reply`.

Replay a request:

```http
POST /v1/messages/{messageId}/replay
```

## Files and images

Upload a file to a connection before posting the message:

```http
POST /v1/files
Content-Type: multipart/form-data

connectionId=chatgpt-sub
file=@report.pdf
```

Reference the returned ID from a message. A message may contain only files uploaded
for the same connection:

```json
{
  "connectionId": "chatgpt-sub",
  "content": "Summarize this report",
  "fileIds": ["file_..."]
}
```

File metadata and content are separately addressable:

```text
GET /v1/files/{fileId}
GET /v1/files/{fileId}/content
GET /v1/files/{fileId}/content?download=true
```

Logged-in ChatGPT connections support file and image input. The Electron transport
places the local file into ChatGPT's real composer so ChatGPT's current browser-side
upload and Sentinel flow remains intact. Guest connections report file/image input as
unsupported, and the UI disables their attachment control. Assistant download links
and generated images found in the response are copied into MEZHS storage when their
browser URLs remain available.

Other endpoints:

```text
GET /health
GET /v1/connections
GET /v1/chats?connectionId={connectionId}
GET /v1/categories
POST /v1/files
GET /v1/files/{fileId}
GET /v1/files/{fileId}/content
POST /v1/categories
PUT /v1/categories/{categoryId}
DELETE /v1/categories/{categoryId}
PATCH /v1/chats/{chatId}
GET /v1/messages/{messageId}
GET /v1/chats/{chatId}
GET /v1/chats/{chatId}/messages
```

## Persistence

Chats and append-only message snapshots are separated by connection:

```text
data/connections/{connectionId}/chats/{chatId}/chat.json
data/connections/{connectionId}/chats/{chatId}/messages.jsonl
data/connections/{connectionId}/files/{fileId}/file.json
data/connections/{connectionId}/files/{fileId}/content
```

On startup, MEŽS rebuilds its message index from these files. Requests interrupted by a restart are marked failed rather than left permanently running.

## Anonymous sessions

ChatGPT and Gemini guest connections use a fresh temporary browser profile for each
request. MEŽS rebuilds the conversation from its local message log, then removes the
temporary profile. ChatGPT guest mode is covered by the live transport check as well as
the deterministic API suite.

## Tests

Build everything:

```powershell
dotnet build
dotnet build transports/Mezhs.Browser.WebView2/Mezhs.Browser.WebView2.csproj
```

Run the deterministic HTTP smoke test:

```powershell
powershell -ExecutionPolicy Bypass -File tests/api-smoke.ps1
```

Run the provider-boundary architecture test:

```powershell
powershell -ExecutionPolicy Bypass -File tests/architecture.ps1
```

The smoke test covers server startup, health, new-chat creation, asynchronous polling,
chat continuation, persisted request/reply history, replay linkage, multipart upload,
inline download, and provider-returned file import.
