# Browser conversation parallelism, limits and Agent status indicators

**Status:** Plan proposed; independent High-risk plan review required before implementation.
**Task branch:** `browser-conversation-parallelism` (based on `main` at `2b17b887701d25822bfa1048650185153edbb164`).
**Classification:** **High-risk** — shared authentication, cross-chat concurrency, browser lifecycle, cancellation, and Agent Web runtime presentation.
**Scope:** Browser integration and Electron transport; small Agent-execution cleanup hook; Agent Web status dots.
**Related design:** `docs/plans/multi-agent-orchestration.md` on `plan/multi-agent-orchestration` is a **reference only**, not an implementation dependency.
**Plan-review revision:** Clarifies operation-scoped cancellation, safe authorization recovery, guest isolation, legacy test migration, and Agent status precedence. No implementation is included; an independent re-review is required. Do not merge/copy its Agent state machine, routes, CLI, or tree features into this task.

## 1. Problem and goals

MEŽS currently serializes unrelated authenticated browser conversations twice:
- `BrowserAccountSession.UseCoreAsync` takes one `SemaphoreSlim` across the whole provider action, including model response.
- `electron/main.js` owns one `BrowserWindow` and a global `operationQueue`.
- `MessageService` already has a **separate per-ChatId gate**, which is the correct place for same-chat ordering and must remain.

**Required behavior:** independent conversations on the same configured connection may send and receive model responses concurrently, like browser tabs; one chat remains sequential. Login/session state may be shared; conversation execution, progress, cancellation, and model selection must not leak across chats. A configurable connection-level `Limits` object is enforced by `BrowserIntegrationBase`. Keep a running Agent's window alive across shell commands/waits when a programmatic model follow-up is expected, and release it after execution ends. Add Agent Web status dot/icon indicators using existing runtime state.

Do **not** infer that every observed latency came from these locks; instrument and compare end-to-end timing.

## 2. Approved choices / invariants

1. **Process:** one Electron *main* process per configured browser connection, with separate `BrowserWindow`/renderer contexts keyed by **local ChatId**. These are independent renderer lifetimes, not separate Electron applications or guaranteed one unique OS PID per window.
2. **Authentication and guests:** authenticated windows share one Electron `Session` within that process; do not launch independent browser applications against the same persistent Chromium profile. Serialize only short actual account/session mutations as needed, **never a whole model turn**. Anonymous guest sends retain **fresh per-turn ephemeral session/storage isolation** even when the Electron main process is reused: no guest cookies, local storage, or page context may survive to another send, including a second turn of the same local ChatId.
3. **Limits placement:** `BrowserIntegrationBase` constructs, owns, validates, and applies its connection's `Limits`; provider implementations supply send behavior. Do not add a second concurrent-send limiter in `BrowserAccountSession`, Electron, Agent policy, or `MessageService`.
4. **Configuration:** nested `connections[].limits` object, initially `maxParallel` and `minIntervalBetweenStartsMs`. Both are non-negative integers. `0` means unlimited parallel turns / no configured spacing, respectively; omitted `maxParallel` defaults to **1 for backward-compatible admission** and omitted spacing to **0**. The defaults are separate from the explicitly configured value 0. Reject negative and nonsensical values at startup. The object can grow later for five-hour/weekly usage budgets, but **do not implement future quota fields, tracking, or a framework now**.
5. **Start spacing:** Option A — measure the instant the integration **admits** the model turn, not the eventual HTTP POST. Example with 3 slots, 5000ms spacing: admissions no closer than 5 seconds; completed turns can overlap. Use a monotonic clock to avoid time changes. Waiters can be cancelled; a request cancelled *before* admission consumes neither permit nor start timestamp.
6. **Permit lifetime:** from admitted send through the **actual local provider operation settling or being explicitly retired** (including cancellation and uncertain-outcome cleanup), not merely until a caller stops HTTP polling. It counts **model turns**, not Agent jobs, idle browser windows, login views, or running shell commands. `0` concurrency bypasses the cap but not the spacing rule. An abandoned operation cannot permanently leak a permit. No global lock spans provider I/O or response streaming.
7. **Model choice and safe retry:** model/effort belongs to each individual request. Existing ChatGPT payload already has `model` and `thinking_effort`. Investigate and, if verified safe, remove the per-send account-level model-preference `PATCH`; do not replace it with a global model-selection lock. Compare actual returned execution metadata for simultaneously selected different models. **Never re-invoke an opaque send delegate after an authorization error unless provider evidence proves its submit phase was never reached.**
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
| `transports/Mezhs.Browser.Electron/ElectronBrowserTransport.cs` and `transports/Mezhs.Browser.Abstractions/IChatBrowserTransport.cs` | One Electron transport/process; transport cancellation stops caller polling but does not address accepted Electron work | Reuse one main process per connection, route by conversation/operation ID, provide operation-scoped cancellation/terminal acknowledgement without disposing unrelated windows |
| `tests/integration-cancellation-contract.ps1` and `tests/agent-provider-lifetime.ps1` | Require whole-transport disposal/recreation when a browser send is cancelled | Replace those obsolete assumptions with scoped-cancellation, cleanup and unaffected-chat invariants; keep the cancellation contract for callers |
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
- An admitted operation owns its permit **independently of the caller's polling task**. On in-flight cancellation, it stays counted until the Electron operation settles or its context is explicitly retired after a bounded abort/drain path; retain no cross-chat mutex and do not count an abandoned operation forever. A cancelled-before-admission request consumes neither permit nor pacing timestamp.
- Keep admission wait and browser startup distinct in diagnostics: queued for permit, waiting for pacing, starting/authorizing browser, submitting prompt, streaming model response, finalizing.
- Default 1 preserves legacy send-at-a-time behavior **without** retaining global browser-window serialization: explicit `maxParallel: 0` and values greater than 1 enable parallelism. Include a config example with both settings explicitly 0.

