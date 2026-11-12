# Browser conversation parallelism, limits and Agent status indicators

**Status:** Plan proposed; independent High-risk plan review required before implementation.
**Task branch:** `browser-conversation-parallelism` (based on `main` at `2b17b887701d25822bfa1048650185153edbb164`).
**Classification:** **High-risk** — shared authentication, cross-chat concurrency, browser lifecycle, cancellation, and Agent Web runtime presentation.
**Scope:** Browser integration and Electron transport; small Agent-execution cleanup hook; Agent Web status dots.
**Related design:** `docs/plans/multi-agent-orchestration.md` on `plan/multi-agent-orchestration` is a **reference only**, not an implementation dependency. Do not merge/copy its Agent state machine, routes, CLI, or tree features into this task.

## 1. Problem and goals

MEŽS currently serializes unrelated authenticated browser conversations twice:
- `BrowserAccountSession.UseCoreAsync` takes one `SemaphoreSlim` across the whole provider action, including model response.
- `electron/main.js` owns one `BrowserWindow` and a global `operationQueue`.
- `MessageService` already has a **separate per-ChatId gate**, which is the correct place for same-chat ordering and must remain.

**Required behavior:** independent conversations on the same configured connection may send and receive model responses concurrently, like browser tabs; one chat remains sequential. Login/session state may be shared; conversation execution, progress, cancellation, and model selection must not leak across chats. A configurable connection-level `Limits` object is enforced by `BrowserIntegrationBase`. Keep a running Agent's window alive across shell commands/waits when a programmatic model follow-up is expected, and release it after execution ends. Add Agent Web status dot/icon indicators using existing runtime state.

Do **not** infer that every observed latency came from these locks; instrument and compare end-to-end timing.

## 2. Approved choices / invariants

1. **Process:** one Electron *main* process per configured browser connection, with separate `BrowserWindow`/renderer contexts keyed by **local ChatId**. These are independent renderer lifetimes, not separate Electron applications or guaranteed one unique OS PID per window.
2. **Authentication:** authenticated windows share one Electron `Session` within that process; do not launch independent browser applications against the same persistent Chromium profile. Serialize only short actual account/session mutations as needed, **never a whole model turn**.
3. **Limits placement:** `BrowserIntegrationBase` constructs, owns, validates, and applies its connection's `Limits`; provider implementations supply send behavior. Do not add a second concurrent-send limiter in `BrowserAccountSession`, Electron, Agent policy, or `MessageService`.
4. **Configuration:** nested `connections[].limits` object, initially `maxParallel` and `minIntervalBetweenStartsMs`. Both are non-negative integers. `0` means unlimited parallel turns / no configured spacing, respectively; omitted `maxParallel` defaults to **1 for backward-compatible admission** and omitted spacing to **0**. The defaults are separate from the explicitly configured value 0. Reject negative and nonsensical values at startup. The object can grow later for five-hour/weekly usage budgets, but **do not implement future quota fields, tracking, or a framework now**.
5. **Start spacing:** Option A — measure the instant the integration **admits** the model turn, not the eventual HTTP POST. Example with 3 slots, 5000ms spacing: admissions no closer than 5 seconds; completed turns can overlap. Use a monotonic clock to avoid time changes. Waiters can be cancelled; a request cancelled *before* admission consumes neither permit nor start timestamp.
6. **Permit lifetime:** from admitted send through completion/failure/cancellation/disposal of its active work. It counts **model turns**, not Agent jobs, idle browser windows, login views, or running shell commands. `0` concurrency bypasses the cap but not the spacing rule. No global lock spans provider I/O or response streaming.
7. **Model choice:** belongs to each individual request, including thinking effort. Existing ChatGPT payload already has `model` and `thinking_effort`. Investigate and, if verified safe, remove the per-send account-level model-preference `PATCH`; do not replace it with a global model-selection lock. Compare actual returned execution metadata for simultaneously selected different models.
8. **Lifecycle:** create each conversation window on first need; hold it while its Agent execution is active, including shell commands or programmatic waits. Once execution ends, request release; actual destruction waits for active browser operations to settle. Ordinary non-Agent chats use idle cleanup. When no windows/operations remain, keep the shared Electron main process warm until `transport.idleMinutes` expires; 0 disables automatic idle shutdown.
9. **No orchestration implementation:** reuse today's AgentWorker execution lifetime to release the window. Do not add persisted `Waiting`/`Stopped` statuses, new Stop/Resume semantics, Agent CLI/API endpoints, status event bus, polling daemon, or second state store.
10. **Workflow:** plan and changes only on this task branch, independent plan review before code, independent code review before independent testing, owner approval required for merge; main is release-only. No version/changelog edits on the task branch.

