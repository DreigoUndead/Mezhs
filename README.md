# MEŽS

**One interface for several AI services, with optional policy-controlled AI agents.**

MEŽS is a self-hosted application for using multiple AI providers through one API and a shared web interface. Its integration layer handles the different ways providers work: browser sessions, account sessions and other transports. You can use MEŽS as an ordinary chat application or run the separate Agent host when you want AI-assisted tasks with controlled command execution.

## What can it do?

- **Chat across providers:** ChatGPT, Gemini and Grok integrations are included, with account and guest modes where supported.
- **Unified conversations:** shared chat UI, conversation history, model selection where supported, and file handling.
- **Run AI agents:** agent chats, configurable policies, command execution, progress and execution history.
- **Recover ongoing work:** durable queued tasks and shell execution records help Agent recover from restarts.
- **Integrate with tools:** HTTP endpoints expose chats, messages, connections, files, diagnostics and, in Agent mode, agent-specific operations.

Provider availability and authentication depend on the selected integration. Some browser integrations depend on third-party interfaces that can change without notice.

## Getting started

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js and npm](https://nodejs.org/) (Node.js 22 is used by the CI workflow)
- A desktop environment for the Electron browser integrations

Clone the repository and run these commands from its root:

```sh
npm install --prefix electron
dotnet build Mezhs.sln -c Release
```

The .NET Web projects build their React frontends during `dotnet build`. The Electron sidecar is installed separately.

### Option 1 — ordinary chat

In **two terminals**, from the repository root:

```sh
dotnet run --project src/Mezhs.Api -c Release --no-build
```

```sh
dotnet run --project src/Mezhs.Web -c Release --no-build
```

Open **http://127.0.0.1:5173**. The API listens at **http://127.0.0.1:5050** with the checked-in `mezhs.yaml`.

### Option 2 — Agent mode

Instead of the ordinary API host, start the Agent API and Agent Web in **two terminals**:

```sh
dotnet run --project src/Mezhs.Agent.Api -c Release --no-build
```

```sh
dotnet run --project src/Mezhs.Agent.Web -c Release --no-build
```

Open **http://127.0.0.1:5174**. The Agent API listens at **http://127.0.0.1:5060** with the checked-in `agent.yaml`.

The Agent API includes the ordinary API, so you usually run **either** the ordinary API or Agent API, not both against the same storage.

### Configuration

- [`mezhs.yaml`](mezhs.yaml): standard API, integrations, Electron transport and storage.
- [`agent.yaml`](agent.yaml): Agent host, policies, workspace and execution settings.

Change the configured connections for the providers you use. Account integrations may require a browser login. The APIs support a custom config path using `--config <path>`.

**Security:** Agent mode can execute policy-approved shell commands with the host user's privileges. This is **not a sandbox**. Agent API and Agent Web are intended to run on loopback only. Review policies before allowing agents to act, especially on untrusted input.

## How it's organized

| Component | Role |
|---|---|
| `Mezhs.Api` | Core HTTP API for providers, chats, messages and files |
| `Mezhs.Web` / `Mezhs.Web.Lib` | Web host and reusable React chat interface |
| `Mezhs.Agent.Api` / `Mezhs.Agent.Web` | Agent orchestration, policies, command results and dashboard |
| `Mezhs.Executor` | Durable command/process execution |
| `electron/` and integrations | Browser transport and provider-specific integrations |

The Agent host extends the same API composition as the ordinary host. The Agent owns policy and task reasoning; Executor owns process execution and history.

## Documentation and development

- [Architecture](docs/architecture.md) and [engineering guidelines](.agents/coding.md)
- [Development workflow and agent instructions](AGENTS.md)
- [Current behavior specifications](specifications/current/)
- [Current release version](VERSION) and [release history](CHANGELOG.md)
- [Agent design manifesto](AGENT_MANIFESTO.md) and [Executor design manifesto](EXECUTOR_MANIFESTO.md)

Changes use a risk-based workflow: small edits can be handled in one chat, while more complex work receives independent review and testing. Every implementation merge requires the project owner's explicit approval.