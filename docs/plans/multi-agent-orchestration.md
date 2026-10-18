# MEŽS multi-agent orchestration: implementation handoff

**Status:** Architecture agreed in design discussion on 2026-10-17; **not implemented**.  
**Repository:** `DreigoUndead/Mezhs`  
**Planning / implementation branch:** `plan/multi-agent-orchestration` (created from `main`).  
**Scope:** Agent API, Agent API client, a new Console-based CLI, persistence/runtime, integration error handling and pacing, and Agent Web UI.

## 0. Start here in the next chat

Implement on **this existing branch** in reviewable increments. The plan is a behavior contract, not an implementation. Inspect actual code and read `.agents/skills/repository-change-workflow/SKILL.md` and `.agents/skills/software-change/SKILL.md`. Do not merge without explicit approval.

**Dependency:** PR #21 (`agent-model-presets`) was still open/unmerged when this API contract was reviewed. Reconcile its approved model/connection/effort work before relying on it.

**Design rule:** One `/v1/agents` API powers **both root and child creation**, subsequent prompting, Stop/Resume, and inspection for **both Web UI and CLI**. No separate root-agent endpoint, child-agent implementation, or UI-only lifecycle path. Generic `/v1/chats` and `/v1/messages` remain the underlying conversation/transport API, not public Agent orchestration entry points. No global agent-count/worker-slot/queue limits; only integrations throttle provider requests. Agents are cooperative local-machine tools, not security sandboxes.

## 1. Existing integration points to inspect

- `src/Mezhs.Console/ConsoleApplication.cs` — existing ConsoleBase-like command framework with `[Command]` discovery. Check its synchronous invocation constraints and build the new CLI on it.
- `src/Mezhs.Agent.Api/Program.cs` — Agent routes/hosting.
- `src/Mezhs.Agent.Api/` — `AgentService`, `AgentWorker`, `AgentStore`, policy models, `AgentPromptBuilder`, command interpreter/shell integration.
- `src/Mezhs.Agent.Api.Client/AgentApiClient.cs` — shared typed HTTP client; extend rather than implement raw ad hoc HTTP per command.
- `src/Mezhs.Agent.Web/src/App.tsx` — Agent UI and current policy/connection/model picker.
- `src/Mezhs.Api/` and integration base classes — model send, error recovery, account-session pacing and integration registry.
- `Mezhs.Executor` — durable shell/process execution and kill/restart ownership, separate from Agent orchestration.

The current root Agent runtime is queue-/SQLite-backed; detached shell work has its own durable Executor records. Child agents and agent hierarchy management are new features. Avoid creating competing sources of truth for execution or process lifetime.

## 2. Core objects and invariants

- **Agent** is the public resource name, represented by an existing underlying Agent chat ID. `agentId` in the API is that same chat ID (no second identity). An **execution** is an individual task/request. Parent-child trees are persistent.
- Root agents have `parentId: null`; children reference a parent agent ID and have a unique direct-child name. UI and CLI use **the same Create operation**; differences are only request values, not alternate server logic.
- Each agent has a fixed policy ID. **Current `agent.yaml` is the only source of policy values**, including depth/capability rules; remove cached authoritative policy objects/snapshots.
- Parent tools operate on descendants, not unrelated agents. This is cooperative tool behavior, **not a security guarantee**: unrestricted SH can call APIs or change/redeploy the host.
- Children never automatically message or activate parents; parents inspect or wait explicitly.
- No global or per-policy count, active-agent, queue, or Agent execution-slot limit. Per-agent ordering and integration-account throttling still apply.
- Persist agent/parent/name/execution state independently of OS process presence. A stopped/failed child remains visible.

## 3. Unified Create for root and child agents; duplicate is always an error

**One public endpoint:** `POST /v1/agents` creates an agent and starts its initial task **atomically from the caller's perspective**. It returns the assigned agent ID **and** initial execution ID immediately. The UI's "New Agent → enter first prompt → Send" and CLI's `Create` invoke this **same API operation**; UI does not have to poll an execution until the new chat ID is assigned. Merely opening the UI's new-agent composer remains a local, unsaved draft.

Conceptual request (root):

```json
{ "parentId": null, "name": "My agent", "policyId": "manual", "task": "Investigate X", "connectionId": "chatgpt-sub", "model": null }
```