### 4.2 Electron connection host and window manager

- One authenticated session/profile owner per configured connection; one Electron main process that may host many ChatId-keyed `BrowserWindow` contexts. Keep a single login/authorization flow, separate from turn locks.
- Replace module invocation's implicit global `window` with a **context-bound** window, page invocation and progress channel. Route `invoke`, polling, cancellation and terminal acknowledgements to their corresponding context and operation IDs; avoid cross-window responses and callback mix-ups.
- The transport must generate a **stable operation ID before posting** to Electron, so a caller can address work whose HTTP start acknowledgement is lost or cancelled. The host records the operation once; an ambiguous start must never be blindly re-posted. Provide a narrow cancel/abandon operation action in the **existing Electron transport protocol**, not an Agent route or independent status manager.
- No global provider-operation Promise queue. Per-context serial dispatch/drain is required as a lifecycle safety barrier, not an additional connection concurrency limiter. `MessageService` remains authoritative for ordinary same-chat ordering; after its cancelled caller releases that gate, the context barrier must prevent an old, still-running provider action overlapping a new send to that same ChatId.
- A cancelled/abandoned operation must be marked and its provider work given an abort signal where the module permits it, including relevant `session.fetch` work; closing a window alone does not prove session-level network work stopped. Terminal or retired operations are cleaned **without relying on the caller polling again**; retain undelivered normal terminal results only for a bounded interval. Do not let stale callbacks, progress or results update a newer operation.
- Enforce deterministic create/get/release behavior for the same ChatId, shutdown when no contexts remain, and reliable disposal on host death. Window crashes must fail only affected operations when possible; main-process failure can affect all contexts and must be reported truthfully.
- Ensure browser diagnostics and `OpenBrowser` target a well-defined window without silently stealing another conversation's context. Where a context is required, use explicit selection or a documented management context; do not create an unbounded extra window per diagnostic call.
- **Guest isolation decision:** reuse the connection's Electron **main process**, but allocate a new non-persistent Electron `Session` partition and fresh renderer for **every anonymous model turn**, not a persistent ChatId window. Destroy/release that ephemeral context when the turn settles or is retired, even when another turn uses the same local ChatId; never share guest cookies, page storage or conversation state between turns. The per-turn session replaces the current fresh temporary profile/process behavior without weakening its isolation. Validate ChatGPT/Gemini guest page operations, and add explicit same-ChatId repeat-turn and cross-ChatId isolation tests.
- Connection-level session must not be shared through concurrent external Electron *main processes* using the same profile path.

### 4.3 Browser-context ownership and release

