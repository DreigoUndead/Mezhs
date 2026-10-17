# MEŽS multi-agent orchestration: implementation handoff

**Status:** Architecture agreed in design discussion on 2026-10-17; **not implemented**.  
**Repository:** `DreigoUndead/Mezhs`  
**Planning / implementation branch:** `plan/multi-agent-orchestration` (created from `main`).  
**Scope:** Agent API, Agent API client, a new Console-based CLI, persistence/runtime, integration error handling and pacing, and Agent Web UI.

## 0. Start here in the next chat

Implement on **this existing branch** in reviewable increments. The plan is a behavior contract, not an implementation. Inspect actual code and read .agents/skills/repository-change-workflow/SKILL.md and .agents/skills/software-change/SKILL.md. Do not merge without explicit approval.

**Dependency:** PR #21 (agent-model-presets) was still open/unmerged on 2026-10-17. Reconcile its approved model/connection/effort work before relying on it.

**Design rule:** UI and CLI use one Agent API and lifecycle. Agents are trusted cooperative local-machine tools, not security sandboxes. Do not add global agent-count/worker-slot/queue limits; only integrations can throttle provider submissions. No auto-injected child notifications, command-position restoration, duplicate lifecycle engine, or invented orchestration infrastructure.

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

- Root agents originate from UI/external entry points. Agent **chats** form persistent named parent/child trees; **executions** represent individual requests/tasks in a chat.
- Each chat has a fixed policy ID. **Current agent.yaml is the only source of policy values**, including depth/capability rules; remove cached authoritative policy objects/snapshots.
- Parent tools operate on descendants, not unrelated agents. This is a cooperative tooling rule, **not a security guarantee**: unrestricted SH can call APIs or change/redeploy the host.
- Children never automatically send messages or activate their parent; parents inspect or wait explicitly.
- No global or per-policy count, active-agent, queue or concurrent-Agent-execution slot limit. Per-chat ordering and provider-account throttling remain valid.
- Persist chat/parent/name/execution state independently of OS process presence. A stopped/failed child remains visible.

## 3. Named Create: duplicate is always an error

Conceptual CLI: `Create(name: "worker-x", policy: "developer", task: "Implement X", connection: null, model: null)`.

- A parent name is unique among its **direct children**. Same name under a different parent is fine.
- A second Create with an existing name **always fails**, even if every parameter matches. Do not implement idempotent reuse, canonical creation-spec fingerprints or silent restart.
- The conflict error includes the existing child's chat ID, allowing a resumed parent to inspect the earlier child.
- Atomically reserve (ParentChatId, Name) with a SQLite uniqueness constraint. Current ChatStore files and AgentStore SQLite are separate: reserve a predetermined chat ID and make subsequent chat materialization/initial execution startup recoverable after a crash. No phantom children after rejected validation.
- Successful CLI Create prints **only** the new child chat ID; errors go to stderr with nonzero exit.

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

## 9. Agent Web UI: actual statuses, prompt always available

- Persistent collapsible parent/descendant tree with human-readable child names and IDs.
- Indicators show **actual state**, with no separate human unread/read-completion flags: Completed green; Failed red; Waiting orange; Running animated/active; Stopped gray; Queued neutral. Define projection precedence consistently.
- **The selected-chat input/composer must remain enabled when Running, Waiting, Stopped, Completed or Failed.** Specifically remove current `activeExecution` and `selectedChat.paused` guards on send/composer in `src/Mezhs.Agent.Web/src/App.tsx`. Existing-chat Send calls the same `Prompt` API used by agents, not a UI-only path.
- Show queued vs submitted prompts honestly; entering a prompt does not imply current SH interruption.
- UI Stop/Stop All and Resume/Resume All use the same API operations as CLI. Preserve shell log and execution diagnostics; Root UI may show provider Retry. Opening a chat must not change its status.

## 10. CLI command inventory

Implement `Mezhs.Agent.Cli` using `Mezhs.Console` [Command] discovery/binding and the typed `Mezhs.Agent.Api.Client`; no parallel parser. Bounded synchronous CLI waits are permissible without global agent-worker slot limits.