Child creation differs only in parameters: `{ "parentId": "chat_parent", "name": "worker-x", "policyId": "developer", "task": "Implement X" }`.

- `parentId` is **optional in the request body**; missing or null means root. A child must supply a nonempty `name`; a root may omit it, letting the UI/backend derive a display title from the task. Name uniqueness applies only to one parent's **direct** children, not root-agent titles.
- A second Create with an existing direct-child name **always fails** (`409 Conflict`), even if every parameter matches. Include `existingAgentId` in the error; do not implement idempotent reuse, fingerprints or silent restart.
- Atomically reserve `(ParentId, Name)` with a SQLite uniqueness constraint; allocate the agent chat ID before returning. Since AgentStore SQLite and the underlying ChatStore files are separate, make chat materialization and initial execution startup recoverable/reconcilable after crashes. An accepted Create may be queued but must not be silently lost.
- The API returns `201 Created`, `Location: /v1/agents/{agentId}`, and at minimum `{ agentId, executionId, status }`; do not report execution completion on creation. Child-name conflicts return `409` without new work. Other invalid fields/policy/depth return actionable errors without phantom child records.
- CLI inside an agent defaults omitted `parentId` to the current agent's ID (a child). Standalone CLI with omitted `parentId` creates a root, identical to UI. Explicit `parentId` selects a parent in the same API; cooperative ancestry/policy checks apply. **The API itself never assumes that every Create is a child**.
- Successful CLI Create prints **only** the new `agentId`; errors go to stderr with nonzero exit.

## 4. Live YAML policy

Illustrative values: `manual.subAgents.maxDepth: 2` / `onDone: RejectIfActive`; `delegator: 1 / Accept`; `developer: 0 / StopAll`.

- The **policy ID** assigned to a chat is immutable, but settings are read from the current **agent.yaml** whenever used. No stored policy snapshots. YAML changes affect existing agents. Read/update the file atomically; reject invalid configuration instead of silently using stale policy values.
- Depth: 0 forbids children, 1 allows direct children, 2 allows grandchildren. A child policy cannot increase a current ancestor's remaining depth. Check all ancestors against the current YAML.
- The current policy controls allowed commands/environment and allowed connections/models/effort; selected integration must not bypass those cooperative rules.
- `RejectIfActive`: reject DONE if any descendant is Queued, Running, or Waiting; correction includes active descendant IDs. Stopped, Failed, Cancelled and Completed do not block.
- `StopAll`: recursively request Stop of active descendants and their shell process trees, then accept DONE. Do not wait indefinitely for OS process termination.
- `Accept`: DONE is accepted and children continue; no automatic parent wake.
- Synchronize descendant admission with a parent's terminal DONE transition so a child cannot be created between an active-descendant check and completion. A short SQLite transaction/state check is enough.
- No policy or Agent runtime `maxParallel`, global agent-count or queue-capacity limit.

## 5. Stop, Resume and Prompt — same API for UI and agents

Conceptual methods: `Stop(chatId, tree:false)`, `Resume(chatId, tree:false)` and `Prompt(chatId, text, connection?, model?)`.

**Stop:** Mark the chat stopped; forbid further automatic model continuations, shell launches and agent-management command launches. Request kill of the running shell execution and **all processes associated with its execution ID** (OS process-tree/group ownership where possible). Kill is a request, **not** a wait for every process to disappear. `tree:true` also stops descendant *agents*; do not confuse that with killing one shell execution's process tree. Let an already-submitted provider request finish and persist its reply; do not execute reply commands while stopped or blindly resend an uncertain provider request.

**Prompt at any time:** UI and parent agent use the identical Prompt API. Keep a persistent, ordered queue of incoming prompts. **Prompt never interrupts a running SH or in-flight model request, and never implicitly Stop/Resume.**
- **Running:** Before each new assistant command (including DONE), atomically check queued prompts. If any exist, submit them to the model *before* that command, alongside results of already completed commands; discard all remaining unexecuted commands from the superseded assistant response. The new assistant response becomes the current one. If a prompt arrives during SH, SH finishes and its result is retained first. If a prompt arrives during a provider request, apply it **after** that response arrives and **before** its commands.
- **Stopped:** explicit prompts are submitted serially to the model and their responses saved; **none** of their commands run. Multiple prompts yield multiple replies, but only the **latest assistant response** is eligible for Resume.
- **Completed/Failed:** Prompt is still allowed; start a new conversation execution in the same chat and same current policy, not an implicit Retry.
- Preserve per-chat ordering, queue/consumption state across restart, and ensure an old in-flight model reply cannot replace newer explicitly requested work. Record replies but never launch superseded commands.