- For **authenticated** conversations, maintain one active context per (`connectionId`, `ChatId`) in the browser owner, with active-operation tracking and a single teardown path; anonymous guest turns instead own disposable per-turn contexts as specified in 4.2. Do not create a second Agent-specific browser registry/status table.
- Cancellation has three distinct phases: **before admission** (no permit or timestamp), **admitted but not submitted** (request aborts without provider submission), and **possibly submitted** (response outcome may be uncertain). A caller cancelling or losing its polling connection cannot imply the Electron operation stopped. Use operation-scoped cancellation/abandonment, preserve its context until local settle, and surface uncertainty instead of claiming the provider did not receive the turn.
- After Electron accepts an operation, propagate cancellation to the specific provider operation (AbortSignal/abortable fetch or page work where supported), retain its in-memory ownership until it settles, and retire only that context after a **bounded** drain/abort deadline if it cannot settle. Final retirement invalidates late callbacks and reports an explicit uncertain outcome. Do not terminate the shared host or another chat as a cancellation shortcut. Document provider limitations where remote acceptance cannot be undone.
- If the cancelled caller has already released the `MessageService` ChatId gate, a subsequent same-chat send must **wait for scoped drain or fail explicitly** rather than overlap the previous operation. Once the previous local work is settled/retired, allow a **new explicit user turn** on a fresh or safe context; never implicitly replay the uncertain previous turn. A permit is released exactly once on terminal settle/retirement, including host crash and forced context retirement; operation records and terminal results must be bounded even when the caller never polls again.
- The Agent runtime holds a context for the **duration of its execution**, not merely for the streaming response. A long `SH` or Wait does not consume a `Limits` turn permit but retains its context for the expected next prompt.
- Provide a minimal generic, idempotent **release-conversation** capability available at the existing integration/registry boundary. Call it from `AgentWorker`'s execution-lifetime cleanup when a runnable execution actually ends. Do not add Agent enums to `BrowserIntegrationBase`.
- Release must not close a context still serving an active turn, and must not race a newly started execution/reuse of the same chat. Use a minimal **generation/lease token** held by the existing browser context owner, associated with the Agent execution that acquired the hold: a stale terminal cleanup can release only its own hold and cannot destroy a newer execution's context. Reuse existing per-chat ordering and active-operation counts; no persisted lease, Agent-specific registry, extra limiter or polling on persisted statuses.
- Ordinary conversations retain idle-window timeout behavior. The host shuts down only when no live window or pending browser operation remains, after `transport.idleMinutes`; `idleMinutes: 0` means no automatic idle shutdown.
- On cancellation, never blindly replay an uncertain provider send. Preserve existing message/recovery contracts; if a Stop/Cancel request races an already-submitted turn, defer browser teardown until the operation settles or its bounded retirement explicitly reports uncertainty. A cancellation response alone is not proof of remote abort. On lost start acknowledgement, correlate by the caller-created operation ID rather than submitting again.
- When host exits/crashes, release resources and permits, preserve persisted chat IDs/history, and lazily recreate the window for a later turn. No duplicate submission as a side effect of recreation.

### 4.4 Model selection and authorization correctness

- Inspect whether the ChatGPT account-level `setModelPreference` mutation is needed for a per-turn request. Keep the explicit per-turn model/thinking-effort payload. Do not assume omission of the account PATCH is correct without live verification.
- **Remove the generic whole-delegate retry** in `BrowserAccountSession.UseCoreAsync` after `BrowserAuthorizationRequiredException`: today that catches a failure from anywhere in a provider action and invokes the complete delegate again, which is unsafe once a send may have been accepted. Perform authorization preflight separately. Allow automatic reauthorization/retry for read-only operations or a send with **positive, provider-specific evidence of non-submission**; otherwise stop, report authorization/uncertain outcome truthfully and require an explicit new user turn. Do not dispose the shared transport merely because one context needs login.
- Reauthentication uses a well-defined account/login window and a short session-mutation boundary, not a global model-turn lock. One chat's expired session, prompt submission, model selection or popup/login should never tear down an unrelated chat's active browser context; account-wide authentication invalidation must be reported honestly to affected operations.
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
- **Precise display precedence:** the newest nonterminal **Agent** execution wins (`CancelRequested` -> **Stopping**, `Running` -> **Running** or verified **Waiting** activity, `Queued` -> **Queued**); otherwise `Paused` -> **Paused**; otherwise newest terminal **Agent** execution -> its terminal status; otherwise **Unknown**. Sort deterministically by `CreatedAt DESC, ExecutionId DESC`. If `Paused` races a still-`Running` execution, keep the true Running/Stopping indicator and show paused state separately; do not claim the execution has stopped. Ignore Shell executions for the primary dot. Include the exact same projection in the sidebar and header.
- Use existing Agent runtime/store reads. The sidebar presently polls only `/v1/agent-chats` for the whole list, while `/runtime` is polled only for the selected chat. Add a **single batched read-only Agent status summary** to the existing list response (including `Paused` and newest Agent execution, not Shell history), rather than N+1 runtime requests. Share one UI derivation/helper for header and sidebar; selected-chat runtime may refine verified Waiting activity. Do not persist a second status, add a status manager or infer Waiting from stale historical activity.
- Accessible text/tooltip/`aria-label`; color must not be the sole signal. Honor reduced-motion settings; don't animate terminal indicators. Do not implement orchestration tree, unified `/v1/agents` endpoints, Prompt-anytime behavior, Stop/Resume, CLI, or other UI controls as part of these dots.
- Status indicators observe Agent lifecycle only; their implementation must not become the trigger for browser cleanup.

