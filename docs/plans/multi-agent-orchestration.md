# MEŽS multi-agent orchestration: implementation handoff

**Status:** Architecture agreed in design discussion on 2026-10-17; **not implemented**.  
**Repository:** `DreigoUndead/Mezhs`  
**Planning / implementation branch:** `plan/multi-agent-orchestration` (created from `main`).  
**Scope:** Agent API, Agent API client, a new Console-based CLI, persistence/runtime, integration error handling and pacing, and Agent Web UI.

## 0. Start here in the next chat

Implement this plan on **this existing branch** in reviewable increments. Inspect the actual current repository before coding; this is a desired behavior contract, not a claim that the named endpoints/classes already exist. Read `.agents/skills/repository-change-workflow/SKILL.md` and `.agents/skills/software-change/SKILL.md`, follow their review/test/merge rules, and do not merge without explicit review approval. Preserve the MEŽS local-machine policy/trust boundary.

**Important dependency:** At planning time, per-execution integration/model selection and thinking-effort presets were being developed on `agent-model-presets` (PR #21), not necessarily merged into `main`. Verify its status and reconcile the necessary changes before depending on those features. Do not silently assume the plan branch already contains them.

The chief design preference is **one simple, shared mechanism per responsibility**: UI and CLI call the same Agent API, no duplicate lifecycle implementation, no auto-injected child notifications, no command-level checkpoints, no speculative concurrency limits.

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

- A top-level agent is a chat/execution created by a human or an external entry point (e.g. a future WhatsApp listener).
- An agent chat may own named **direct child agent chats**, forming a persistent tree. Children can own children up to the effective depth limit.
- Each chat selects exactly one immutable **policy** on creation. A parent may select any configured policy for a child, subject to depth, permission, and policy capability checks.
- A parent agent may manage **any of its descendants**, direct or nested. It may not manage siblings, ancestors, unrelated agents, or other trees through the agent-management interface. Human UI authorization is separate.
- API authorization must derive the calling agent identity from a trusted server-side execution context. Never trust a supplied `parentId` or `callerChatId` as proof of authority.
- Only parent/descendant management communication: no direct sibling-to-sibling agent-management messaging.
- No automatic message/result delivery from completed children to a parent. The parent asks using Inspect/Wait.
- Child chat IDs identify agents. Executions represent individual requests/tasks for those agents.
- Child failures and stops must be inspectable; keep a persistent tree, not ephemeral child processes only.

## 3. Naming and idempotent Create

CLI conceptual signature:

```csharp
Create(
    name: "developer-feature-x",
    policy: "developer",
    task: "Implement feature X",
    connection: "chatgpt-account",
    model: "gpt-6-high"); // optional defaults may be resolved
```

- Command name is **`Create`**, not `TryCreate`.
- Name is unique among **one parent's direct children**. The same name under another parent is allowed.
- If no child with that name exists: atomically create/start it and return its chat ID.
- If a child with that name exists and **every creation parameter is identical**: return the existing chat ID, with **no side effect**. Do not prompt, resume, restart, retry, or modify the existing child, regardless of its state.
- If the name exists but **any creation parameter differs**: a clear conflict error, not reuse or replacement.
- Compare a canonical stored creation specification (including policy, initial task, connection, model, effort, and all other accepted creation parameters). Capture resolved defaults at creation so later default configuration changes cannot quietly redefine the existing child.
- Ensure unique `(ParentChatId, Name)` in persistent storage, and atomic compare-or-create under races/crashes.
- On success CLI stdout is the chat ID, even on idempotent reuse. Failure is an actionable error and nonzero exit.

This intentionally solves duplicate child spawning when a parent's latest response is executed again after Stop/Resume. It does **not** make shell commands idempotent.

## 4. Policy

Proposed shape:

```yaml
policies:
  manual:
    subAgents:
      maxDepth: 2
      onDone: RejectIfActive

  delegator:
    subAgents:
      maxDepth: 1
      onDone: Accept

  developer:
    subAgents:
      maxDepth: 0
      onDone: StopAll
```

- `maxDepth: 0`: no child creation. `1`: direct children only. `2`: grandchildren allowed, and so on.
- Depth budget must **never increase** by choosing a child policy with a higher limit. Enforce both ancestor remaining depth and child's own policy constraints.
- No `maxParallel` and no conceptual global count limit for simultaneously active child agents. Do not use a transient 'active' count for spawn admission. Runtime/executor/provider capacity protection remains an internal reliability matter, not an arbitrary policy quota.
- `onDone: RejectIfActive`: if any descendant is still queued, running, or waiting, reject `<DONE>` and send an actionable correction with the active descendant IDs to the agent. Do not terminally fail the parent for this policy correction.
- `onDone: StopAll`: recursively stop active descendants, then accept the parent's `<DONE>`.
- `onDone: Accept`: accept `<DONE>` and let descendants continue; parent remains completed and does not wake automatically on their eventual completion.
- **Stopped, failed, and completed** descendants do not block `RejectIfActive`. Stopping an unrecoverable child explicitly allows the parent to take over and finish.

Policies continue to own capability restrictions, e.g. allowed commands, shell access, and external-input trust boundaries; child policy assignment must not bypass server authorization.

## 5. Stop, Resume, and Prompt (shared UI/CLI semantics)

Use a single Agent API implementation for UI and CLI:

```csharp
Stop(chatId, tree: false);
Resume(chatId, tree: false);
Prompt(chatId, "Explain progress", connection: null, model: null);
```

- `tree:false` operates on one chat; `tree:true` recursively operates on **all** descendants too.
- Stop prevents further **automatic** model submissions, shell launches and agent-management calls for that chat.
- If a model submission is already in flight, let it finish and **store its response**; do not launch its commands. Respect the no-blind-replay rule for an ambiguous provider submission.
- If a shell command is currently running, **kill it** via the Executor's existing process ownership. Forget in-progress command position; do not checkpoint and continue the same process.
- The UI and parent agent can send an **explicit** prompt to a stopped chat. Its model response is recorded, but its commands do not run until Resume.
- Ensure Stop/Prompt ordering: a late old model response must not overwrite an explicitly requested newer response. Serialize at the correct per-chat boundary.
- Resume re-executes **commands in the latest assistant response from the beginning**, even commands that previously succeeded; do not replay commands from older assistant responses or restart a killed shell process from its previous position. Repeated side effects of ordinary commands are accepted. A successful `Create` is safe to repeat because of its unique-name contract.
- If Stop interrupts a pending wait, forget its elapsed-time state. On Resume, the latest response's wait is executed afresh with a full timeout.
- Human Stop All / Resume All acts on all selected descendants, including ones previously stopped individually. Do not introduce stop-reason tracking or ownership layers.
- `Prompt` must **not** automatically Stop or Resume. To intervene in a working child, the master explicitly does Stop → Prompt → Resume. This is deliberately interruptive, simpler than injecting text into the next model request.
- No Inject command in V1.
- The UI exposes Stop and Stop All, and Resume and Resume All, through matching controls (e.g. split action/dropdown), backed by these same endpoints.

Illustrative parent recovery flow:

```text
Create("developer", ...task X...) -> childId
WaitFor(childId, 600)             -> timeout
Inspect(childId)                  -> see latest progress/error
Stop(childId)                     -> kill unfinished work
Prompt(childId, "Explain status")  -> explicit response, commands suspended
# optional Resume(childId), or parent completes task itself
<DONE>                            -> allowed; stopped child does not block
```

## 6. Wait semantics: only explicit retrieval, no auto notifications

Conceptual commands:

```text
Wait(timeoutSeconds)
WaitFor(childChatId, timeoutSeconds)
WaitAny([childChatIds], timeoutSeconds)
WaitAll([childChatIds], timeoutSeconds)
```

- **Every wait has a required finite timeout.** No unbounded/infinite wait.
- `Wait` is a pure timer; no child requirement.
- `WaitFor` waits for the specified child's **latest execution** to reach `<DONE>`.
- `WaitAny` waits for the first successful `<DONE>` among a selected set, with failure/stop behavior below.
- `WaitAll` waits for all selected children's latest executions to reach `<DONE>`.
- Capture selected chat IDs and their **current latest execution IDs atomically** when registering a wait. Never treat `<DONE>` from an older execution as completion of a newer task.
- If the latest execution **already** returned `<DONE>`, return that result immediately for that child. If a new task has subsequently been submitted, the older DONE is irrelevant.
- At registration, any selected child that is **Stopped** or **Failed** (or has no execution) yields an actionable validation error instead of silently waiting. An already-completed latest execution is allowed as above.
- A newly stopped selected child interrupts a registered `WaitFor`, `WaitAny`, or `WaitAll` immediately with an error; don't burn the rest of the timeout.
- A child execution failing during a wait returns its error/status to the caller instead of pretending it completed successfully.
- A wait timeout reports which chat/execution(s) remained unfinished. Timeout is a result the master can handle, **not a terminal failure of the master**.
- Return the child chat ID, execution ID, status, and (for DONE) the **last assistant response**; include clear errors when failed/stopped. No need to copy every intermediate message into the parent.
- If a parent is not waiting, child DONE produces **no automatic parent activation or injected message**. `Inspect` or later wait can retrieve the result.
- In a simultaneous completion/stop/failure race, make result selection deterministic and covered by tests.
- The Agent worker must not hold a scarce model worker slot or block the whole runtime while a parent waits; integrate waits into durable runtime scheduling. Keep Console command execution and API wait lifecycle consistent with the current synchronous command framework.

## 7. Retry and model/integration selection

- Use **the same handler and path** for automatic error handling, UI Retry, and CLI `Retry(chatId)`. Never implement a separate 'CLI retry' algorithm.
- Error recovery belongs in a **virtual method on the integration base class**, overridable by providers for error-specific handling. The method receives the error/attempt/request context and returns a recovery decision; it may decline recovery.
- No YAML retry count/delay policy. A small safe default (previously discussed: at most one retry after approximately five seconds) may be selected by the base handler, overridden per error/provider, honoring Retry-After where present. Treat this as a proposed default to verify against the provider contract, not authorization to blindly replay ambiguous submissions.
- **Never automatically re-send a request whose acceptance is uncertain**. A timeout/disconnect does not prove the provider did not accept it.
- Root agent UI may expose Retry after unrecovered errors. No separate child Retry button; a parent can recover/retry a descendant through its allowed API/CLI actions.
- Model, connection/integration, and thinking effort can be selected at agent creation or prompt/execution as supported by existing runtime. Switching model/integration uses the **same execution-start/model-picker plumbing as the UI**, and never changes the chat's immutable policy.
- Verify the pending `agent-model-presets` work rather than cloning its selection behavior.

## 8. Provider pacing / account safety

- All requests using the **same authenticated provider account**, whether manual, master, child, automatic retry, or explicit Retry, must use shared account-level submission pacing and serialization.
- Proposed integration configuration: account grouping plus `pacing.minIntervalMs` (initial experimental value 3000) and `maxConcurrentSends: 1`.
- Account group, not chat ID or agent ID, determines the shared throttle. Distinct configured connections using the same account must not bypass it.
- Avoid holds on chat/worker locks while waiting for an account-wide send slot where possible. Confirm behavior with tests; 3 seconds is a starting point, not a guaranteed provider limit.

## 9. UI tree and status

- Replace/augment the flat Agent chat listing with a **collapsible persistent parent → descendant tree**, showing human-readable child names.
- Persist the read/unread state separately from the execution state. A child DONE message seen by its parent does **not** clear the human unread marker.
- Indicators:
  - **Green**: successful DONE, not yet opened by a human since completion.
  - **Red**: failed/error; remains visible until human inspection.
  - **Orange**: waiting for sub-agents, not ordinary provider/queue waiting.
  - **Alternating/animated green–orange**: actively processing.
  - **Gray**: stopped.
  - Completed and viewed: neutral.
- Opening a chat clears its human unread-completion notification.
- UI Stop/Stop All and Resume/Resume All call the same API operations used by CLI. Preserve the existing execution log/shell diagnostic display.
- Do not infer completion solely from process absence; persisted Agent execution state is authoritative.

## 10. Complete proposed CLI command inventory

Build a new `Mezhs.Agent.Cli` using `Mezhs.Console` and the typed `Mezhs.Agent.Api.Client`:

| Command | Signature (conceptual) | Behavior |
| --- | --- | --- |
| `Create` | `Create(name, policy, task, connection?, model?)` | Idempotent named direct child; print chat ID |
| `List` | `List()` | List accessible descendants with names, IDs and states |
| `Get` | `Get(chatId)` | Agent metadata, policy, parent and status |
| `Inspect` | `Inspect(chatId)` | Current execution, latest assistant response, command outcomes, errors |
| `Messages` | `Messages(chatId)` | Chat conversation history |
| `Log` | `Log(chatId)` | Execution/debug log |
| `Prompt` | `Prompt(chatId, text, connection?, model?)` | Explicit next task/instruction; no implicit resume |
| `Retry` | `Retry(chatId)` | Shared integration error handler |
| `Stop` | `Stop(chatId, tree=false)` | Stop one or whole subtree |
| `Resume` | `Resume(chatId, tree=false)` | Resume one or whole subtree |
| `Wait` | `Wait(timeoutSeconds)` | Timer only |
| `WaitFor` | `WaitFor(chatId, timeoutSeconds)` | DONE of one selected current execution |
| `WaitAny` | `WaitAny(chatIds[], timeoutSeconds)` | First DONE / actionable error |
| `WaitAll` | `WaitAll(chatIds[], timeoutSeconds)` | All selected DONE / actionable error |
| `Policies` | `Policies()` | Available policies |
| `Connections` | `Connections()` | Available integration connections |
| `Models` | `Models(connectionId)` | Available models/effort presets |

Add/help reuse `Help` and any existing framework validation/test commands as actually supported.

- Commands are agent-execution-scoped when invoked by an agent; apply trusted caller/descendant authorization.
- Favor structured machine-readable stdout for details/errors, stable names/IDs and nonzero exit on command errors. `Create` prints only the child chat ID on successful create/reuse.
- Use the existing ConsoleApplication parsing/attribute conventions; don't invent a second CLI parser.

## 11. Complete proposed Agent API endpoint inventory

Routes below are **proposed contracts**, not all verified pre-existing endpoints. Check current Agent API routing and reuse/extend existing endpoints instead of duplicating them.

| Method | Route | Purpose |
| --- | --- | --- |
| POST | `/v1/agent-chats/{parentId}/sub-agents` | Atomic named Create / identical reuse / mismatched conflict |
| GET | `/v1/agent-chats/{parentId}/sub-agents` | List children/descendants; optionally recursive |
| GET | `/v1/agent-chats/{chatId}` | Metadata, policy and persistent status |
| GET | `/v1/agent-chats/{chatId}/runtime` | Current execution/runtime projection |
| GET | `/v1/agent-chats/{chatId}/messages` | Conversation history |
| GET | `/v1/agent-chats/{chatId}/executions` | Execution history |
| GET | `/v1/agent-chats/{chatId}/debug-log` | Debug/command log |
| POST | `/v1/agent-chats/{chatId}/prompt` | Explicit prompt using selected connection/model |
| POST | `/v1/agent-chats/{chatId}/retry` | Shared integration error-recovery handler |
| POST | `/v1/agent-chats/{chatId}/stop` | Stop with body `{ "tree": false }` |
| POST | `/v1/agent-chats/{chatId}/resume` | Resume with body `{ "tree": false }` |
| POST | `/v1/agent-chats/{chatId}/waits` | Register Wait/WaitFor/WaitAny/WaitAll using chat IDs + timeout |
| GET | `/v1/waits/{waitId}` | Wait state and terminal result |
| GET | `/v1/policies` | Reuse policy discovery endpoint |
| GET | `/v1/connections` | Reuse connection discovery endpoint |
| GET | `/v1/connections/{id}/models` | Reuse model/effort discovery endpoint |
| POST | `/v1/executions` | Reuse/extend existing execution-start behavior |
| GET | `/v1/executions/{id}` | Reuse existing execution/status retrieval |

Internal responsibilities: persisted hierarchy and create-parameter comparison, inherited depth enforcement, caller/descendant authorization, shared Stop/Resume/Prompt/Retry state transitions, durable wait registration + wake-ups, latest-execution/result projection, per-account send pacing, and unread UI state.

## 12. Critical scenarios / verification checklist

1. **Atomic Create**: first create succeeds; same name+all same params returns same ID without any new work; same name+different task/model/policy/effort fails clearly; same name different parent succeeds; concurrent duplicate create makes one child only.
2. **Depth & permissions**: maxDepth 0/1/2, ancestry budget cannot be escalated by permissive child policy; parent can access nested descendants but not sibling/unrelated trees.
3. **Stop/Resume**: active shell is killed, in-flight provider response is stored, no further auto work runs; latest response commands re-execute from start on Resume; older responses do not replay. Check late provider replies vs a new explicit Prompt.
4. **Tree control**: Stop/Resume single vs recursive; Resume All includes previously stopped children; no stop-reason bookkeeping.
5. **DONE policy**: RejectIfActive blocks queued/running/waiting descendants and returns actionable IDs; stopped/failed/completed do not block; StopAll recursively stops; Accept does not wake parent on later child completion.
6. **Wait**: mandatory finite timeout; Wait timer only; child DONE before registration returns immediately for latest execution; stale DONE after a newer Prompt does not count; no execution/stopped/failed at registration errors; stopped/failed mid-wait wake with error; timeout is handled by parent; WaitAny/WaitAll behavior and simultaneous races are deterministic.
7. **Intervention**: master can Stop → Prompt → Resume, or Stop then take over and reach DONE; Prompt alone does not secretly kill/resume; no Inject behavior.
8. **Recovery**: UI and CLI Retry invoke the same integration virtual error handler; model/connection changes use existing execution machinery; ambiguous provider sends are never blindly duplicated.
9. **Pacing**: manual/master/child/retry traffic for same account group is serialized/throttled across connections; no chat-level bypass.
10. **UI**: persistent tree, names, exact indicator meanings, unread DONE persists across restart, opening chat clears it, Stop All/Resume All share API semantics.
11. **Restart**: queued agents/child relationship and waits survive correctly or fail explicitly according to a documented recovery rule; never silently repeat an uncertain provider send or unknowingly duplicate shell work. Inspect current durable Executor reconciliation first.
12. **Repository**: build/tests and reviewable diff; no unrelated changes, no merge until explicitly approved.

## 13. Suggested implementation sequence

1. Persist parent/name/creation spec, hierarchical authorization and depth policy; atomic Create.
2. Add typed Agent API client methods and new Console-based CLI (including inspect/history/discovery).
3. Unify Stop/Resume/Prompt and settle scheduling/lifecycle semantics with tests.
4. Implement explicit durable Wait/WaitFor/WaitAny/WaitAll and recovery/wake-up mechanics.
5. Add shared integration virtual error handling and account-group pacing, reusing model/integration selection.
6. Build collapsible Agent tree, indicators, persistent unread flags, controls and root Retry.
7. End-to-end and restart/race tests, diff review, one unmerged PR against main.

### Explicit non-goals for V1

- No Git branch/worktree management or workspace locks in orchestration; agents/policies own their workflows.
- No `maxParallel` policy quota, global agent count quota or queued overflow behavior for a quota.
- No injected parent messages in child results or automatic parent wake on child DONE.
- No cross-sibling agent-management API.
- No automatic Stop/Resume side effects hidden inside Prompt.
- No preserved shell command progress/checkpoint replay.
- No long-running shell-process resume.
- No automatic replay of uncertain model submissions.
- No configurable retry-number/delay YAML.
- No agent awareness that a Resume occurred, beyond ordinary re-execution of its latest response.

**Definition of done:** The feature can be driven through both Agent Web UI and the new CLI using one Agent API implementation, obeys the above lifecycle/wait/policy contracts, is tested, and is presented as an unmerged reviewable branch/PR.
