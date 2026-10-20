# Plan: agent-model-presets architecture review cleanup

**Date:** 2026-10-19
**Status:** T1–T7 implemented; coordinator review 2026-10-20: PASS WITH LIMITATIONS. Automated/published/runtime evidence passes, but the live authenticated root's final DONE/Completed state was not evidenced in T7's report. Await final root-state verification and explicit user merge approval.
**Target:** Existing agent-model-presets review branch; do not merge until the user reviews the completed diff and explicitly approves.
**Scope:** All eight findings from the 2026-10-19 code review, plus the follow-up on global workers/concurrency. This is not the separate orchestration proposal or the general execution-UX backlog.
**Guidance:** Read .agents/README.md and the repository-change-workflow and software-change skills before modifying code. For observed defects follow systematic-debugging. Reuse the existing review branch rather than splitting this review into speculative branches.

## Coordination and ownership (2026-10-19)

- **ChatGPT coordinator (this review):** #1 (keep existing relative Electron diagnostic import), #3 (preserve meaningful JS source boundaries), #7 (shared diagnostics API with local-access restrictions). T1–T5 implementation has been reviewed; T4 follow-up passed the local agent's regression tests.
- **MEŽS local implementation agent:** #2 (base-class account session/diagnostics ownership, preserving inheritance), #4 (deploy source JS files directly without a bundler), #5 (guest pageOperations rewrite and live guest characterization), #6 (safe, general escaped-tag/parser correction based on actual raw replies), #8 (remove global limits and fixed worker pool while preserving durable dispatcher and recovery).
- **Ownership rule:** Execute the MEŽS handoff after pulling the already-pushed coordinator changes. Work on the same agent-model-presets review branch; do not overwrite, revert, or parallel-edit the coordinator's files without reviewing them. Existing untracked/local user files are not cleanup targets.
- **Verification:** T1 HTTP diagnostics 22/22 PASS (synthetic remote IP); T2 account/Grok ownership tests PASS; T3 published CommonJS probe and Node 90/90 PASS; T4 Node 99/99, guest 14/14 and lifecycle 8/8 PASS; T5 parser/API, policy, architecture and cancellation recovery PASS, plus all 22 T4 Node regressions after the coordinator timeout correction. Local agent reported .NET Release build with 0 warnings/errors. Live anonymous guest sending remains unverified (login wall). No merge authorization is implied.

## Small-task handoff sequence

**Run one MEŽS agent task at a time.** Each agent run must read the repository skills, inspect the local tree, synchronize without destroying local work, implement *only* its assigned scope, run targeted tests, commit/push to `agent-model-presets`, report file/test/commit evidence, and stop. The coordinator reviews each report before the next task. Never merge.

