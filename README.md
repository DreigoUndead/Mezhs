# MEŽS

MEŽS exposes multiple AI integrations through one asynchronous HTTP API and one React frontend. Integrations may use a browser session, a native/local runtime, or a direct service API; the MEŽS core does not assume which transport an integration needs.

Current built-in integrations are ChatGPT Web, ChatGPT Account, and Gemini Web. A deterministic Mock integration is used by the test suite.

## Architecture

```text
React UI
   |
   v
MEŽS HTTP API
   |
   +-- chats / messages / history / files
   +-- IntegrationRegistry
   |      `-- discovers [Integration(...)] classes in Mezhs.Integrations.*.dll
   |
   `-- integration contracts
          |
          +-- ChatGPT integration DLL
          |     +-- ChatGPT Web
          |     +-- ChatGPT Account + login module
          |     `-- embedded browser module
          |
          +-- Gemini integration DLL
          |     `-- embedded browser module
          |
          `-- future API/local integrations

Browser integrations
   |
   v
IBrowserIntegrationHost
   |
   v
Electron transport
   |
   `-- loads the browser module supplied by the integration
```

The important boundaries are:

- **Core** owns local chats, messages, history, files, configuration, HTTP endpoints, message processing, and dynamic integration discovery.
- **Integration DLLs** own service-specific behavior, capabilities, authentication modules, validation, and any browser module they need.
- **Electron** owns only generic browser mechanics: BrowserWindow lifetime, profiles/cookies, visibility, module execution, and the localhost bridge. Electron contains no ChatGPT/Gemini code.
- **React** is integration-neutral. It renders behavior from server-provided connection metadata and capabilities; adding a new integration does not require a matching frontend provider class.

`Mezhs.Integration.Abstractions` deliberately has no browser dependency. A local-model integration can implement the core contract without referencing Electron or browser types. Browser integrations opt into the separate `Mezhs.Integration.Browser` helper layer.

A MEŽS chat is deliberately independent from any one integration connection. Every user request and assistant reply records the connection that handled it. Remote continuation state is kept separately for each `(chat, connection)` pair. This lets one local conversation alternate between different accounts or integrations without giving any provider ownership of the local chat.

## Projects

```text
Mezhs.sln
|- src/Mezhs.Api
|- src/Mezhs.Web
|- src/Mezhs.Integration.Abstractions
|- src/Mezhs.Integration.Browser
|- transports/Mezhs.Browser.Abstractions
|- transports/Mezhs.Browser.Electron
|- integrations/Mezhs.Integrations.ChatGpt
|- integrations/Mezhs.Integrations.Gemini
`- integrations/Mezhs.Integrations.Mock
```

The API discovers concrete `IChatIntegration` classes marked with `[Integration("type")]` in `Mezhs.Integrations.*.dll` files in its application directory. Built-in integrations are project references so their DLLs are included with the application, while the runtime discovery mechanism does not reference concrete integration types. There is no separate integration-factory layer.

## Browser integration contract

Browser-specific automation belongs to the integration, not Electron.

For example:

```text
integrations/Mezhs.Integrations.ChatGpt/
|- ChatGptIntegration.cs
`- browser/
   |- chatgpt.ts
   `- tsconfig.json
