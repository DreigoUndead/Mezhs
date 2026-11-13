# Browser conversation parallelism, limits and Agent status indicators

**Status:** Implementation committed on the task branch; awaiting independent code review, then independent testing (High-risk). Approved plan revision: `203ed641`.
**Branch:** `browser-conversation-parallelism`; PR #54 (based on `main` at `2b17b887701d25822bfa1048650185153edbb164`).
**Scope:** Browser integrations, Electron connection host, small Agent execution-lifetime release hook, and Agent Web sidebar/header dots.
**Reference only:** `docs/plans/multi-agent-orchestration.md` on `plan/multi-agent-orchestration`. Do not implement its APIs, state machine, hierarchy, scheduler or CLI.

## 1. Goal

Independent conversations using the same configured browser connection must be able to talk to the model concurrently, like tabs. A single active local ChatId remains ordered. Authentication may be shared; conversations, requests, results and progress must not cross ChatIds.

The existing bottlenecks are the whole-turn semaphore in `BrowserAccountSession.UseCoreAsync` and the global `operationQueue`/single `window` in `electron/main.js`. `MessageService` already serializes active local sends per ChatId; **reuse it**, do not introduce another connection-level queue in Electron.

Add connection `limits` (parallel turns and minimum time between starts), retain Agent browser windows across command cycles, release them at execution end, and show Agent status dots in the sidebar and selected header. Measure actual overlap; do not assume all observed delay comes from the known locks.

## 2. Owner-approved architecture and intentional trade-off

**Decision (2026-11-12): Prefer simple ownership and best-effort lifecycle handling over a system of defensive protocols.** This explicitly supersedes the previous plan-review additions requiring stable client-generated operation IDs, lost-acknowledgement recovery, bounded drain/retirement protocols, extra same-chat schedulers, generation leases, and strict permit tracking after the caller abandons work.

This follows the **implemented Agent delete** approach in `AgentService.DeleteChatsAsync`: use the existing owners to request cancellation and delete/release resources, without a new coordination subsystem. Some late work and partial outcomes are acceptable; retry/recovery must remain an explicit operation, not an invented global guarantee.

The intended cancellation contract is:

- Cancel **only the affected local browser operation/context**, best effort. Never dispose the shared Electron process or unrelated ChatId windows to cancel one request. Ignore stale progress/results from a cancelled context and clean up the local state using the existing transport/window lifecycle.
- `maxParallel` counts locally admitted, active **model sends**; release the local permit when that send completes, fails or its local work is cancelled/abandoned. **It does not attempt to count remote provider computation after a local cancellation.** The provider might still process an accepted turn, or its remote outcome may be unknown.
- Same-chat ordering is enforced for **active local sends** by the existing `MessageService` gate. After cancellation, an explicit later send may proceed even if the prior remote provider request has not conclusively stopped. No implicit replay of the cancelled/uncertain submission; do not claim remote cancellation, exactly-once delivery or confirmed non-submission.
- Keep lifecycle mechanisms **small and owned by the component with the necessary state**. If a basic context-specific abort/close hook is needed in the existing Electron transport, add just that hook. Do **not** build a separate operation state machine, acknowledgement/replay protocol, tombstones, durable scheduler or lifecycle service for hypothetical races.
- Keep existing security boundaries, per-chat routing and resource cleanup. Simplicity **does not** permit sharing another chat's content, credentials or results, killing unrelated work, leaking unbounded local resources, or automatically resubmitting an uncertain turn.

**Instruction to the next independent reviewer:** Evaluate this **explicitly accepted weaker guarantee**, not the superseded stronger one. Do not report missing operation IDs, drain deadlines, exact-once remote cancellation, generation leases or post-cancel remote-concurrency accounting as defects **on their own**. A blocking finding must identify a violated requirement/security boundary or a plausible *concrete failure in this ownership design*, provide source evidence and recommend the **smallest architectural correction**. If a stricter guarantee is genuinely required, escalate its value/cost to the owner per `.agents/workflow.md`; do not reintroduce it implicitly through a list of edge-case safeguards.

## 3. Ownership and design

### BrowserIntegrationBase: configuration and admission

- Parse typed nested `connections[].limits`: `maxParallel` (omitted default **1**; explicit **0** unlimited) and `minIntervalBetweenStartsMs` (omitted/default **0** no spacing). Nonnegative integers; reject invalid values at startup. Example:

  ```yaml
  connections:
    - id: chatgpt
      limits:
        maxParallel: 0
        minIntervalBetweenStartsMs: 0
  ```