**Resume:** Clear Stopped and run executable commands **from the very latest assistant response from the beginning, in all cases, regardless of previously successful command attempts**. Never replay any older assistant response and never continue a killed shell process from its previous command position. If no assistant response exists yet, continue to the required model turn; handle a reply with no commands via normal DONE/continuation logic. Repeating ordinary shell side effects is accepted. A stopped Wait loses elapsed time; Resume executes it anew with the full timeout. `tree:true` resumes descendants, even those separately stopped. No stop-reason layers, command checkpoints, or special Inject command.

**Atomic boundary:** the pre-command prompt check and launching the next command must share a serialization boundary. If command launch wins, a newly arriving prompt applies *after* that command. If prompt enqueue wins, the prompt is processed *before* that command.

## 6. Wait semantics: first agent to stop working

Conceptual commands: `Wait(timeoutSeconds)`, `WaitFor(chatId, timeoutSeconds)`, `WaitAny(chatIds[], timeoutSeconds)`, `WaitAll(chatIds[], timeoutSeconds)`.

- Every wait has a finite required timeout. `Wait` is a pure timer.
- “Stopped working” means **Stopped, Completed/DONE, Failed or Cancelled**. Queued, Running or Waiting are still working.
- `WaitFor` returns when the selected child first reaches any stopped-working state. `WaitAny` returns on the **first** stopped-working child, for *any* reason, not just DONE. `WaitAll` returns when all selected children have stopped working, even with mixed terminal outcomes.
- If an observed child is already terminal, return immediately. Validate IDs, existing execution and descendant relationship. Use a coherent current execution/status view, never stale DONE from an earlier task. Deterministic tie-breaking for simultaneous events.
- Return chat ID, execution ID, **actual status**, latest assistant response (when present), and error details. For WaitAny include the triggering child; for WaitAll include each child's state. Failed/Stopped/Cancelled are **normal observed results**, not command errors.
- A timeout returns `TimedOut` and IDs still working, without terminating the parent. Only validation/transport errors get a nonzero CLI exit.
- No automatic notification or activation of a parent that is not waiting.
- **Do not build a persistent wait-job table or special suspension scheduler in V1.** Bounded CLI/API polling or waiting on persisted current state is sufficient without a fixed agent-worker slot pool. Stop kills the waiting shell command; Resume restarts the latest-response wait with a fresh timeout.
- Every agent command can override its default command timeout. A `WaitFor(...,600)` invoked via SH must set a suitable outer command/Executor timeout greater than its wait duration; the default 120 seconds must not cut it short.

## 7. Retry and integration/model selection