## 5. Risk register / mitigations

| Risk | Severity | Plan safeguard |
| --- | --- | --- |
| Concurrent profile ownership / auth corruption | Critical | One Electron main and `Session` owner per configured connection; integration tests for login, refresh and multiple windows |
| Wrong model / effort due to account-level preference | High | Per-turn payload + two-model concurrent verification against returned metadata; escalation on discrepancy |
| Cross-window IPC/progress/response confusion | High | Context/operation IDs, deterministic routing and concurrent interleaving tests |
| Cancellation, active-send teardown, uncertain resubmission | High | Caller-created operation IDs, scoped abort/abandon, bounded drain/retirement, one-shot permit release, no blind retry, injected crash/cancel tests |
| Authorization failure after an accepted provider send | High | Remove generic delegate retry; preflight + positive non-submission proof for any automatic retry |
| Guest privacy regression from a warm connection host | High | Fresh non-persistent session and renderer **per anonymous turn**, including repeat sends in the same chat; isolation/cleanup tests |
| Unbounded `maxParallel: 0` memory/CPU | High | Explicit opt-in, honest diagnostics and stress measurement; don't silently impose a hidden cap |
| Shared-base provider regressions (Grok/Gemini/guest) | High | Provider-specific regression and anonymous isolation tests |
| Status projection staleness / wrong sidebar dot | Medium | Canonical AgentStore/runtime projection, transition/paused tests; no N+1 fan-out |
| Startup and memory overhead | Medium | Measure cold host, warm window, 1/5/10 windows; don't assume fixed startup timing |
| Compatibility with future orchestration plan | Medium | Generic release boundary only; no status API refactor now; future plan must reconcile implementation |
| Security boundary for remote renderer | High | Review current IPC/preload/sandbox settings, no broadened privilege or undocumented bypass |

## 6. Implementation increments (one branch, reviewable commits)

1. **Baseline and fixtures:** map integration/transport callers and affected specifications; add timing/concurrency regression fixtures reproducing serialization. Establish current login/diagnostics/model/cancel behavior and a safe manual parallel test fixture.
2. **Limits:** typed nested config and validation; central `BrowserIntegrationBase` admission wrapper, monotonic spacing and cancellation; unit tests covering 0 semantics, missing defaults, fairness and error release.
3. **Connection-owned Electron host:** migrate one-window/global-queue contract to ChatId-targeted **authenticated** windows/operations with shared authenticated session, independent progress, login and diagnostics. Establish caller-created operation ID, scoped cancellation/abandon, bounded drain and terminal-result cleanup; guest turns use fresh non-persistent sessions/renderers in the same main host. Preserve provider compatibility.
4. **Provider correctness:** update browser module context and scoped-abort plumbing; remove unsafe whole-send re-invocation on authorization failures; verify preflight versus post-submit auth expiry, distinct model/effort outcomes, concurrent auth renewal, uploads and streamed replies. Remove redundant account preference mutation only with evidence.
5. **Lifecycle:** minimal generation-safe per-conversation Agent release, cancellation drain/retirement and permit ownership, AgentWorker execution-finalization hook, non-Agent idle expiry, warm main-process idle shutdown and crash/cancel safety. Migrate the existing whole-transport cancellation tests to scoped lifetime assertions.
6. **Agent Web dots:** use actual existing runtime and paused state; add batched read-only list summary and shared deterministic display precedence, with small accessible status indicators in sidebar and header. Derive optional Waiting activity only from current verified data; test without new Agent statuses.
7. **Cleanup:** remove obsolete global send lock/queue, stale code paths and misleading 'shared slot' message; update implemented/current specifications, tests and generated Codegraph before review.

**Mandatory complexity check:** If safe session sharing, lifecycle draining, or model selection requires a second scheduler, duplicated persistence, global model lock, or unrelated orchestration refactor, stop and escalate the requirement and simpler alternatives to the owner. No piecemeal fallback architecture.