- `BrowserIntegrationBase` constructs/owns/applies the connection limits through **one common send path** used by all browser providers, including guest sends. Do not rely on each provider remembering to acquire a slot; do not add a second limiter in AccountSession, Electron, Agent or MessageService.
- Use a simple cancellation-aware semaphore for parallel local turns and a small monotonic start-spacing gate. Starts are spaced by **admission**, not later HTTP submission. Cancellation while waiting should not consume admission. A turn holds its permit only for its **local active send lifetime**; the permit is released on local completion/failure/cancellation. No quota framework or future five-hour/weekly budget implementation.
- Keep existing public config, integration discovery and constructor compatibility. `ConnectionOptions`, `MezhsConfigLoader`, `IntegrationRegistry` and `IntegrationConnection` carry the typed limits to the browser integration; provider modules still own how to send.

### Electron transport: shared host, isolated conversations

- One Electron **main process per browser connection**, not one app process per ChatId; one shared authenticated Electron `Session` per connection, with an independent `BrowserWindow`/renderer and context for each authenticated **local ChatId**. Do not open multiple Electron apps on the same persistent Chromium profile.
- Replace the module-global `window`, `activeSession` and provider `operationQueue` with small per-context routing/ownership. Each existing transport invoke/result/progress operation targets its context. The existing ChatId serialization remains at `MessageService`; Electron must not serialize different conversations.
- The connection owns shared auth and warm main-process lifetime. Synchronize only short genuine account/session mutations (e.g. login), **not a whole provider turn**. Keep diagnostics, login, browser opening, model discovery, popup handling and uploads working with multiple windows; select their context deliberately.
- On a cancelled local send, request abort of its operation if practical, otherwise close/discard that **conversation context**; return cancellation promptly. Do not await external provider completion, keep a shared host hostage, or implement an elaborate poll-drain scheme. Finish/remove the obsolete local operation state and ignore late callbacks. Normal transport errors or host crash fail local calls; a new **explicit** send may recreate its context.
- Anonymous providers keep their pre-existing **fresh per-turn isolation**: create a new ephemeral/non-persistent Electron session and window per guest model turn, including another turn in the same ChatId. No guest cookies, local storage or page state carry over. Reuse the Electron *main process* only.
- Preserve Electron IPC/preload authorization checks and existing sandbox/security constraints. Do not expand renderer privileges to make routing easier. Keep warm host shutdown using existing `transport.idleMinutes` semantics (0 disables automatic idle shutdown). Ordinary non-Agent contexts may be retired when idle.

### BrowserAccountSession and providers: auth and model selection

- Remove `BrowserAccountSession`'s **whole-turn** account semaphore; retain only appropriately short login/session synchronization. Cancelling chat A must not dispose a shared transport that chat B uses.
- Remove the existing blanket `BrowserAuthorizationRequiredException` catch-and-**reinvoke-the-whole-send-delegate** behavior. If auth fails mid-send, fail that attempt honestly; authenticate on a separate/pre-send path for a later **explicit** user attempt. No complicated proof-of-non-submission/retry mechanism is required.
- Model and thinking effort belong to each **turn**, not shared mutable account preference. ChatGPT already submits `model` and `thinking_effort` per turn; investigate whether its account-level `setModelPreference` PATCH can safely be removed. Verify two simultaneously selected models/efforts against actual response metadata. Do not silently add a global model lock if a provider behaves differently; ask the owner.
- Preserve ChatGPT/Grok account behavior and ChatGPT/Gemini guest flows. Keep provider-specific code in the provider modules rather than a generic browser-operation framework.

### AgentWorker: one small lifetime hook

- Keep an authenticated context for a running Agent execution across model reply -> shell command/wait -> model follow-up, **without holding a send permit during the shell/wait**.
- At execution-finalization, make a **best-effort, idempotent** generic release-conversation call through the existing integration/registry owner, using the execution's ChatId/connection. The browser context owner discards the window when it is locally idle. Reuse existing execution/chat ordering; no Agent-specific browser registry, persistent lease, generation token, per-execution browser process or status manager.
- Ordinary chats continue using idle cleanup; the shared host stays alive while other contexts are active.

### Agent Web: status dots, not orchestration

Add small accessible indicators in Agent sidebar rows and selected-chat header, using only **existing** Agent execution and paused data. Adapt the visual ideas from multi-agent orchestration, but do not create new persistent statuses or endpoints for orchestration.