```

`src/Mezhs.Integration.Browser/BrowserModule.d.ts` defines the TypeScript contract between an integration browser module and the generic browser host. Its declarations use local names such as `BrowserModule`, `PromptRequest`, `SendContext`, and `Artifact` because the file is already isolated to the integration typecheck context.

The integration's `.ts` file is the single source of truth. Browser modules use JavaScript-compatible TypeScript syntax, are typechecked against the shared contract, and are embedded directly into their integration DLL. When a browser session is needed, MEŽS extracts that embedded source into the connection data directory with a `.js` filename and Electron loads it as a CommonJS module. There is deliberately no second committed generated `.js` copy to drift out of sync.

## ChatGPT connection modes

MEŽS does not distinguish a "Free" account from a "Subscription" account. Those are remote account limits, not different MEŽS integrations.

Two ChatGPT modes remain:

- `chatgpt-web` — anonymous/transient web use. It creates a temporary browser profile for each request, never opens interactive login, reconstructs local conversation history into the prompt, and deletes the temporary session afterward.
- `chatgpt-web-account` — persistent account use. It extends the common ChatGPT web mechanics with a persistent profile, remote chat continuation, file/image capabilities, artifact import, and an explicit login module.

Account sends start hidden. If the account is not authorized, the integration invokes its login module, opens the browser interactively, waits for authorization, and then continues the original send. Electron only reports the authorization requirement; the integration owns the decision to request login.

Remote continuation is connection-specific. If another connection handled an intervening local turn, the old remote thread is no longer an exact continuation of the local chat. MEŽS therefore starts a new remote thread for that connection and seeds it from the complete local history instead of silently dropping the intervening context.

## Login modules

Login is an optional integration module, not a boolean plus an unrelated initialization method.

The connection metadata endpoint reports `requiresLogin: true` when an integration exposes `ILoginModule`. The generic route is:

```http
POST /v1/connections/{connectionId}/login
```

Integrations without a login module do not advertise login capability. This keeps capability discovery tied to the implementation that actually provides the behavior.

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
    name: ChatGPT Account
    integration: chatgpt-web-account

  - id: chatgpt-guest
    name: ChatGPT Guest
    integration: chatgpt-web

  - id: gemini-guest
    name: Gemini Guest
    integration: gemini-web
```

Connection IDs are durable machine/storage identities. Persistent browser profiles and connection-originated files live under the connection ID, so change the display `name` freely but do not rename an existing `id` unless its stored data is intentionally being migrated. Display names are required to be unique so the UI does not present ambiguous connections.

The configuration parser is strict: unknown YAML properties fail instead of silently falling back to defaults.

`workspace` is currently an optional ChatGPT Account setting. For a new remote chat it is the exact workspace/project name to select. Integration-specific validation is owned by the integration itself rather than a central factory switch.

## Run in Visual Studio

Open `Mezhs.sln`. Select the shared `API + Web` solution launch profile and press `F5`. If the profile selector is unavailable, configure both `Mezhs.Api` and `Mezhs.Web` as startup projects.

Default development addresses:

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

They are not nested under a connection. On startup, the store also recognizes the previous `data/connections/{connectionId}/chats/{chatId}` layout and migrates those chats into the connection-neutral layout while preserving the legacy connection's remote continuation state.

Local JSON/JSONL state uses synchronous file operations behind one process lock. Network, browser, process, and stream-copy operations remain asynchronous where they involve real external waiting.

## Tests

The deterministic suite uses `Mezhs.Integrations.Mock.dll`, which is discovered through the same runtime registration mechanism as production integrations. A second mock connection exposes a no-op login module so login discovery/routing can be verified without a real browser.

```powershell
dotnet build Mezhs.sln -c Release
powershell -ExecutionPolicy Bypass -File tests/integration-resources.ps1
powershell -ExecutionPolicy Bypass -File tests/architecture.ps1
powershell -ExecutionPolicy Bypass -File tests/api-smoke.ps1
powershell -ExecutionPolicy Bypass -File tests/account-login-flow.ps1
powershell -ExecutionPolicy Bypass -File tests/profile-identity.ps1
```

`api-smoke.ps1` includes an A → B → A connection regression inside one local chat and verifies that a file uploaded through A can be reused through B. The browser integration sources are typechecked against `src/Mezhs.Integration.Browser/BrowserModule.d.ts`. The architecture test verifies the connection-neutral chat invariant, owned message queue, direct integration registration, concise browser contracts, and that integration-specific behavior does not leak back into Electron, API core, or React.