1. **T1 — Shared diagnostics verification (#7): COMPLETE, reviewed.** Agent commit `42ae609`; targeted builds/publish, HTTP 22/22, diagnostics tests and relevant architecture checks reported PASS. Non-loopback-IP check used a synthetic peer address; full architecture JS-packaging failure deferred to T3.
2. **T2 — Browser diagnostics/session ownership (#2): COMPLETE, reviewed.** Agent commit `c985bba`; centralized base-owned account session/diagnostics/disposal, preserved hierarchy and guest separation. Targeted account/Grok ownership tests, diagnostics and provider checks reported PASS. Live Grok snapshot and browser opening worked; Grok message encountered provider ToS gate and ChatGPT Account lacked login. Two historical full-flow tests reportedly also fail on baseline; validate integrated provider flows in T7.
3. **T3 — Source JS module packaging (#3/#4): COMPLETE, reviewed.** Agent commit `453328c`; removed generated bundle/build script, embedded eight unmodified CommonJS modules, added direct extraction while preserving Grok/Gemini. Reported Node 90/90, .NET Release build, isolated publish and native Node require probe PASS. Live Electron conversation deferred to T7.
**T3 residual verification:** Native Node loading from isolated published resources passed; full live Electron conversation is deferred to T7. Concurrent extraction was not independently tested; inspect only if a concrete runtime issue appears.

4. **T4 — Guest send simplification (#5): COMPLETE, reviewed.** Agent commit `533c155`; guest moved to existing preload `pageOperations` IPC; one send attempt, bounded completion and reliable listener cleanup. Agent reported Node 99/99, guest 14/14, lifecycle 8/8, full Release build PASS. Live isolated guest loaded ChatGPT and observed a login wall before any send; anonymous success unverified. Coordinator follow-up `796d863`–`3010bb6` moved the 190s guest timeout to the guest integration through optional `page.invoke` argument, removing a provider-name check from generic Electron; T5 agent reported all 22 related Node regressions PASS.
5. **T5 — Parser presentation/protocol boundary (#6): COMPLETE, reviewed.** Agent commit `d4296ec`; removed escaped-DONE promotion, treated escaped examples and multiline display markup as inert, preserved raw markers and opaque block bodies, added parser/API regressions using isolated storage/port. Reported protocol, policy, architecture, cancellation-recovery and .NET Release build PASS; all 22 T4 Node regressions PASS. Historical escaped DONE now requires a later raw marker to complete; do not silently re-enable ambiguous commands.
6. **T6 — Agent dispatcher and global-limit removal (#8): COMPLETE, reviewed.** Agent commit `c4d81ba`; removed global capacity/settings, replaced fixed pool with single claim dispatcher plus tracked independent tasks. Reported isolated six concurrent Running roots in API, SQLite and Executor; cancellation, failure isolation, same-chat wake, graceful shutdown, forced and graceful restart without duplicate Executor shells PASS; Release build 0 warnings/errors. Coordinator reviewed code/tests without independently rerunning local tests.
7. **T7 — Integrated validation and documentation.** Full relevant .NET/Node/PowerShell tests, isolated deployment checks, harmless authenticated multi-turn/live Agent test, docs update, final diff review and push. No extra implementation unless correcting a demonstrated regression.

**Do not parallelize tasks that share `agent-model-presets` or run a later task on stale code.** T1–T6 have been reviewed; T7 must independently validate integrated behavior and report limitations. Approval of individual tasks does not authorize a merge.

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

## T7 final integrated validation (2026-10-20)

**Disposition:** Review-ready on the existing `agent-model-presets` branch; do not merge without explicit user approval. No live user application processes were terminated. T7 includes only a whitespace correction, a PowerShell 5.1 compatibility fix in the proxy regression, and this validation record.

### Verified locally

- Full Node regression suite: **99/99 pass** (`node --test tests\*.test.js`); syntax checks pass for 12 JavaScript files.
- `dotnet build Mezhs.sln -c Release --nologo -v quiet`: **pass**, zero errors. The final post-T7 build reported **zero warnings**; an earlier build reported one historical `CS0114` warning in `Mezhs.WhatsApp/WhatsAppApplication.Tests.cs` (the affected method is also present on `main`).
- TypeScript `tsc --noEmit -p`: **pass** for the ChatGPT browser, Agent Web, and Web ClientApp projects.
- Isolated Release `dotnet publish`: **pass** for core API, Agent API, and packaging probe. Published ChatGPT assembly extraction/native Node import: **pass** for eight CommonJS resources, repeat extraction, and missing/stale resource repair.
- Published ChatGPT `entry.js` loaded by the actual Electron `main.js` and `browser-preload.js` using a disposable profile: **pass**; diagnostics invocation completed successfully.
- Electron disposable-profile runtime checks: diagnostics, browser identity/OAuth state, hidden authorization, visible login flow, and provider operation regression: **pass**.
- Architecture/policy/integration regressions: **pass** for repository architecture, Agent architecture, provider lifetime, integration resources, diagnostics architecture, policy decoder, Grok architecture, and cancellation contract.
- Diagnostics API: **pass** across both API hosts, including rejection of synthetic non-loopback access. Parser/protocol, conversation restore, Agent store migration, cancellation/recovery, shared Agent Web UI, attachment staging, and mock end-to-end attachment tests: **pass**.
- Agent runtime resilience: **pass** with six concurrent runnable Agent roots and six overlapping shell commands, failure isolation, cancellation, queued cancellation, same-chat serialization, persisted restart/recovery, and no duplicate shell start. Graceful shutdown/recovery regression: **pass**.
- Isolated Agent API/Web smoke: **pass**, including loopback-only Agent/API exposure, browser CORS restriction, shared endpoints, policy behavior, and debug logs after generic API shutdown. Isolated Agent Web proxy interrupted-upstream regression: **pass**, including controlled 503 and recovery.
- Authenticated live ChatGPT Account Agent on `127.0.0.1:5060`: **verified** real multi-turn `SH` round trip. The harmless marker command `2737` returned exit code 0, its `/v1/executions/2737` record was `Completed` with parent `exec_8fc6f096c3aa465083f4f4a64c7cdf84`, exactly one such execution ID was found in the chat execution listing, and the marker appeared in the durable debug log. The current root remains active until the final `DONE` response.

### Known limitations / non-regressions

- Full `tests/account-login-flow.ps1` and `tests/grok-account-login-flow.ps1` each fail a prior-history reconstruction assertion; **both failures were reproduced independently on a clean `origin/main` source baseline**, not attributed to T1–T7. Their T2 ownership-only tests both pass. Keep history reconstruction as separate follow-up work.
- Live guest upstream completion was not tested, and no external paid ChatGPT/Grok login, model catalog refresh, or real file upload was forced during T7. The authenticated Agent smoke uses the already running user session; the published-module Electron smoke uses a disposable unauthenticated profile.
- Windows PowerShell 5.1 cannot compile newer syntax in the original proxy test; T7 adapted only the test harness to 5.1-compatible C# and explicitly loaded `System.Net.Http`. The checked-in script now passes directly.
- An earlier build emitted `CS0114`, but the final incremental Release build was warning-free. The historical warning is not claimed fixed by this task. This review does not claim unrelated defects are fixed or the branch has been merged.

### Release gate

Finish with `git diff --check` against `main` and the working tree, inspect the final T7 diff, commit/push `agent-model-presets`, verify remote SHA equality and a clean worktree. Branch remains unmerged.

## Coordinator review of T7 (2026-10-20)

- **T7 diff reviewed:** commit `613fb429` changes only this validation record, a whitespace correction, and the Windows PowerShell 5.1 proxy-test harness. No unrelated production behavior was altered by T7.
- **Result:** PASS WITH LIMITATIONS. Reported Node 99/99, .NET Release build, TypeScript, isolated publish/Electron, diagnostics, Agent concurrency/cancellation/recovery and live authenticated SH execution all pass. Baseline-reproduced account-history failures remain separately documented.
- **Outstanding live acceptance:** T7 records root `exec_8fc6f096c3aa465083f4f4a64c7cdf84` as still active after successfully recording child shell `2737`. Verify that this same root reaches `Completed` with a genuine raw DONE response and correct durable result. Do not resubmit the marker shell command or infer final completion from its successful child execution alone.
- **Merge comparison:** GitHub comparison of `main` to `agent-model-presets` at T7 review shows the branch **456 commits ahead and 1 commit behind**, with 99 changed files; these totals include broader branch work outside T1–T7. Recheck branch drift and final integration scope before any explicitly user-approved merge.
- No merge, deletion, or deployment authorized by this review.