| Existing state | Display |
| --- | --- |
| Queued | Neutral dot: Queued |
| Running | Active/animated dot: Running |
| Verified current waiting activity (optional) | Orange dot: Waiting; otherwise stay Running |
| CancelRequested | Stopping, not terminal |
| Paused chat with no active execution | Gray dot: Paused (not Stopped) |
| Completed | Green dot: Completed |
| Failed | Red dot: Failed |
| Cancelled | Gray dot: Cancelled |
| Missing/unavailable | Neutral: Unknown |

Prefer a currently active **Agent** execution over a paused flag or historical terminal record; otherwise paused over the latest terminal Agent result. Exclude Shell history from the primary indicator. Use **one batched read-only projection** on the existing Agent chat list (or equivalent single read), rather than per-row runtime polling. Sidebar and header use the same derivation; existing selected-chat runtime data may refine an observable Waiting signal. Keep existing status text, accessible labels, non-color cues and reduced-motion behavior.

No Stop/Resume state machine, Agent tree, new polling service, extra persistence or CLI is part of this task.

## 4. Implementation path (same branch)

1. Record baseline overlap/serialized behavior and identify all provider/transport callers. Establish existing auth, cancellation and diagnostics contracts before editing.
2. Add typed limits and one common `BrowserIntegrationBase` admission wrapper, with focused unit tests.
3. Make Electron a connection-owned multi-window host with per-context requests/progress; remove the old global queue/whole-turn account lock. Maintain simple scoped cancellation and per-turn guest isolation.
4. Preserve account login and per-turn model selection; remove blanket auth retry and only remove the account-model PATCH after live evidence.
5. Hook Agent execution-finalization to generic browser-context release, add ordinary idle cleanup, then Agent Web status dots using existing state.
6. Update affected tests, `docs/architecture.md`, relevant `specifications/current/`, and generated Codegraph when applicable. Move the final plan from `planned/` to `implemented/` **before** independent code review/testing.

**Complexity stop rule:** If implementation begins requiring a second scheduler, comprehensive operation recovery protocol, durable leases, global model-turn lock, or a new Agent status manager, **stop and ask the owner**. Explain which guarantee forced complexity and whether it can be relaxed, as in this decision record.

## 5. Acceptance and verification

Focus on observable behavior, not exhaustive hypothetical protocol states:

- **Parallelism:** two different ChatIds on one configured account overlap in real model responses when `maxParallel >= 2`, spacing 0; same ChatId's **active local** turns remain ordered. No Electron global queue or whole-account-turn lock.
- **Limits:** missing values default to 1/0, explicit 0/0 disables both constraints, positive spacing is honored, cancelled waiters do not occupy capacity, and locally cancelled/failed sends release capacity.
- **Isolation:** chat A's response/progress/model selection cannot appear in B; guest ChatGPT/Gemini sends retain fresh per-turn state. Two concurrent different ChatGPT models/efforts are verified with actual provider output where permitted.
- **Cancellation:** cancel chat A while chat B is running: A stops locally and its local resources are released/ignored; B completes unaffected; no automatic resubmit or global host shutdown. Record the accepted limitation that the remote provider might still process A and a subsequent explicit send is not guaranteed remote ordering.
- **Authorization:** login/model discovery and concurrent account turns still work. Authentication failure during a send is not retried by replaying its whole delegate; next explicit turn can recover after login.
- **Lifecycle:** Agent follows model -> shell -> model using a retained authenticated context, then releases at terminal execution. Regular-chat idle cleanup and main-host idle shutdown retain previous configuration semantics.
- **UI:** sidebar/header agree for Queued, Running, CancelRequested, Paused, Completed, Failed, Cancelled and Unknown; optional Waiting only when observed. No N+1 per-row requests; labels and reduced motion work.
- **Regression:** run applicable architecture/build/config, account/guest provider, Electron IPC, Agent/API/Web and cancellation tests. In particular update `tests/integration-cancellation-contract.ps1` and `tests/agent-provider-lifetime.ps1`: their old expectation that cancellation **disposes the entire transport** must become a check that only the affected context is cancelled, its local permit is released, and peers survive. Do not retain assertions against the removed disposal implementation.
- Compare cold/warm startup and concurrent browser resource usage; do not promise arbitrary unlimited parallel performance merely because `maxParallel: 0` permits it.

Report real commands/results and known provider limitations; do not claim tests were run in planning. Preserve security checks and do not weaken them to satisfy a test.

## 6. Implementation evidence and handoff

This remains **High-risk** because browser authentication and Electron conversation ownership are architectural changes. The owner-approved weaker cancellation/replay guarantee in section 2 is intentional; independent reviewers should evaluate observable failures and security boundaries without reintroducing an elaborate protocol.