## 3. Existing contracts and change points

| Owner / file | Current state | Intended change |
| --- | --- | --- |
| `src/Mezhs.Api/Configuration/MezhsOptions.cs` and `MezhsConfigLoader.cs` | `ConnectionOptions` lacks limits | Parse/validate a nested `Limits` object, preserve old YAML |
| `src/Mezhs.Api/Services/IntegrationRegistry.cs` and `IntegrationConnection` | Only workspace/defaultModel are passed to integrations | Carry typed limits to browser integration without ad hoc string parsing; keep current constructors/plugin discovery compatible |
| `src/Mezhs.Integration.Browser/BrowserIntegrationBase.cs` | Direct provider overrides; one account session | Centrally enforce admission/start spacing; own connection browser lifetime and conversation release |
| `src/Mezhs.Integration.Browser/BrowserAccountSession.cs` | One transport and one gate across full send | Share connection login/session state; remove full-turn serialization and per-turn transport ownership from the account lock |
| `transports/Mezhs.Browser.Electron/ElectronBrowserTransport.cs` | One Electron transport/process; one invoke target | Reuse one main process per connection and route requests by conversation context; maintain typed transport protocol |
| `electron/main.js` | One global `window` and `operationQueue` | Connection-owned session/host, ChatId-keyed window contexts, per-context operation state/queue only |
| ChatGPT/Grok/Gemini browser modules | Single-window assumptions and distinct login/guest behavior | Accept targeted per-context execution; preserve account and guest semantics |
| `src/Mezhs.Agent.Api/Services/AgentWorker.cs` | Owns execution lifetime | On execution cleanup, request release of its ChatId/connection's browser context, without new status model |
| `src/Mezhs.Agent.Web/src/App.tsx` plus existing styles | Execution status text and runtime polling, no consistent sidebar status dot | Derive accessible sidebar/header status indicators from existing Agent execution/runtime data |

`docs/architecture.md` and affected current specifications must reflect implemented behavior before review, **not** merely because the plan exists. Inspect all callers of login, models, diagnostics, transport, send and disposal before editing.

## 4. Proposed design

### 4.1 Central admission and configuration

- Provide a small typed `Limits` value/object on each configured connection. Do not put arbitrary future provider quota types into today's model.
- Implement one canonical `BrowserIntegrationBase` send wrapper (template method or similarly enforced single path) so every browser provider uses the same per-connection admission rules. Explicitly preserve anonymous guest sends.
- Gate concurrent **turns**, then reserve an admissible monotonic start time, then dispatch. Admission scheduling must be atomic across competing requests; no race may permit two starts inside the configured spacing. Avoid a periodic token bucket if it allows bursts that violate minimum separation.
- A cancelled waiter leaves no leaked concurrency slot, phantom start time or hanging queue entry. Admission fairness should be sensible (FIFO where practical) without introducing a durable scheduling system.
- Keep admission wait and browser startup distinct in diagnostics: queued for permit, waiting for pacing, starting/authorizing browser, submitting prompt, streaming model response, finalizing.
- Default 1 preserves legacy send-at-a-time behavior **without** retaining global browser-window serialization: explicit `maxParallel: 0` and values greater than 1 enable parallelism. Include a config example with both settings explicitly 0.

