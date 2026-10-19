# Plan: agent-model-presets architecture review cleanup

**Date:** 2026-10-19
**Status:** Implementation in progress; T1 (#7) and T2 (#2) committed, verified, coordinator-reviewed 2026-10-19. T3 next; remaining tasks pending.
**Target:** Existing agent-model-presets review branch; do not merge until the user reviews the completed diff and explicitly approves.
**Scope:** All eight findings from the 2026-10-19 code review, plus the follow-up on global workers/concurrency. This is not the separate orchestration proposal or the general execution-UX backlog.
**Guidance:** Read .agents/README.md and the repository-change-workflow and software-change skills before modifying code. For observed defects follow systematic-debugging. Reuse the existing review branch rather than splitting this review into speculative branches.

## Coordination and ownership (2026-10-19)

- **ChatGPT coordinator (this review):** #1 (keep existing relative Electron diagnostic import), #3 (preserve meaningful JS source boundaries), #7 (move diagnostics endpoint to shared MEŽS API with loopback remote and browser-origin restrictions, remove Agent-only mapper, add structural test). #7 code is committed but not yet compiled or exercised live; the MEŽS agent must run the verification gates below.
- **MEŽS local implementation agent:** #2 (base-class account session/diagnostics ownership, preserving inheritance), #4 (deploy source JS files directly without a bundler), #5 (guest pageOperations rewrite and live guest characterization), #6 (safe, general escaped-tag/parser correction based on actual raw replies), #8 (remove global limits and fixed worker pool while preserving durable dispatcher and recovery).
- **Ownership rule:** Execute the MEŽS handoff after pulling the already-pushed coordinator changes. Work on the same agent-model-presets review branch; do not overwrite, revert, or parallel-edit the coordinator's files without reviewing them. Existing untracked/local user files are not cleanup targets.
- **Verification:** T1 shared diagnostics validation completed and reviewed (commit `42ae609`): reported HTTP 22/22, targeted builds/publish and diagnostics/Agent/Electron tests passed; remote-IP denial exercised via synthetic remote address; pre-existing full architecture failure on generated JS remains assigned to T3. Remaining tasks require their own checks. No merge authorization is implied.

## Small-task handoff sequence

**Run one MEŽS agent task at a time.** Each agent run must read the repository skills, inspect the local tree, synchronize without destroying local work, implement *only* its assigned scope, run targeted tests, commit/push to `agent-model-presets`, report file/test/commit evidence, and stop. The coordinator reviews each report before the next task. Never merge.

1. **T1 — Shared diagnostics verification (#7): COMPLETE, reviewed.** Agent commit `42ae609`; targeted builds/publish, HTTP 22/22, diagnostics tests and relevant architecture checks reported PASS. Non-loopback-IP check used a synthetic peer address; full architecture JS-packaging failure deferred to T3.
2. **T2 — Browser diagnostics/session ownership (#2): COMPLETE, reviewed.** Agent commit `c985bba`; centralized base-owned account session/diagnostics/disposal, preserved hierarchy and guest separation. Targeted account/Grok ownership tests, diagnostics and provider checks reported PASS. Live Grok snapshot and browser opening worked; Grok message encountered provider ToS gate and ChatGPT Account lacked login. Two historical full-flow tests reportedly also fail on baseline; validate integrated provider flows in T7.
3. **T3 — Source JS module packaging (#3/#4).** Remove generated `chatgpt.ts`/build.js, package normal CommonJS files; update build and architecture tests. Preserve other providers.
**T3 preparation:** Electron `main.js` and `browser-preload.js` already use Node `require(absoluteModulePath)` and should not need a replacement loader. Update references in `tests/browser-provider-operations.test.js`, `tests/chatgpt-account-api.test.js`, `tests/chatgpt-conversation-unavailable.test.js`, `tests/chatgpt-guest-composer.test.js`, `tests/agent-provider-lifetime.ps1`, `tests/architecture.ps1`, and `browser/tsconfig.json`. Preserve the T4 guest behavioral test coverage while changing only how it loads the entry module. Avoid a generated duplicate source; use ordinary CommonJS sibling paths. Verify resource extraction is safe on repeated initializations and actually works when launched from published artifacts.**

4. **T4 — Guest send simplification (#5).** Reuse preload `pageOperations`; eliminate guest `executeJavaScript`, bounded observation, no blind replay. Characterize live guest access without inventing success.
5. **T5 — Parser presentation/protocol boundary (#6).** Investigate actual escaped-command samples first, remove DONE-only workaround safely, keep literal examples inert and block bodies opaque.
6. **T6 — Agent dispatcher and global-limit removal (#8).** Remove global queue/worker limits as one atomic change while preserving durable claim, lifecycle, same-chat serialization, cancellation and recovery. If scope proves too large, stop after a concrete design/testing report rather than leaving a broken intermediate state.
7. **T7 — Integrated validation and documentation.** Full relevant .NET/Node/PowerShell tests, isolated deployment checks, harmless authenticated multi-turn/live Agent test, docs update, final diff review and push. No extra implementation unless correcting a demonstrated regression.

**Do not parallelize tasks that share `agent-model-presets` or run a later task on stale code.** The current handoff includes #7 implementation committed by the coordinator; that code is not yet verified by a local build/runtime. Each task's completion does not imply approval to merge.

## Decisions and non-goals

- Prefer deleting machinery and moving responsibilities to their existing owners. No new integration base class, diagnostics application, queue framework, dependency-injection layer, or provider-agnostic browser automation framework.
- Preserve existing inheritance, especially ChatGptAccountIntegration -> ChatGptWebIntegration -> BrowserIntegrationBase. Preserve each other provider's existing hierarchy (do not invent a guest class for providers that have none).
- Preserve connection IDs, account authorization, chat history/remote IDs, model selection, file/image input and output, and the accepted multi-turn ChatGPT Account flow.
- Keep the existing durable SQLite execution state and per-chat serialization. Remove global Agent admission/worker limits, not durable scheduling.
- Do not implement per-integration concurrency/rate limits here. That is separately planned future work.
- Keep diagnostics powerful enough to investigate browser internals; isolate them at a trusted local access boundary, avoid leaking credentials, and do not use diagnostics as the normal sending path.
- Avoid speculative protocol retries or re-submission of a prompt whose delivery is uncertain.
- Implement, test, document, and review as one coherent set on the existing review branch. No merge without post-review approval.

## Issue 1: Electron main.js imports ./diagnostics

**Decision: KEEP the relative import. No configurable path or dependency-injection wrapper.**

The require("./diagnostics") path is relative to electron/main.js, not the caller's working directory. The existing generic Electron host may dispatch reserved diagnostics operations. Review provider-specific logic such as inspectRspackExports separately: diagnostics is allowed to contain low-level provider-oriented inspection where justified; do not construct a generalized plugin scheme just to hide one such operation.

**Acceptance:** Electron launches from a different working directory and packaged output; the diagnostic import and operation dispatch still work. No new configuration or abstraction introduced solely for this path.

## Issue 2: Shared browser diagnostics should be owned by BrowserIntegrationBase

**Decision: MOVE the existing diagnostics interface implementation to BrowserIntegrationBase without adding a new base class.**

- Preserve ChatGptAccountIntegration inheriting ChatGptWebIntegration. Grok and other providers keep their current parent types.
- Make the existing CreateAccountSession mechanism reuse one session per integration instance, so account sending, login and diagnostics use the same serialized browser transport and persistent profile.
- Have BrowserIntegrationBase expose the existing BrowserDiagnosticsModule when that persistent session exists, and own disposal of the shared session. Do not change anonymous/guest connections into implicitly authenticated accounts or report diagnostics as supported where no usable account session exists.
- Remove duplicate _diagnostics fields, constructors, Diagnostics overrides, and now-redundant session disposal overrides from ChatGPT/Grok account integrations. Keep provider-specific send/model behavior.
- Validate session creation, disposal, login, hidden/visible window behavior, and concurrency; avoid adding capability flags or a second session cache unless the current mechanism actually requires one.

**Acceptance:** Account integrations share one profile/session between messages and diagnostics; anonymous behavior does not regress; no additional inheritance layer or double disposal; existing provider operations work.

## Issue 3: Number of ChatGPT browser JavaScript files

**Decision: KEEP meaningful source-module boundaries, REMOVE generated duplication.**

The eight source modules separate account transport, messages, results, stream, files, models, guest, and entry. Their count alone is not a defect. Do not merge unrelated responsibilities merely to lower the file count. Remove an individual module only if actual dead code or duplicated responsibility is proven.

**Acceptance:** Each remaining source module has a clear purpose; there is one editable source of truth per function and no second bundled copy in source control.

## Issue 4: 1,641-line generated chatgpt.ts bundle

**Decision: REMOVE the bundler and deploy ordinary CommonJS source files.**

- Remove browser/build.js and the generated browser/chatgpt.ts from the build and tracked sources.
- Embed the ChatGPT browser/modules/*.js files in the integration assembly, or an equivalently simple packaging method that carries all sibling files reliably in published deployments.
- Extend the existing BrowserIntegrationBase extraction logic only as needed: extract the module directory preserving relative paths, return the path to entry.js, then let Node's ordinary require resolution load the siblings. No custom runtime require implementation and no new packaging service.
- Preserve the existing single-file path for Gemini/Grok if still valid; do not force unrelated providers into a new layout.
- Update project files, .gitignore as appropriate, tests, documentation, and stale chatgpt.ts references. Embedded resources must work without relying on a developer's source checkout.

**Acceptance:** Build/publish and Electron preload/main both load the entry module and all relative requires from extracted files; authenticated send, files, models, guest routing, and existing other providers continue working. There is no generated 1,641-line source or mandatory build-time JS bundling.

## Issue 5: guest.js uses executeJavaScript

**Decision: REUSE the existing Electron preload/pageOperations IPC path already used by Grok; simplify guest handling.**

- Move the guest DOM interaction into a regular pageOperations method, called through context.page.invoke("sendPrompt", args), rather than building/injecting a script string in guest.js.
- Reuse the existing browser-preload.js and IPC implementation; do not introduce a new bridge or browser automation abstraction.
- Minimize selectors and state checks. Remove the temporary global fetch monkeypatch if the same behavior can be obtained without it; if reliable force_login observation is unavailable, report the factual UI/submission failure rather than inventing an authorization diagnosis.
- Give guest submission and reply observation a bounded, cancelable lifetime. Do not retry blindly after an uncertain submission, including an apparently empty completed reply: completed/empty, no detected submission, and explicit login-required remain distinct observations when supported.
- Guest changes must NOT accidentally alter the authenticated Account native-requirements flow. account-transport.js currently uses a separate executeJavaScript call for page-owned Sentinel requirements; that is not the guest.js refactor and must be handled only with an independently verified equivalent, not removed mechanically.

**Acceptance:** No executeJavaScript in the guest sending path, no hanging response polling, no accidental duplicate turn submission, existing guest UI tests updated, and live guest behavior tested if guest access is available. If upstream requires login, report that limit instead of claiming a successful guest conversation. Verify authenticated multi-turn sending separately.

## Issue 6: Hardcoded escaped DONE in Agent Parser

**Decision: REPAIR the protocol/presentation boundary with the simplest evidenced rule, not more command-specific cases.**

Current behavior: Parser special-cases the entire line "<escape><DONE></escape>" into DONE, but treats escaped SH as literal; existing regression tests explicitly depend on that difference. The escape element is presentation markup used to display literal tag-like text, not an Agent command. Blindly unwrapping all escaped SH/ATTACH tags could turn examples into shell actions.

Implementation steps:
1. Inspect actual saved assistant reply text and the transport/display boundary to distinguish raw protocol lines from rendered escaped markup. Reproduce the original escaped-DONE case and the presentation-markup rejection case.
2. Prefer ignoring/removing presentation wrappers before the command parser **only if** there is an evidenced, safe way to distinguish protocol output from literal examples. Otherwise keep escaped examples inert and ensure the integration/prompt delivers canonical raw command lines; do not silently make escaped SH executable.
3. Remove the one-off DONE comparison once a provider-neutral rule demonstrably handles completion. Use the existing command Registry for valid marker/block names; no new parser framework.
4. Keep SH/ATTACH block bodies completely opaque. Unknown ordinary formatting must remain display text; malformed real command-shaped syntax must fail safely.
5. Explicitly document the unavoidable ambiguity if transport provides only identical plain text for a literal escaped tag and an intended command; choose the fail-safe interpretation rather than a hidden heuristic.

**Acceptance:** Raw DONE, SH and ATTACH work; completion still works for actual ChatGPT responses; no literal escaped command example executes; ordinary UI markup is inert; SH bodies are not decoded or reinterpreted; new registry commands require no hardcoded name checks. Extend tests/agent-protocol-result.ps1 and include the reported <box> incident.

## Issue 7: DiagnosticsApi belongs in the reusable API

**Decision: MOVE the existing endpoint, not create another app/server.**

- Move endpoint mapping from src/Mezhs.Agent.Api/DiagnosticsApi.cs into the shared src/Mezhs.Api composition (a nearby source file or MezhsApi.cs, whichever is simplest).
- Register it once in MapMezhsApi(), so ordinary MEŽS API and Agent API expose the same route: POST /v1/diagnostics/connections/{connectionId}/browser/{operation}.
- Remove MapAgentDiagnostics() and duplicate endpoint registration from Agent Program.cs; retain the existing IntegrationRegistry + Diagnostics contract and response/error behavior.
- Because the shared API may listen beyond localhost while diagnostics can click/type/read browser contents, keep diagnostics locally reachable only; do not turn CORS into an authorization claim. Within that boundary allow legitimate low-level diagnostic operations instead of over-sanitizing debugging.
- Avoid logging secrets, tokens, full sensitive response bodies, or raw browser credentials. Do not mix diagnostic operations into production provider sending.

**Acceptance:** One route implementation; both API hosts can invoke it locally; remote clients cannot access powerful diagnostics; models/messages endpoints unchanged; existing diagnostics tests and smoke tests pass.

## Issue 8: Why QueueCapacity, MaxConcurrentExecutions and fixed workers exist

**Decision: REMOVE global admission and worker-count limits; PRESERVE durable execution scheduling and lifecycle.**

Current code uses QueueCapacity (32) + MaxConcurrentExecutions (4) as a global maximum outstanding Agent count, and starts four async AgentWorker consumers. This protects a global count but does not protect individual integrations; four ChatGPT waits can block unrelated integrations.

- Remove QueueCapacity, MaxConcurrentExecutions and maxOutstandingExecutions from Agent configuration, AgentService.Start, AgentStore.TryCreateRootExecution/InsertExecution and related errors/architecture tests.
- Keep durable Queued/Running/CancelRequested/completed state, atomic SQLite claim, per-chat exclusion, execution IDs, restart recovery and durable command evidence. Do not drop the queue table/state or replace it with a second in-memory source of truth.
- Keep one background AgentWorker *dispatcher* that wakes, repeatedly claims all currently runnable queued roots and starts independent asynchronous execution tasks. Use existing atomic claim to prevent double-start; same chat stays serialized.
- Track in-flight tasks and cancellation explicitly so no fire-and-forget/unobserved failures, clean shutdown drains or cancels according to existing recovery semantics, and finishing work wakes the dispatcher to release same-chat successors.
- Do not add a new global semaphore, queue-size limit or worker pool as a disguised replacement. Unbounded concurrent starts are a deliberate interim tradeoff; revisit resource protection only when per-integration limits are designed.
- Integration-specific concurrency/rate limiting is OUT OF SCOPE for this plan. Do not partly implement it here.

**Acceptance:** More than four independent chats can become Running without waiting for a global worker slot; same-chat executions never overlap; cancellation/kill, shutdown/restart recovery, and durable results remain correct. Test work completion and exceptions under concurrent start/stop conditions.

## Implementation sequence and validation gates

1. **Characterize and protect the existing behavior.** Record relevant current tests and expected outputs; inspect existing standalone Agent, shared API, ChatGPT Account, guest and Grok paths. No speculative fallback or live-API assumptions.
2. **Shared browser/API ownership (#1, #2, #7).** Move diagnostics/session ownership and API registration, preserve inheritance and published endpoint, run provider and API tests.
3. **Direct JavaScript packaging (#3, #4).** Remove bundle/build.js, extract normal sibling JS resources and update tests and docs. Check Electron main+preload from an isolated deployed directory, not just source checkout.
4. **Guest simplification (#5).** Reuse pageOperations; test bounded completion, login-required, and no duplicate submission. Do not touch authenticated Sentinel semantics to satisfy this guest cleanup.
5. **Parser protocol repair (#6).** Base the escaped-tag handling on observed raw text, make presentation inert, keep valid protocol commands and opaque bodies, extend regressions.
6. **Dispatcher and limit removal (#8).** Remove both global limit settings and fixed worker pool; preserve atomic claim, per-chat serialization, cancellation and restart behavior.
7. **Final audit.** Remove obsolete code, settings, generated files, tests asserting deleted behavior and stale .agents incident/README references. Review diff for accidental changes.

Run targeted Node tests (ChatGPT account, guest, provider operations, diagnostics), .NET 10 build/publish, tests/agent-protocol-result.ps1, tests/agent-runtime-resilience.ps1, tests/agent-architecture.ps1 and relevant browser packaging tests. Use isolated build outputs if normal build files are locked; do not kill user applications. Include a harmless live authenticated multi-turn Agent test if available: prompt -> SH -> durable result -> follow-up -> DONE. Test guest live only if upstream permits it. Finish with git diff --check and a review of all changed files. Record which tests ran and which external behaviors remain unverified.

**Finish condition:** Implementation is committed and pushed to the existing review branch; offer the user its diff/PR for review; leave it unmerged. This planning document alone is not implementation or permission to merge.