## 7. Verification and acceptance

**Automated and targeted integration tests**
- Same ChatId remains ordered under simultaneous sends; distinct ChatIds on the same connection genuinely overlap when `maxParallel >= 2` and spacing is 0. No global operation queue remains.
- At most configured active model turns; `maxParallel: 0` removes concurrency cap; `minIntervalBetweenStartsMs: 0` disables spacing. Missing config behaves as legacy one-at-a-time. Invalid negatives rejected at startup.
- Admission times satisfy the configured monotonic minimum interval under contention; cancellation of a queued/pacing request does not consume a slot or reserve a start. For admitted work, permits release only when scoped local operation settles or is retired, not when the HTTP polling caller stops. Assert exact-once release under abort, lost-start-ack, renderer crash, host crash and no-follow-up-poll conditions.
- Concurrent different-model/different-effort ChatGPT requests produce the intended per-turn outcomes; run side-by-side live verification where permitted. Unit mocks alone are insufficient proof of provider behavior.
- Inject a 401 **before** submission and an authorization error/**uncertain result after** the provider accepts submission. Prove that only confirmed non-submitted sends may be retried, and no generic delegate replay or second POST occurs on an ambiguous outcome. Reauth in one chat must not discard an unrelated in-flight chat.
- Auth login/model discovery/diagnostics continue working while multiple windows exist; popup and file upload/download tests remain valid. Guest ChatGPT/Gemini turns have fresh non-persistent sessions/pages **even for consecutive sends with the same local ChatId**; no guest cookie/storage/history crosses turns or other chats. Guest contexts are reclaimed on completion/cancellation.
- After `/invoke` accepts a send, cancel chat A while chat B remains active; verify A's operation receives scoped abort/retirement, B finishes, no overlapping operation for A during drain, no leaked operation record/permit, and an explicit subsequent user turn is possible. Cover cancellation **between invoke POST and start acknowledgement** using the stable caller-created operation ID. A renderer crash/release does not close another chat; a host crash reports failure/uncertainty without re-sending accepted turns.
- Agent execution performs model -> long-running shell command -> model follow-up on same retained context; when terminal, the window closes only after all browser work is settled. Agent statuses and policy behavior remain unchanged.
- Normal chat idle cleanup and connection-host idle shutdown work, including idleMinutes=0, without orphan windows/processes. Measure cold/warm startup and 1/5/10-window resource use.
- Agent Web dots exercise Queued/Running/verified active Waiting/Paused/CancelRequested/Completed/Failed/Cancelled/unknown, plus transient Paused+Running/CancelRequested precedence and tied execution timestamps. Verify sidebar and header agree, screen reader labels, reduced motion, and a **single list-level summary query**, without N+1 polling.

**Required regression suites:** build/architecture, config, account login, Grok account login, guest providers, cancellation contracts, Electron transport/IPC, Agent API/runtime, Agent Web UI and targeted manual parallelism. **Explicit migration:** `tests/integration-cancellation-contract.ps1` must stop expecting the *entire shared transport* to be disposed after one cancelled operation; `tests/agent-provider-lifetime.ps1` must stop matching the old `BrowserAccountSession` disposal statements. Replace both with behavior-based scoped-cancel, context-drain, no-replay, and unaffected-peer checks, while preserving prompt cancellation of callers. Record why old assertions became invalid and prevent weakening their intent. Use the repository's existing test entry points and add missing fixtures; name actual commands/results in the implementation report. No fabricated CI or live-provider results.

**Acceptance:** all applicable independent reviews/tests PASS, current specifications match behavior, instrumentation demonstrates real overlap, risk/limitations documented, exact commit/PR supplied for owner approval. No merge until explicit owner authorization.

## 8. Handoffs and release

- **Now:** independent **Plan Reviewer** checks this document, the actual source, existing contracts, and `.agents/workflow.md`; verdict PASS/FAIL/BLOCKED with actionable findings. No implementation until PASS and owner decision to proceed.
- **Then:** Developer implements on **this same branch**, produces developer checks, refreshes Codegraph, moves final plan from `planned/` to `implemented/` before code review.
- **Then:** independent Code Reviewer and independent Tester, in that order; fixes repeat affected stages. Pause for owner approval.
- **Release only:** approved merge into `main`, single version/changelog finalization, release tag and branch cleanup per `.agents/release.md`. Do not edit release metadata on the task branch.