### 4.2 Electron connection host and window manager

- One authenticated session/profile owner per configured connection; one Electron main process that may host many ChatId-keyed `BrowserWindow` contexts. Keep a single login/authorization flow, separate from turn locks.
- Replace module invocation's implicit global `window` with a **context-bound** window, page invocation and progress channel. Route `invoke` and polling to their corresponding context and operation IDs; avoid cross-window responses and callback mix-ups.
- No global provider-operation Promise queue. Per-conversation serial execution can remain where needed as defense-in-depth; the existing core `MessageService` ChatId gate remains authoritative for ordering.
- Enforce deterministic create/get/release behavior for the same ChatId, shutdown when no contexts remain, and reliable disposal on host death. Window crashes must fail only affected operations when possible; main-process failure can affect all contexts and must be reported truthfully.
- Ensure browser diagnostics and `OpenBrowser` target a well-defined window without silently stealing another conversation's context. Where a context is required, use explicit selection or a documented management context; do not create an unbounded extra window per diagnostic call.
- Guest providers must preserve their anonymous isolation: distinct guest storage contexts/ephemeral state as appropriate, not accidental cookie or conversation sharing merely because the connection host is shared. Validate real provider module behavior.
- Connection-level session must not be shared through concurrent external Electron *main processes* using the same profile path.

### 4.3 Browser-context ownership and release

- Maintain one active context per (`connectionId`, `ChatId`) in the browser owner, with an active-operation count and a single teardown path. Do not create a second Agent-specific browser registry/status table.
- The Agent runtime holds a context for the **duration of its execution**, not merely for the streaming response. A long `SH` or Wait does not consume a `Limits` turn permit but retains its context for the expected next prompt.
- Provide a minimal generic, idempotent **release-conversation** capability available at the existing integration/registry boundary. Call it from `AgentWorker`'s execution-lifetime cleanup when a runnable execution actually ends. Do not add Agent enums to `BrowserIntegrationBase`.
- Release must not close a context still serving an active turn, and must not race a newly started execution/reuse of the same chat. Investigate leases/generation ownership using existing per-chat execution ordering before adding locks. No polling on persisted statuses.
- Ordinary conversations retain idle-window timeout behavior. The host shuts down only when no live window or pending browser operation remains, after `transport.idleMinutes`; `idleMinutes: 0` means no automatic idle shutdown.
- On cancellation, never blindly replay an uncertain provider send. Preserve existing message/recovery contracts; if a Stop/Cancel request races an already-submitted turn, defer browser teardown until the operation resolves or fails with an explicit uncertainty outcome.
- When host exits/crashes, release resources and permits, preserve persisted chat IDs/history, and lazily recreate the window for a later turn. No duplicate submission as a side effect of recreation.

### 4.4 Model selection and authorization correctness

- Inspect whether the ChatGPT account-level `setModelPreference` mutation is needed for a per-turn request. Keep the explicit per-turn model/thinking-effort payload. Do not assume omission of the account PATCH is correct without live verification.
- Concurrent distinct-model tests must verify both the submitted payload and resulting model/effort metadata. If provider behavior contradicts this design, **escalate for an owner decision** rather than silently serializing every send or claiming correctness.
- Separate login and models/diagnostics calls from model turn concurrency: synchronize only shared account changes that require it; ensure interactive login does not invalidate ongoing independent windows.
- Preserve security properties and existing Electron IPC validation. Document the current renderer sandbox settings and assess them in security review; don't casually expand renderer authority. Any required security hardening outside this task's causal scope must be escalated, not hidden as an unreviewed rewrite.

### 4.5 Agent Web status dots / indicators (requested excerpt from orchestration plan)

Adapt the **visual status indicators only** from `docs/plans/multi-agent-orchestration.md` `9`: Completed green, Failed red, Waiting orange, Running animated/active, Stopped gray, Queued neutral. Show a small dot or accessible icon in the Agent sidebar and selected Agent header; keep the existing status text.

