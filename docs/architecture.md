# Architecture and runtime constraints

These are implementation details and architectural boundaries. Confirm them against source when changing the system.

## Agent runtime

`Mezhs.Agent.Api` can execute policy-approved commands on the host operating system. That is an intentional capability boundary, not a sandbox.

The current Agent API trust boundary is the local machine. Both `Mezhs.Agent.Api` and `Mezhs.Agent.Web` must bind only to loopback addresses, and Agent Web may proxy only to a loopback Agent API. There is deliberately no bearer-token layer between these cooperating local processes; if MEŽS later gains a remotely reachable Agent entry point, authentication belongs at that external boundary.

Loopback does not make external event content trusted. A future WhatsApp/Jira/reminder listener may feed untrusted text into a local Agent. Policies for those sources must therefore expose only the capabilities they are intended to have; unrestricted `SH` still means the privileges of the account running MEŽS Agent. Prefer narrow structured tools or real OS isolation where an external source must not have host-shell authority.

Agent shell commands always run from the configured `workspace`. Caller-provided environment variables are denied unless the selected policy explicitly allows their names; `MEZHS_*` variables are reserved for runtime execution context.

## Architecture

`Mezhs.Api` is the reusable core HTTP/API composition. It owns the standard MEŽS services, initialization, exception pipeline and endpoint definitions. `Mezhs.Api.exe` is a thin host over that composition.

`Mezhs.Agent.Api` is a strict superset of the normal API: it hosts the same core composition in-process and then adds Agent policy, reasoning, execution and diagnostic behavior. It does not call a second `Mezhs.Api` process through HTTP. The standard MEŽS Web UI can therefore target either API host, while Agent Web requires `Mezhs.Agent.Api` because only that host exposes the Agent endpoints. Agent API loads Core `mezhs.yaml` and Agent `agent.yaml` independently. Core owns transport, storage and integration connections (including `workspace`, `defaultModel` and per-connection limits); Agent owns its loopback listener, shell workspace, Agent SQLite, messages and policies. Agent API composes the one Core integration registry in-process, and `--mezhs-config` selects the Core YAML independently from Agent `--config`. Policies select a Core connection and may override its model default.

`Mezhs.Api` and `Mezhs.Agent.Api` are alternative API hosts, not cooperating services that should normally run over the same storage. When Agent capability is needed, run the Agent host instead of a second normal API host. Because the core message runtime is in-process, restarting the Agent host also interrupts in-flight model requests; those messages are failed on restart rather than blindly replayed. Detached `Mezhs.Executor` shell work remains independently durable and is reconnected during Agent recovery.

Agent responsibilities are split by semantic owner. `Mezhs.Agent.Api` owns policy, durable root reasoning/admission state, same-chat serialization and root cancellation. `Mezhs.Executor` owns host shell/process lifetime and durable shell execution history. Agent policy validates the concrete `SH` body before Executor receives it; Executor itself does not interpret Agent policy. Agent depends on Executor for shell execution, while Executor remains independent of Agent business logic.

Executor makes each host execution independently observable through one durable SQLite row, one runtime owner process and one owned shell/process. It supports detached execution, heartbeat, timeout, kill, restart lineage, self-restart handoff and lazy reconciliation of stale active owners. Agent API and Agent Web project shell state from Executor instead of maintaining a second shell lifecycle copy.

`Mezhs.Sqlite` owns the generic SQLite connection foundation: path handling, WAL, busy timeout, foreign-key setup and schema helpers. `Mezhs.Log.Sql` builds reusable SQL operations on that foundation. `ExecutorStore` goes through `LogSql` for SQLite access while retaining only execution-specific schema, queries and atomic state transitions; `AgentStore` uses the same `Mezhs.Sqlite` foundation directly for Agent-specific durable state. Core `ChatStore` uses a separate SQLite database for chats, messages, categories and remote state, importing retained legacy chat files once. `Mezhs.Log.Shared` keeps the separate Notes/log-root semantics used by the log CLI rather than imposing them on Executor storage.

SQLite is the source of truth for queued root Agent executions and admission capacity. Workers keep only a small in-memory wake signal, atomically claim eligible work, and enforce same-chat serialization before an execution becomes `Running`. Queued work survives Agent API restart. Work that had already started is requeued for reasoning recovery; previously launched `SH` blocks reconnect to their existing Executor rows using durable command identity rather than executing the host action again.
## Browser connection parallelism

`BrowserIntegrationBase` is the sole admission owner for browser model sends. It emits a separate transient blocked/admitted signal only when capacity or positive start spacing actually delays a send. `MessageService` binds this to the exact user message ID, and `AgentWorker` associates the active Agent send with its root execution. The Agent API overlays effective `Waiting` (orange dot) only while that running message is admission-blocked; durable root status remains `Running`. Provider-reported `waiting`/thinking/progress is message detail only, never an Agent global status. The association and admission flag clear after admission or send exit. Independently running Core/Agent processes do not aggregate permits; use the Agent host for shared chat/Agent sends.\n\nEach `connections[].limits` accepts `maxParallel` (default 1, explicit 0 means unlimited local active sends) and `minInterval` (integer milliseconds; default 0, no start spacing). The same limiter covers authenticated and guest providers. The spacing gate measures monotonic time between local admissions, not the time when an external provider accepts a prompt. `MessageService` already orders locally active sends within one ChatId; different ChatIds can overlap when the connection's limit permits it.

