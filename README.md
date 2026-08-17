# MEŽS

MEŽS exposes multiple AI integrations through one local async HTTP API and a React UI.

## Requirements

- .NET 10 SDK
- Node.js / npm
- Windows PowerShell for the repository test scripts

## Run

Install Electron dependencies once:

```powershell
npm ci --prefix electron
```

Start MEŽS:

```powershell
dotnet run --project src/Mezhs.Api/Mezhs.Api.csproj -- --config mezhs.yaml
```

Use another config file with:

```powershell
dotnet run --project src/Mezhs.Api/Mezhs.Api.csproj -- --config path\to\mezhs.yaml
```

## Tests

Build and run the deterministic repository tests with:

```powershell
dotnet build Mezhs.sln -c Release
npx --yes -p typescript@5.9.2 tsc -p integrations/Mezhs.Integrations.ChatGpt/browser/tsconfig.json
npx --yes -p typescript@5.9.2 tsc -p integrations/Mezhs.Integrations.Gemini/browser/tsconfig.json
node --test tests/browser-provider-operations.test.js
./tests/integration-resources.ps1
./tests/architecture.ps1
./tests/api-smoke.ps1
./tests/account-login-flow.ps1
./tests/profile-identity.ps1
npm ci --prefix electron
./tests/electron-provider-operations.ps1
./tests/electron-hidden-authorization.ps1
./tests/electron-login-visibility.ps1
```

The provider-operation suite intentionally runs twice: ordinary Node independently verifies protocol math such as the Sentinel SHA3 proof, while `electron-provider-operations.ps1` executes the same provider operations inside Electron's embedded Node runtime so runtime capability differences are caught before release.

## Messages

Create a new local chat and submit its first message:

```http
POST /v1/messages
Content-Type: application/json

{
  "connectionId": "gemini-guest",
  "content": "Hello"
}
```

The API immediately returns `202 Accepted` with a message ID and chat ID. Poll:

```http
GET /v1/messages/{messageId}
```

Statuses are `Queued`, `Running`, `Completed`, `Failed`, and `Cancelled`.

Continue the same local chat through any selected connection by sending both identifiers:

```http
POST /v1/messages
Content-Type: application/json

{
  "chatId": "chat_...",
  "connectionId": "chatgpt-sub",
  "content": "Continue this through ChatGPT"
}
```

The next turn can use another connection again. The local history remains one chat and each message records which connection handled it. Replay a previous user request with:

```http
POST /v1/messages/{messageId}/replay
```

Replay uses the original request's connection.

Message execution is owned by a host-managed background queue. The HTTP request only records/enqueues the work; shutdown has one owner for draining active message work instead of detached `Task.Run` jobs.

## Files

Connections advertise file/image support through integration capabilities. Upload a file before sending:

```http
POST /v1/files
Content-Type: multipart/form-data

connectionId=chatgpt-sub
file=@report.pdf
```

The upload connection records where the file originated and determines whether that upload operation is supported. The resulting local file ID is not permanently owned by that connection: a later message in the same or another local chat may reuse it through another file-capable connection.

Stored file metadata/content are available through:

```text
GET /v1/files/{fileId}
GET /v1/files/{fileId}/content
GET /v1/files/{fileId}/content?download=true
```

ChatGPT Account passes local file paths to its browser module so the integration can use ChatGPT's real browser-side upload flow. Assistant artifacts discovered by an integration are imported into MEŽS storage with the connection that produced them recorded as their origin.

## Storage

New local chats live under:

```text
data/chats/{chatId}/chat.json
data/chats/{chatId}/messages.jsonl
```