**Important current-state mapping, not a new status machine:**

| Actual existing data | Display |
| --- | --- |
| Latest active Agent execution `Queued` | Neutral dot, 'Queued' |
| Active Agent execution `Running` | Animated active dot, 'Running' |
| Explicit observable waiting activity within `Running`, if reliably available | Orange dot, 'Waiting' as an **activity** label; else show Running, don't invent Waiting |
| Agent chat `Paused` (present API, not future orchestration `Stopped`) | Gray dot, 'Paused' — don't mislabel as 'Stopped' |
| `CancelRequested` | Distinct 'Stopping' label/active indication; don't mark terminal |
| Terminal `Completed` | Green dot, 'Completed' |
| Terminal `Failed` | Red dot, 'Failed' |
| Terminal `Cancelled` | Gray dot, 'Cancelled' |
| No executions / unavailable data | Neutral/unknown indicator with honest label; never infer DONE |

- Once the future orchestration branch introduces real `Waiting`/`Stopped`, its UI can reuse the component and palette. **Do not add those statuses to the database or Agent API now.**
- Choose the most recent **Agent** execution, not a Shell execution or arbitrary historical success, and handle current execution/paused precedence explicitly.
- Use existing Agent runtime/store reads. For many sidebar rows avoid N+1 runtime polling; if needed add a **read-only derived summary** to an existing list response, not a second persisted owner or new status manager.
- Accessible text/tooltip/`aria-label`; color must not be the sole signal. Honor reduced-motion settings; don't animate terminal indicators. Do not implement orchestration tree, unified `/v1/agents` endpoints, Prompt-anytime behavior, Stop/Resume, CLI, or other UI controls as part of these dots.
- Status indicators observe Agent lifecycle only; their implementation must not become the trigger for browser cleanup.

## 5. Risk register / mitigations

| Risk | Severity | Plan safeguard |
| --- | --- | --- |
| Concurrent profile ownership / auth corruption | Critical | One Electron main and `Session` owner per configured connection; integration tests for login, refresh and multiple windows |
| Wrong model / effort due to account-level preference | High | Per-turn payload + two-model concurrent verification against returned metadata; escalation on discrepancy |
| Cross-window IPC/progress/response confusion | High | Context/operation IDs, deterministic routing and concurrent interleaving tests |
| Cancellation, active-send teardown, uncertain resubmission | High | Active-operation tracking, graceful drain, no blind retry, injected crash/cancel tests |
| Unbounded `maxParallel: 0` memory/CPU | High | Explicit opt-in, honest diagnostics and stress measurement; don't silently impose a hidden cap |
| Shared-base provider regressions (Grok/Gemini/guest) | High | Provider-specific regression and anonymous isolation tests |
| Status projection staleness / wrong sidebar dot | Medium | Canonical AgentStore/runtime projection, transition/paused tests; no N+1 fan-out |
| Startup and memory overhead | Medium | Measure cold host, warm window, 1/5/10 windows; don't assume fixed startup timing |
| Compatibility with future orchestration plan | Medium | Generic release boundary only; no status API refactor now; future plan must reconcile implementation |
| Security boundary for remote renderer | High | Review current IPC/preload/sandbox settings, no broadened privilege or undocumented bypass |

## 6. Implementation increments (one branch, reviewable commits)