- Automatic recovery, UI Retry button and CLI `Retry(chatId)` call one **virtual integration-base HandleError** path, overridable by provider. It receives error/request/attempt context and can decline recovery. No separate CLI retry implementation.
- Retry applies to the identified failed provider/model request; it is not generic replay of shell commands or the entire agent execution. Prompt and Resume are different actions.
- **Never automatically resubmit an uncertain provider request.** Provider-specific optional backoff/Retry-After behavior belongs to the integration, not Agent retry-number/delay YAML.
- Use existing shared connection/model/thinking-effort selection and execution-start plumbing (reconcile PR #21), subject to the live policy. A model/integration switch never changes chat policy ID. Root UI may show Retry for provider errors; parents can use CLI for descendants.

## 8. Integration/account pacing is the sole throttle

- Remove fixed Agent worker slots, global queue-capacity/admission restrictions and maxParallel policy. Agent count is not artificially limited.
- Each integration may independently configure/request pacing, e.g. `minIntervalMs` and `maxConcurrentSends`, grouped by authenticated provider account where applicable. **No mandatory global three-second delay or serialized-send limit.**
- Requests from human UI, master, child and Retry sharing an authenticated account obey that integration/account's configured rules, including across differently named connections. Different accounts need not share limits.
- Separate minimum interval between submission starts from maximum simultaneous provider requests. A provider chooses whether an in-flight limit spans whole streaming responses or only the submit phase.
- Per-chat model turn ordering remains serialized; account pacing should not block incoming prompt registration/status inspection.

## 9. Agent Web UI: API parity, actual statuses, prompt always available

- Persistent collapsible parent/descendant tree with human-readable names and IDs, sourced from `GET /v1/agents` using `parentId` and `recursive` when helpful. No separate `/sub-agents` endpoint.
- Indicators show **actual state**, without separate human unread/read-completion flags: Completed green; Failed red; Waiting orange; Running animated/active; Stopped gray; Queued neutral. Define precedence consistently.
- Clicking New Agent opens a **local draft only**; sending its first task uses `POST /v1/agents` (the same API as CLI Create). The response already includes `agentId`/`executionId`; delete the old `waitForAttachedChat` UI polling logic.
- **Keep the existing-agent input enabled when Running, Waiting, Stopped, Completed or Failed.** Existing-chat Send uses `POST /v1/agents/{agentId}/prompt`, same as CLI Prompt. Remove the current `activeExecution` and `paused` input/send guards in `App.tsx`.
- Show queued versus submitted prompts honestly; input never silently stops a running command.
- Stop, Stop All, Resume and Resume All use the **same Agent API** as the CLI. UI execution-level shell Kill/Restart buttons use the existing `/v1/executions/{id}/kill` and `/restart` endpoints, also exposed by CLI commands; these are *not* alternate Agent Stop/Resume implementations.
- UI policy/connection/model choice, runtime, messages, execution history and debug log use the same Agent API reads as CLI. Underlying transport-only `/v1/chats` and `/v1/messages` remain available to the runtime but are not used to bypass Agent lifecycle in either frontend.

## 10. CLI command inventory — added, updated, removed

`Mezhs.Agent.Cli` is a **new executable** using existing `Mezhs.Console` `[Command]` discovery/binding and the **same typed `Mezhs.Agent.Api.Client`** used by Agent Web (through its HTTP endpoints). Do not invent a separate command parser or alternate business logic.

There is **no existing `Mezhs.Agent.Cli`**, so every CLI function below is **ADDED** in the new CLI; the "API backing" column makes clear whether its underlying server operation is new, updated or existing.

| CLI method (conceptual) | API backing | Behavior |
| --- | --- | --- |
| `Create(policy, task, name?, parentId?, connection?, model?, effort?)` | **Added** `POST /v1/agents` | From UI/standalone: root if no parent; from agent: default parent is current agent; child name required |
| `List(parentId?, recursive=false)` | **Renamed/updated** `GET /v1/agents` | No filter lists all visible agents; with parentId lists children; recursive traverses descendants. CLI may default parent to current execution context |
| `Get(agentId)` | **Renamed/updated** `GET /v1/agents/{id}` | Metadata, policy ID, actual state and parent/name |
| `Inspect(agentId)` | **Reused reads** | Aggregate Get, runtime, executions and latest reply locally; no special Inspect endpoint |
| `Messages(agentId)` | **Renamed** `GET /v1/agents/{id}/messages` | Agent protocol-aware history |
| `Log(agentId)` | **Renamed** `GET /v1/agents/{id}/debug-log` | Existing debug log |
| `Prompt(agentId, text, connection?, model?, effort?)` | **Added** `POST /v1/agents/{id}/prompt` | Anytime prompt injection; accepted/queued response |
| `Retry(agentId)` | **Added** `POST /v1/agents/{id}/retry` | Shared integration HandleError |
| `Stop(agentId, tree=false)` | **Added** `POST /v1/agents/{id}/stop` | Same as UI Stop; optionally subtree |
| `Resume(agentId, tree=false)` | **Added** `POST /v1/agents/{id}/resume` | Same as UI Resume; latest reply only |
| `Wait(timeoutSeconds)` | **CLI-only** | Bounded timer; no server wait entity |
| `WaitFor(agentId, timeoutSeconds)` | **Reuse** agent status reads | First stopped-working condition for one child |
| `WaitAny(agentIds[], timeoutSeconds)` | **Reuse** agent status reads | First selected child to stop working |
| `WaitAll(agentIds[], timeoutSeconds)` | **Reuse** agent status reads | All selected children stop working |
| `Policies()` | **Existing** `GET /v1/policies` | Live policy discovery |
| `Connections()` | **Existing** `GET /v1/connections` | Connection discovery |
| `Models(connectionId)` | **Existing** `GET /v1/connections/{id}/models` | Model/effort discovery |
| `KillExecution(executionId)` | **Existing** `POST /v1/executions/{id}/kill` | Same low-level shell Kill action exposed by UI |
| `RestartExecution(executionId)` | **Existing** `POST /v1/executions/{id}/restart` | Same low-level shell Restart action exposed by UI |

**Removed/obsolete command concepts:** no `CreateChild`, `CreateRoot`, `TryCreate`, `Pause`, `Inject`, independent `CancelAgentExecution`, or separate `StartAgentExecution`. Root and child use one `Create`; follow-ups use `Prompt`; interrupt/replay use `Stop`/`Resume`. `Help`, `Validate`, and `Test` are inherited from `Mezhs.Console` rather than added anew.

**CLI argument behavior:** A manual standalone `Create` with no parent creates a root; if invoked within a MEŽS Agent execution, an omitted parent defaults to the current agent. Explicit parent IDs are accepted subject to normal policy/cooperative hierarchy checks. The CLI constructs a request and calls the same server API; it **does not** implement creation locally.

**Per-command timeout:** Agent protocol/parser/Interpreter/Executor must support a finite per-command timeout override, including SH running long Wait CLI commands. Keep the default in current YAML policy; avoid outer 120-second timeout truncating a longer explicitly requested wait. Exact optional parser syntax is an implementation decision to validate against current command parsing.

## 11. Agent API and typed client — explicit change ledger

**Scope of renaming:** `/v1/agents` names the **Agent orchestration resource**. It replaces Agent API's `/v1/agent-chats` routes. It does **not** rename generic underlying conversation endpoints `/v1/chats`, `/v1/messages`, files, or integration endpoints. In routes, `agentId` is the same value previously called `chatId`, not a new GUID or foreign-key layer.

### 11.1 Added endpoints

| Method | Route | Purpose and caller contract |
| --- | --- | --- |
| POST | `/v1/agents` | **Unified root/child creation plus initial task**, optional body `parentId`, returns `201 {agentId, executionId, status}`. Both UI New Agent and CLI Create |
| POST | `/v1/agents/{agentId}/prompt` | Anytime follow-up/injected prompt; immediately accept and persist in arrival order, return `202 {promptId, status}`. Both UI Send and CLI Prompt |
| POST | `/v1/agents/{agentId}/retry` | Same integration `HandleError` used by automatic recovery and UI/CLI Retry |
| POST | `/v1/agents/{agentId}/stop` | Request Stop with optional body `{ "tree": false }`; stop active shell execution/process tree; UI/CLI identical |
| POST | `/v1/agents/{agentId}/resume` | Resume with optional `{ "tree": false }`; latest assistant reply only; UI/CLI identical |

**Creation is one operation, not two:** do not add `POST /v1/agents/{parentId}/sub-agents`, a separate root-create route, or a second `POST /v1/executions` path for UI new-agent submission. The backend owns transactional registration and crash reconciliation; no UI/CLI multi-call create workflow.

### 11.2 Renamed / updated endpoints

| Existing route | Replacement / action | Change |
| --- | --- | --- |
| GET `/v1/agent-chats` | GET `/v1/agents?parentId={id}&recursive={bool}` | **Rename + extend.** No `parentId` means all agents (UI can render tree); supplied parentId selects direct children, `recursive=true` includes descendants. No separate `/sub-agents` GET |
| GET `/v1/agent-chats/{chatId}` | GET `/v1/agents/{agentId}` | **Rename + extend.** Include `agentId`, `parentId`, `name`, immutable policy ID, actual state and current runtime summary |
| GET `/v1/agent-chats/{chatId}/runtime` | GET `/v1/agents/{agentId}/runtime` | **Rename + extend.** Include stopped/waiting/running, active shell, model, pending prompt state as needed |
| GET `/v1/agent-chats/{chatId}/messages` | GET `/v1/agents/{agentId}/messages` | **Rename** parsed Agent history with protocol information |
| GET `/v1/agent-chats/{chatId}/debug-log` | GET `/v1/agents/{agentId}/debug-log` | **Rename** existing Agent-specific downloadable debug log |
| GET `/v1/executions?chatId={id}` | GET `/v1/executions?agentId={id}` | **Change query parameter name** for filtering Agent + Shell execution history; omitted ID still lists executions |
| GET `/v1/executions/{executionId}` | Same path, **updated** state/result | Preserve single execution inspection; expose accurate new Agent states |
| GET `/v1/policies`, GET `/v1/policies/{policyId}` | Same paths, **updated behavior** | Resolve current YAML (no policy snapshots); policy ID remains fixed per agent |

### 11.3 Removed / replaced endpoints

| Existing endpoint | Replacement / reason |
| --- | --- |
| GET `/v1/agent-chats/{chatId}/executions` | Use **existing** `GET /v1/executions?agentId={id}`; one owner for execution listing |
| PATCH `/v1/agent-chats/{chatId}` (body `paused`) | Remove old pause toggle; **Stop**/`Resume` actions have real lifecycle semantics |
| POST `/v1/executions` (Agent start; first/follow-up task) | Replace public Agent submission: **Create** root/child via `POST /v1/agents`, **Prompt** existing via `POST /v1/agents/{id}/prompt`. No second Agent entry path |
| POST `/v1/executions/{executionId}/cancel` (root Agent cancellation) | Agent chat stop now uses `POST /v1/agents/{id}/stop`; preserve separately named low-level Executor Kill below |
| All old `/v1/agent-chats/*` routes | Remove once UI, client and tests migrate to `/v1/agents/*`; do not leave independent old handlers |

**Compatibility:** These are intended **V1 breaking changes** to the Agent-facing API. Search/update all repository and test callers before removing old routes. If temporary compatibility is necessary for an external consumer, its alias must delegate to the **same** canonical Agent service, never run a parallel state machine; remove when migrated.

### 11.4 Existing endpoints explicitly retained

| Method | Route | Use |
| --- | --- | --- |
| GET | `/v1/executions`, `/v1/executions/{id}` | Agent/Shell execution list and detail (list filter renamed above) |
| POST | `/v1/executions/{id}/kill`, `/v1/executions/{id}/restart` | **Shell Executor only**; UI and CLI both expose KillExecution/RestartExecution; **not Agent Stop/Resume** |
| GET | `/v1/connections`, `/v1/connections/{id}/models` | Integration/model/effort discovery (reconcile PR #21) |
| GET | `/v1/policies`, `/v1/policies/{id}` | Live policy discovery |
| All | `/v1/chats/*`, `/v1/messages/*`, `/v1/files/*` | Existing generic transport/conversation/file API for runtime and other consumers; no rename or second Agent orchestration path |

No separate API `/v1/waits`, `/v1/agents/{id}/waits`, `/v1/agents/{id}/sub-agents`, or `/v1/agents/{id}/inspect` is introduced. CLI Wait observes existing Agent status; Inspect composes existing read endpoints.

### 11.5 `AgentApiClient` method ledger (C#)

The **typed** `src/Mezhs.Agent.Api.Client/AgentApiClient.cs` owns HTTP calls; UI sends the same HTTP contracts. Do not implement policy/Stop/Resume/Prompt logic in clients.

| Method | Change | Maps to |
| --- | --- | --- |
| `CreateAgentAsync(request)` | **Add** | POST `/v1/agents` |
| `GetAgentsAsync(parentId?, recursive?)` | **Rename/extend** old `GetAgentChatsAsync` | GET `/v1/agents` |
| `GetAgentAsync(agentId)` | **Rename** old `GetAgentChatAsync` | GET `/v1/agents/{id}` |
| `GetAgentRuntimeAsync(agentId)` | **Add** | GET `/v1/agents/{id}/runtime` |
| `GetAgentMessagesAsync(agentId)` | **Add** | GET `/v1/agents/{id}/messages` |
| `GetAgentDebugLogAsync(agentId)` | **Add** | GET `/v1/agents/{id}/debug-log` |
| `PromptAgentAsync(agentId, request)` | **Add** | POST `/v1/agents/{id}/prompt` |
| `RetryAgentAsync(agentId)` | **Add** | POST `/v1/agents/{id}/retry` |
| `StopAgentAsync(agentId, tree=false)` | **Add** | POST `/v1/agents/{id}/stop` |
| `ResumeAgentAsync(agentId, tree=false)` | **Add** | POST `/v1/agents/{id}/resume` |
| `GetExecutionsAsync(agentId?)` | **Update** | GET `/v1/executions?agentId` |
| `GetExecutionAsync`, `KillExecutionAsync`, `RestartExecutionAsync` | **Keep**, adapt DTO as needed | Existing execution detail/shell actions |
| `GetPoliciesAsync` | **Keep**, live YAML value handling | GET `/v1/policies` |
| `GetConnectionsAsync`, `GetModelsAsync` | **Add/reuse existing if available after PR #21** | Existing discovery endpoints |
| `SetAgentPausedAsync`, `CancelExecutionAsync`, public Agent `StartExecutionAsync` | **Remove/retire** | Replaced by Stop/Resume, Create and Prompt |

Keep the separate general `MezhsApiClient` chat/message transport methods for their existing consumers. They **must not** become an alternate Agent Prompt or Create implementation.

### 11.6 UI ↔ CLI action-parity rule

| User action in Agent Web | CLI command | Exact canonical API |
| --- | --- | --- |
| New Agent + first task | Create (no parent) | POST `/v1/agents` |
| Create child (tree/action, if exposed) | Create (with parent) | POST `/v1/agents` |
| List/open agent | List/Get | GET `/v1/agents`, GET `/v1/agents/{id}` |
| Send prompt while any state | Prompt | POST `/v1/agents/{id}/prompt` |
| Stop / Stop All | Stop | POST `/v1/agents/{id}/stop` |
| Resume / Resume All | Resume | POST `/v1/agents/{id}/resume` |
| Retry failed provider send | Retry | POST `/v1/agents/{id}/retry` |
| Read messages/runtime/log/history | Messages/Inspect/Log/Get | GET Agent endpoints and `/v1/executions?agentId` |
| Kill/Restart selected shell execution | KillExecution/RestartExecution | POST `/v1/executions/{id}/kill` or `/restart` |
| Choose policy/connection/model/effort | Policies/Connections/Models + Create/Prompt | Existing discovery endpoints; selections in same Create/Prompt bodies |

**Response/ordering requirements:** Create returns `agentId` + first execution ID without UI polling; Prompt returns an accepted prompt ID/status quickly (may be queued behind SH/provider, or allow model reply while stopped). The Agent service persists/serializes all state changes. Stop/Resume responses report the transition/request, not false claims that every OS process is already gone. Existing per-agent messages/execution reads give the UI/CLI the actual outcome.

## 12. Critical scenarios / verification checklist

1. **One Create:** UI New Agent Send and standalone CLI Create send identical `POST /v1/agents` shape; root `parentId=null`; agent-invoked child `parentId=current agent`; both receive `agentId` and `executionId` immediately. No duplicate implementation and no `waitForAttachedChat`.
2. **Child collision:** same direct-child name always returns `409` with existing agent ID (including same parameters); same name under another parent succeeds; races admit only one; crash between SQLite reservation/chat materialization is recoverable.
3. **Agent list:** `GET /v1/agents` all agents for tree; `?parentId=X` only direct children; `recursive=true` descendants; same method supports CLI List and UI tree.
4. **UI/CLI parity:** every Agent Web action has a CLI command using the **same endpoint**, including selected shell execution Kill/Restart. No direct UI-only route or second creation/submission path.
5. **Migration:** no surviving independent `/v1/agent-chats/*`, `POST /v1/executions` Agent submission, legacy `paused` PATCH or root Agent `/cancel` path after client/test migration. Generic `/v1/chats`/`/v1/messages` remain intact.
6. **No slots:** many parents/children waiting and running; no global agent-count/worker/queue admission cap; integration pacing still works.
7. **Live policy:** valid YAML change affects existing agent's next policy check; no policy snapshots; current depth/command/connection/model/effort constraints.
8. **Stop/Resume:** Stop kills active shell execution process tree without indefinite wait; in-flight provider response saved; Resume executes latest assistant response's commands only, from start, regardless of earlier successes.
9. **Prompt:** UI and CLI send anytime; prompt during SH waits for command completion and retains its result; prompt during model request applies after response/before commands; queued prompt outranks resumed older response; prompts before DONE and prompt/command-launch races tested.
10. **Stopped prompting:** explicit prompts still submit model turns and save responses without executing commands. Multiple responses: Resume runs only latest. Late old replies never run superseded commands.
11. **Tree:** Stop/Resume single vs recursive; Resume All includes individually stopped children; shell process-tree kill differs from child-agent subtree Stop.
12. **DONE policy:** RejectIfActive with IDs; StopAll recursive; Accept descendants continue; create-vs-DONE race doesn't admit a late child.
13. **Wait:** CLI timer; For/Any/All observe Stopped/DONE/Failed/Cancelled (including initially terminal and mixed WaitAll), finite timeout normal result; no separate wait endpoints/parent notifications; prevent stale DONE.
14. **Timeout:** per-command override survives SH and Executor, e.g. 600-second Wait not killed by default 120 seconds.
15. **Retry/model:** UI/CLI Retry same virtual HandleError; no blind resend on uncertain acceptance; selection uses shared Create/Prompt API and approved PR #21 model picker work.
16. **Status/UI:** actual state only; input enabled while busy/stopped; queued prompt receipt accurate; tree persists across restart.
17. **Restart:** pending prompts/children survive; no duplicate uncertain provider submissions; model-reply and prompt-order races covered.
18. **Repository:** build/test/migration search/review and one unmerged PR; no unrelated changes.

## 13. Suggested implementation sequence

1. Reconcile approved PR #21 dependency; refactor lifecycle around **one Agent API**, Stop/Resume and a pre-command prompt-injection boundary.
2. Establish `/v1/agents` as the public Agent resource, allocate stable agent/chat ID before initial execution, and add **one Create** for root or child; migrate UI New Agent and CLI Create together.
3. Remove global Agent worker/queue admission limits; preserve per-agent ordering and integration/account pacing only.
4. Persist parent/name and live YAML policy resolution; implement atomic named-child conflicts and restart reconciliation.
5. Implement `Prompt` during all states, shared Stop/Resume/Retry, and appropriate Shell Executor process-tree kill; update UI composer and remove legacy Pause/Cancel route behaviors.
6. Migrate renamed Agent read endpoints and GET `/v1/executions?agentId`; remove obsolete `/v1/agent-chats`, public `POST /v1/executions` Agent submission, and redundant per-Agent execution-list route. Verify **all** repository/test call sites before deletion.
7. Extend typed `AgentApiClient` and build the Console CLI inventory, including shell Kill/Restart parity and per-command timeout overrides.
8. Implement bounded Wait/WaitFor/WaitAny/WaitAll over persisted Agent state, without a durable wait-job subsystem.
9. Integrate virtual provider HandleError, model/connection/effort selection and integration-only account pacing.
10. Build persistent UI tree/real status indicators; run full migration, restart, race and E2E tests; review a single PR and **leave unmerged**.

### Explicit V1 non-goals

- No workspace/worktree manager or workspace locks in orchestration.
- No global Agent count/worker-slot/queue-capacity or policy maxParallel limits.
- No separate root-Agent or child-Agent API/CLI implementation; no `/sub-agents` endpoint.
- No automatically injected child messages or parent wake.
- No sibling-to-sibling management tool, no artificial shell sandbox security.
- No duplicate Create idempotent reuse/fingerprint.
- No Prompt-triggered implicit Stop/Resume, separate Inject command, or command-position checkpoint.
- No killed-shell process resume or blind replay of uncertain provider submissions.
- No policy snapshots, unread tracking, persistent wait-job scheduler, or Agent retry-count/delay YAML.
- No duplicate transport-level `/v1/chats`/`/v1/messages` implementation in Agent API.

**Definition of done:** Agent Web UI and CLI use **the exact same Agent API routes** for all equivalent actions, including one root-or-child Create, anytime Prompt and shared lifecycle. Added/renamed/removed endpoints and typed client methods match the explicit ledgers above; tests pass; present one reviewable **unmerged** PR.