Implementation changes on the task branch include connection-level limits, per-context Electron transport/windows, guest turn isolation, Agent context retention/release and Agent Web status dots. The provider-specific ChatGPT account-level `setModelPreference` PATCH is deliberately retained until **live** concurrent model/effort response evidence justifies changing it.

**Developer verification recorded before independent review:** .NET Agent API build (0 warnings/errors), Agent Web TypeScript/Vite build (pass, with existing large-chunk advisory), 19 targeted Node provider/IPC tests (pass), account-login/diagnostics and Agent lifetime regressions (pass), and seven integration cancellation/limits registrations (pass). A new configuration parsing test was added for limits defaults and invalid values. The local tests use mock provider transports and do **not** prove overlap against a real ChatGPT or Grok account, real concurrent per-turn model choice, or Electron UI resource measurements; those should be tested independently where live access is permitted.

1. Independent **Code Reviewer** checks the complete PR diff against this implemented plan, current specifications and security boundaries. Reviewer returns PASS / FAIL / BLOCKED with actionable evidence, without changing files or merging.
2. After any code-review fixes, a separate independent **Tester** runs final technical and live checks, including realistic overlapping account sends, cancellation isolation, auth and guest behavior where credentials/browser environments permit. Report untested live scenarios explicitly.
3. The owner separately approves any merge. `main` is release-only; do not update `VERSION` or `CHANGELOG.md` on this task branch.


## 7. Post-review lifecycle corrections (2026-11-13)

After the first independent code review, four concrete lifetime/ownership issues were corrected on the same task branch:

- Ordinary authenticated chat windows now retire when each local send settles, even if the shared host's idle shutdown is disabled; the authenticated Electron process remains warm under its existing host policy. Agent executions retain their selected chat context between model turns and shell work.
- Agent retention/release resolves `execution.ConnectionId` through `IntegrationRegistry`, rather than broadcasting to all browser connections. Counts preserve a context while another overlapping Agent execution still retains the same ChatId.
- Electron's bounded best-effort cancellation request is awaited before the cancelled local send returns. The account-session owner also retires the cancelled context before a later explicit same-chat send is admitted. Remote provider cancellation and exactly-once behavior are **not** claimed.
- The Agent status-dot border-radius declaration was corrected.

Regression additions: `tests/account-login-flow.ps1` exercises ordinary retirement, overlapping Agent retention and retained-context cancellation; `tests/electron-conversation-contexts.test.js` uses the actual Electron context functions with mock windows/sessions to check account isolation, guest ephemerality, cancellation isolation and release-after-busy behavior; `tests/agent-provider-lifetime.ps1` checks connection-scoped ownership and ordered cancellation. `tests/diagnostics-architecture.ps1` was updated to recognize the existing account/guest host split without relaxing diagnostic capability checks.

These are targeted local-lifecycle guarantees, not an expansion of the owner-approved remote cancellation contract. The reviewer/tester should still perform independent confirmation of provider behavior, same-chat explicit retry, resource usage and concurrent model/effort selection. The post-review Codegraph refresh requires the full local repository/export tool and was not performed through the GitHub-only correction environment; do not treat the existing graph as source truth.

## 8. Owner-approved second review disposition (2026-11-13)

The fresh independent review at `033de6d87d5bbb3a7835a12d094472bc83cb8ce6` identified two additional local lifecycle failures. Under the workflow's **two-fixes/design-reassessment** rule, the owner explicitly chose minimal corrections rather than a larger lifecycle/retry redesign:

- **P1:** One interactive login attempt per configured account; other chats for that account wait for the attempt and then recheck saved cookies without opening a second dialog. Keep short locking for shared transport state, but **never hold that lock while waiting for human login**. An existing model send must be able to cancel and release its local permit independently. Closing the login window (with or without a cookie) ends the attempt; if no authorization was obtained, callers get an explicit authorization error. Other configured accounts are unaffected.
- **P2:** A conversation's Electron BrowserWindow is exposed to its context owner immediately after creation, before `loadURL`/provider initialization. Cancellation or ordinary release closes only that window even when navigation is stalled. No new deadline, remote cancellation guarantee, recovery service or operation replay protocol.

This is an owner-approved **minimal architectural correction**, not approval to change the broader guarantees in section 2. Targeted regressions cover stalled interactive login plus peer cancellation, second-chat login deduplication, closing/dismissing the login window, and cancellation of a still-navigating account/guest window. Independent review and independent High-risk testing are still required; CI alone does not authorize merging.