One Electron main process belongs to each browser connection. An account connection shares its authenticated Chromium session but routes each ChatId to its own BrowserWindow/renderer. A guest connection reuses its Electron main process while each model turn uses a fresh, ephemeral Chromium session and window, without carrying guest cookies or local storage to later turns. Operation IDs route local status, progress and results. Cancelling an operation retires only its affected context (best effort), never the shared Electron process or peer ChatIds. A locally cancelled turn can have an unknown remote outcome; there is no implicit replay or guarantee that the provider stopped computation.

`BrowserAccountSession` uses a short shared-host state lock and a separate per-account login gate; only one interactive login starts for concurrently waiting chats, and existing sends can cancel while login waits. Closing the login window ends that attempt without replaying a provider request. It does not serialize full model turns. It preserves `transport.idleMinutes` warm-host shutdown and tracks currently active sends. An Agent root execution temporarily retains its selected connection's browser context through model → shell/wait → model cycles without consuming a model-send permit during shell work; terminal execution releases that connection's retention best effort. Overlapping Agent finalization preserves a context retained by another execution. Ordinary account-chat windows are retired after each local send; the shared Electron host remains subject to its configured idle shutdown.

Model selection and effort are passed with each ChatGPT turn. The existing provider-specific account-level `setModelPreference` mutation has **not** been removed: concurrent different-model/effort behavior still requires live-provider verification. Do not add a global model lock or claim independent served-model selection until that verification is available.

## Integration V2 typed runtime contracts

The connection-scoped `IntegrationRegistry` constructs `IntegrationBase` providers.
`MessageService` persists the integration-generated outgoing user ID **before**
submission and passes an immutable per-turn `ChatContext` and `PromptRequest`
into `PromptAsync`. `ChatContext.GetHistoryAsync` queries Core-owned chat
history on demand, including messages from other connections; integrations
must not open Core storage directly. The `PromptResult` gives Core the
assistant ID, complete reply text, returned model, optional provider
conversation/parent identifiers, deep link, and output files. Core retains
remote continuation state per local chat and connection, including legacy
remote parent IDs; it does not replace persisted local chat IDs.

`BrowserIntegrationBase` retains the single admission owner described above.
`BrowserAccountIntegrationBase` uses the existing shared
`BrowserAccountSession` for authenticated prompting, account login, models,
diagnostics, and Agent context retention. Guest browser integrations use
short-lived anonymous contexts, never the persistent account session.
`ApiIntegrationBase` and `LocalModelIntegrationBase` remain placeholders
until concrete implementations need them. The public integration contract
uses direct model/login/diagnostic methods, not the former capability-module
wrappers. `AllowFiles` gates input attachments; output files do not require
an output capability flag.

Each provider returns the executed model when available, or the best known
current/selected model otherwise. A UI warning compares an explicitly
requested non-default model with the integration-returned model through
persisted reply parent IDs; it never changes the user selection, retries a
prompt, or represents a selected model as independently verified execution.

`IntegrationHttpClient` owns typed GET/POST/PATCH request encoding, response
interpretation (JSON and C# SSE/NDJSON helpers), response-size limits, and
single-dispatch/no-implicit-replay semantics. This is composed by the
lightweight `ApiIntegrationBase` through ordinary `HttpClient`, and by
`BrowserHttpExecutor` through the existing `IChatBrowserTransport`. The
browser operation `typedHttp` executes same-origin `/backend-api/` calls
inside the Electron-owned authenticated session, without duplicating
cookies, authorization or session ownership.
Streaming HTTP response bodies are incrementally framed as bounded SSE/NDJSON
events inside Electron and delivered in order to C# while the request is active.
An acknowledged, bounded event queue provides backpressure separately from
best-effort progress snapshots; invalid frames and delivery failures fail
explicitly. Headers-only GET probes avoid buffering large conversation bodies.
Shared signed binary transfer enforces destination/header validation, credential
separation, redirect rejection and bounded upload/download streaming.

ChatGPT Account models and projects use typed HTTP and C# normalization.
Its prompt preparation, submission mapping, stream interpretation, reply
ancestry, citation handling, file processing and final conversation recovery
are implemented in C#. The provider-specific JavaScript that remains owns
authenticated browser fetch, request-local native Sentinel/conduit preparation
and required browser-page security hooks. Uncertain prompt submissions are
never automatically replayed. The existing provider-level model preference
PATCH remains in place; concurrent distinct-model behavior awaits isolated
live-provider verification.