1. **Baseline and fixtures:** map integration/transport callers and affected specifications; add timing/concurrency regression fixtures reproducing serialization. Establish current login/diagnostics/model/cancel behavior and a safe manual parallel test fixture.
2. **Limits:** typed nested config and validation; central `BrowserIntegrationBase` admission wrapper, monotonic spacing and cancellation; unit tests covering 0 semantics, missing defaults, fairness and error release.
3. **Connection-owned Electron host:** migrate one-window/global-queue contract to ChatId-targeted windows/operations with shared session, independent progress, login and diagnostics; preserve guest isolation and provider compatibility.
4. **Provider correctness:** update browser module context plumbing; verify per-turn ChatGPT model selection, distinct models/efforts, auth renewal, uploads and streamed replies; remove redundant account preference mutation only with evidence.
5. **Lifecycle:** minimal per-conversation context release, AgentWorker execution-finalization hook, non-Agent idle expiry, warm main-process idle shutdown and crash/cancel safety.
6. **Agent Web dots:** use actual existing runtime and paused state; add small accessible status indicators to sidebar and header, derive optional Waiting activity, test correctness without new Agent statuses.
7. **Cleanup:** remove obsolete global send lock/queue, stale code paths and misleading 'shared slot' message; update implemented/current specifications, tests and generated Codegraph before review.

**Mandatory complexity check:** If safe session sharing, lifecycle draining, or model selection requires a second scheduler, duplicated persistence, global model lock, or unrelated orchestration refactor, stop and escalate the requirement and simpler alternatives to the owner. No piecemeal fallback architecture.

## 7. Verification and acceptance

**Automated and targeted integration tests**
- Same ChatId remains ordered under simultaneous sends; distinct ChatIds on the same connection genuinely overlap when `maxParallel >= 2` and spacing is 0. No global operation queue remains.
- At most configured active model turns; `maxParallel: 0` removes concurrency cap; `minIntervalBetweenStartsMs: 0` disables spacing. Missing config behaves as legacy one-at-a-time. Invalid negatives rejected at startup.
- Admission times satisfy the configured monotonic minimum interval under contention; cancellation of a queued/pacing request does not consume a slot or reserve a start; failures release resources.
- Concurrent different-model/different-effort ChatGPT requests produce the intended per-turn outcomes; run side-by-side live verification where permitted. Unit mocks alone are insufficient proof of provider behavior.
- Auth login/model discovery/diagnostics continue working while multiple windows exist; guest sessions remain isolated; popup and file upload/download tests remain valid.
- One renderer crash/cancellation/release does not close another active chat. A host crash surfaces failures without re-sending accepted or uncertain turns; subsequent new turn can reinitialize.
- Agent execution performs model -> long-running shell command -> model follow-up on same retained context; when terminal, the window closes only after all browser work is settled. Agent statuses and policy behavior remain unchanged.
- Normal chat idle cleanup and connection-host idle shutdown work, including idleMinutes=0, without orphan windows/processes. Measure cold/warm startup and 1/5/10-window resource use.
- Agent Web dots exercise Queued/Running/active waiting/Paused/CancelRequested/Completed/Failed/Cancelled/unknown. Verify sidebar and header, screen reader labels, reduced motion, and most-recent-Agent ordering without N+1 polling.

**Required regression suites:** build/architecture, config, account login, Grok account login, guest providers, cancellation contracts, Electron transport/IPC, Agent API/runtime, Agent Web UI and targeted manual parallelism. Use the repository's existing test entry points and add missing fixtures; name actual commands/results in the implementation report. No fabricated CI or live-provider results.

**Acceptance:** all applicable independent reviews/tests PASS, current specifications match behavior, instrumentation demonstrates real overlap, risk/limitations documented, exact commit/PR supplied for owner approval. No merge until explicit owner authorization.

## 8. Handoffs and release

- **Now:** independent **Plan Reviewer** checks this document, the actual source, existing contracts, and `.agents/workflow.md`; verdict PASS/FAIL/BLOCKED with actionable findings. No implementation until PASS and owner decision to proceed.
- **Then:** Developer implements on **this same branch**, produces developer checks, refreshes Codegraph, moves final plan from `planned/` to `implemented/` before code review.
- **Then:** independent Code Reviewer and independent Tester, in that order; fixes repeat affected stages. Pause for owner approval.
- **Release only:** approved merge into `main`, single version/changelog finalization, release tag and branch cleanup per `.agents/release.md`. Do not edit release metadata on the task branch.