| Command | Signature (conceptual) | Behavior |
| --- | --- | --- |
| `Create` | `Create(name, policy, task, connection?, model?)` | New named child; any duplicate => conflict including existing ID |
| `List` | `List()` | Accessible descendants, names, IDs, states |
| `Get` | `Get(chatId)` | Chat metadata, live policy ID, tree |
| `Inspect` | `Inspect(chatId)` | Latest execution, assistant response, outcomes/errors |
| `Messages` | `Messages(chatId)` | Chat history |
| `Log` | `Log(chatId)` | Execution/debug log |
| `Prompt` | `Prompt(chatId, text, connection?, model?)` | Inject at next pre-command boundary; works while stopped |
| `Retry` | `Retry(chatId)` | Integration HandleError |
| `Stop` | `Stop(chatId, tree=false)` | Stop chat/subtree; kill associated shell process trees |
| `Resume` | `Resume(chatId, tree=false)` | Replay latest response's commands from start |
| `Wait` | `Wait(timeoutSeconds)` | Bounded timer |
| `WaitFor` | `WaitFor(chatId, timeoutSeconds)` | Any terminal/stopped-working state |
| `WaitAny` | `WaitAny(chatIds[], timeoutSeconds)` | First selected child that stops working |
| `WaitAll` | `WaitAll(chatIds[], timeoutSeconds)` | All selected children stop working |
| `Policies` | `Policies()` | Live YAML policy discovery |
| `Connections` | `Connections()` | Available integrations |
| `Models` | `Models(connectionId)` | Available models/effort |

Retain Help/Validate. Parent/descendant checks are cooperative tool guidance, not a security boundary. Stable machine-readable records; errors stderr/nonzero; Create stdout just new ID.

**Per-command timeout override:** extend agent command protocol/parser/Interpreter/Executor so each command can specify its own finite timeout (default from current policy). An SH wrapping a long Wait must carry an outer timeout sufficient for wait + overhead; tests must cover this. Determine exact optional syntax compatible with current parser during implementation.

## 11. Agent API endpoint inventory (proposed, reuse existing routes)

| Method | Route | Purpose |
| --- | --- | --- |
| POST | `/v1/agent-chats/{parentId}/sub-agents` | Atomic named Create; duplicate conflict |
| GET | `/v1/agent-chats/{parentId}/sub-agents` | Children/descendants |
| GET | `/v1/agent-chats/{chatId}` | Metadata, policy, persisted status |
| GET | `/v1/agent-chats/{chatId}/runtime` | Execution/runtime state |
| GET | `/v1/agent-chats/{chatId}/messages` | Conversation history |
| GET | `/v1/agent-chats/{chatId}/executions` | Execution history |
| GET | `/v1/agent-chats/{chatId}/debug-log` | Debug/command log |
| POST | `/v1/agent-chats/{chatId}/prompt` | Always allow enqueue/submit prompt |
| POST | `/v1/agent-chats/{chatId}/retry` | Shared integration HandleError |
| POST | `/v1/agent-chats/{chatId}/stop` | Stop with body `{ "tree": false }` |
| POST | `/v1/agent-chats/{chatId}/resume` | Resume with body `{ "tree": false }` |
| GET | `/v1/policies` | Live policy discovery |
| GET | `/v1/connections` | Connection discovery |
| GET | `/v1/connections/{id}/models` | Model/effort discovery |
| POST | `/v1/executions` | Reuse/extend execution start |
| GET | `/v1/executions/{id}` | Execution/status |

No separate persistent wait registration/endpoints (`/v1/waits/...`) are required: typed client bounded waits can observe persisted runtime/execution status. Add efficient batch status lookup only if demonstrated necessary. Keep authoritative chat lifecycle and prompt queue state server-side, separate from Executor OS-process ownership.

## 12. Critical scenarios / verification checklist

