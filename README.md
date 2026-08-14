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
   |      `-- discovers Mezhs.Integrations.*.dll
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

- **Core** owns chats, messages, history, files, configuration, HTTP endpoints, and dynamic integration discovery.
- **Integration DLLs** own service-specific behavior, connection modes, capabilities, authentication modules, and any browser module they need.
- **Electron** owns only generic browser mechanics: BrowserWindow lifetime, profiles/cookies, visibility, module execution, and the localhost bridge. Electron contains no ChatGPT/Gemini code.
- **React** is integration-neutral. It renders behavior from server-provided connection metadata and capabilities; adding a new integration does not require a matching frontend provider class.

`Mezhs.Integration.Abstractions` deliberately has no browser dependency. A local-model integration can implement the core contract without referencing Electron or browser types. Browser integrations opt into the separate `Mezhs.Integration.Browser` helper layer.

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

The API discovers integration factories from `Mezhs.Integrations.*.dll` files in its application directory. Built-in integrations are project references so their DLLs are included with the application, while the runtime discovery mechanism does not reference concrete integration types.

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

`src/Mezhs.Integration.Browser/BrowserModule.d.ts` defines the TypeScript contract between an integration browser module and the generic browser host.

The integration's `.ts` file is the single source of truth. Browser modules currently use JavaScript-compatible TypeScript syntax, are typechecked against the shared contract, and are embedded directly into their integration DLL. When a browser session is needed, MEŽS extracts that embedded source into the connection data directory with a `.js` filename and Electron loads it as a CommonJS module. There is deliberately no second committed generated `.js` copy to drift out of sync.

## ChatGPT connection modes

MEŽS no longer distinguishes a "Free" account from a "Subscription" account. Those are remote account limits, not different MEŽS integrations.

Two ChatGPT modes remain:

- `chatgpt-web` — anonymous/transient web use. It creates a temporary browser profile for each request, never opens interactive login, reconstructs local conversation history into the prompt, and deletes the temporary session afterward.
- `chatgpt-web-account` — persistent account use. It extends the common ChatGPT web mechanics with a persistent profile, remote chat continuation, file/image capabilities, artifact import, and an explicit login module.

A normal message send is never allowed to become an interactive login operation. If an account connection needs authorization, the hidden send fails and the user must invoke the explicit login action. Only that login action may show the browser window.

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
  - id: chatgpt-account
    name: ChatGPT Account
    integration: chatgpt-web-account

  - id: chatgpt-guest
    name: ChatGPT Guest
    integration: chatgpt-web

  - id: gemini-guest
    name: Gemini Guest
    integration: gemini-web
```

The configuration parser is strict: unknown YAML properties fail instead of silently falling back to defaults.

`workspace` is currently an optional ChatGPT Account setting. For a new remote chat it is the exact workspace/project name to select.

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

Create a new chat and submit its first message:

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

Continue an existing chat by posting `chatId` instead of `connectionId`. Replay a previous user request with:

```http
POST /v1/messages/{messageId}/replay
```

## Files

Connections advertise file/image support through integration capabilities. Upload a file before sending:

```http
POST /v1/files
Content-Type: multipart/form-data

connectionId=chatgpt-account
file=@report.pdf
```

Then include its ID in the message request. Stored file metadata/content are available through:

```text
GET /v1/files/{fileId}
GET /v1/files/{fileId}/content
GET /v1/files/{fileId}/content?download=true
```

ChatGPT Account passes local file paths to its browser module so the integration can use ChatGPT's real browser-side upload flow. Assistant artifacts discovered by the integration are imported into MEŽS storage.

## Tests

The deterministic suite uses `Mezhs.Integrations.Mock.dll`, which is discovered through the same runtime plugin mechanism as production integrations. A second mock connection exposes a no-op login module so login discovery/routing can be verified without a real browser.

```powershell
dotnet build Mezhs.sln -c Release
powershell -ExecutionPolicy Bypass -File tests/integration-resources.ps1
powershell -ExecutionPolicy Bypass -File tests/architecture.ps1
powershell -ExecutionPolicy Bypass -File tests/api-smoke.ps1
```

The browser integration sources are also typechecked against `src/Mezhs.Integration.Browser/BrowserModule.d.ts` in CI. The architecture test verifies that integration-specific behavior does not leak back into Electron, API core, or React and that the core integration contract remains browser-free.