1. **Create:** duplicate name always conflict (even same specs) with existing ID; same name under different parent succeeds; concurrent Create reserves only one child; interrupted SQLite/chat-file materialization recovers.
2. **No slots:** many parents/children waiting and running; no global agent-count/worker/queue admission cap. Integration-specific pacing still works.
3. **Live policy:** editing valid agent.yaml changes existing agents' next policy check; no snapshots; enforce current depth, command, connection/model constraints.
4. **Stop/Resume:** Stop kills active shell execution process tree without indefinite wait; in-flight provider response saved, not blindly replayed; Resume executes commands from **latest assistant response only**, from start, regardless of earlier success.
5. **Prompt:** during SH, prompt waits until SH completes, retains result, supersedes unexecuted commands; while provider request in flight, applies after response and before commands; before DONE is handled; multiple simultaneous prompts ordered/durable.
6. **Stopped prompting:** prompts still submit model turns and save responses without executing commands. Multiple responses: Resume runs only latest. Late old replies never execute superseded commands.
7. **Tree:** Stop/Resume single vs recursive; Resume All includes individually stopped children; shell process-tree kill is distinct from child-agent tree stop.
8. **DONE policy:** RejectIfActive corrects with descendant IDs; StopAll requests recursive Stop; Accept leaves descendants active; create-vs-DONE race cannot admit late active child into a terminal subtree.
9. **Wait:** timer; For/Any/All observe **Stopped, DONE, Failed, Cancelled**, including initially terminal, mid-wait stop/error, mixed WaitAll; timeout normal result, not parent failure; latest-execution stale DONE ignored; deterministic races; no unsolicited parent messages.
10. **Timeout:** per-command overrides propagate through SH and Executor; e.g. WaitFor 600s is not killed at default 120s.
11. **Recovery:** UI/CLI Retry use one virtual HandleError; no blind uncertain-send replay; model/connection/effort selection shared with UI/PR #21.
12. **Provider pacing:** only configured integration/account settings throttle submissions, across manual/master/child/retry; no global fixed three seconds or max-one default.
13. **UI:** persistent collapsible tree; actual state only; composer usable while busy/stopped/done/failed; prompt pending state truthful; controls share API.
14. **Restart:** pending prompts and children survive; no duplicate uncertain provider request; test Stop/Prompt/Resume and reply-order races.
15. **Repository:** build, smoke tests, diff review and an unmerged PR against main; no unrelated changes.

## 13. Suggested implementation sequence

1. Reconcile approved model-presets dependency, then redesign Stop/Resume/Prompt and the pre-command injection boundary as one lifecycle mechanism, with tests.
2. Remove global agent worker-slot and queue-admission limits; preserve per-chat ordering and optional integration-account pacing.
3. Persist parent/name, live policy checks, non-idempotent atomic Create and restart materialization.
4. Extend typed API client and Console CLI; support per-command timeout override.
5. Implement bounded waits over persisted state without a durable wait-job subsystem.
6. Integrate shared virtual integration HandleError and model/connection selection.
7. Update UI tree, real statuses, and **always-enabled existing-chat prompt composer**; wire all controls to shared API.
8. Verify restart/race/e2e tests, review diff, and keep PR unmerged until explicitly approved.

### Explicit V1 non-goals

- No workspace/worktree manager or workspace locks in orchestration.
- No global Agent count, worker-slot, queue-capacity or policy maxParallel limit.
- No automatically injected child messages or unsolicited parent wake.
- No sibling-to-sibling management tool, and no artificial shell sandbox security.
- No duplicate Create idempotent reuse or stored creation fingerprint.
- No Prompt-triggered implicit Stop/Resume; no Inject command.
- No command-position checkpoints or killed-shell process resume.
- No uncertain provider submission replay.
- No persisted policy snapshots, unread tracking, or persistent wait-job scheduler.
- No orchestration-level retry count/delay policy.

**Definition of done:** same Agent API powers UI and CLI; unlimited-agent concurrency, live YAML policies, named children, anytime prompt injection, latest-response Stop/Resume and bounded terminal-state waits are implemented/tested in one reviewable **unmerged** PR.

