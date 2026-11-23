# ChatGPT Account protocol minimization

ID: `chatgpt-account-protocol-minimization`
Branch: `chatgpt-account-protocol-minimization`
Level: High-risk
Base and required merge destination: `integration-architecture-v2`
Base revision: `b8f25cff351aed34b3de2f7ef1483a01183a9049`

## What

**Primary objective (owner clarification, 2026-11-21):** Remove the majority of ChatGPT Account's provider-specific JavaScript and make browser integrations primarily **private-API mappings in C#** on standardized shared transport. Ideally, adding any browser integration requires zero JavaScript: it defines endpoint paths, request/response DTOs, per-provider semantics and explicitly named exceptional native hooks, while the reusable framework handles the browser session, HTTP transport, streams, cancellation and safe file transfer. Move account protocol interpretation/orchestration into C# where browser execution is unnecessary. Retain JavaScript only when it has a proven need for native browser-page execution (such as ChatGPT's Sentinel requirements). Reuse the existing `BrowserAccountIntegrationBase`, `BrowserAccountSession`, `IntegrationHttpClient` and `BrowserHttpExecutor`; no second client, session owner or scheduler.

**Branch and merge requirement (owner instruction):** This task MUST be developed, reviewed, tested and merged back into **`integration-architecture-v2`**, never directly into `main`. Open any PR against `integration-architecture-v2`; after independent review/testing and **explicit owner merge approval**, merge `chatgpt-account-protocol-minimization` into `integration-architecture-v2`. If the parent advances, reconcile that parent into the task branch and reverify before the final merge. Do not merge the task branch into `main`, change `main`, or publish a release as part of this task.

Scope: ChatGPT Account sending, projects/models, request preparation, streamed response/progress, final conversation retrieval, file transfer, and the narrowly necessary shared transport primitives. Existing ChatGPT Guest, Gemini Guest, Grok Account, Core/Agent/UI semantics, and public API contracts must remain functional.

Out of scope: changing provider behavior merely to reduce line count; dropping files, project support, citations, reasoning/effort metadata, or cancellation; replacing genuine browser-owned ChatGPT security requirements; speculative provider frameworks; extra login/session ownership; altering concurrency guarantees; unrelated refactors; version/changelog/release updates.

## Why

The v2 architecture's typed authenticated HTTP executor serves models and, after the initial Development slice, paginated projects. Message submission still follows a second, large legacy JavaScript protocol implementation. This is a poor template for adding browser integrations: the framework should own repeatable browser HTTP mechanics and provider code should mostly map private API contracts. **The owner clarified that the most important result is eliminating the majority of provider JavaScript and standardizing integrations, even when required C# DTO/protocol code means the total line count does not halve immediately.** Reducing total provider-specific code remains a valuable secondary measure, not a reason to leave a complete protocol in JavaScript.

Measured baseline on the parent at `b8f25cf` (physical lines, including blanks/comments; account attribution in the mixed `entry.js` is approximate):
- `ChatGptAccountIntegration`: 179 C# lines; `ChatGptTypedHttpEndpoints.cs`: 97 C# lines.
- Six `account-*.js` modules: 1,435 JavaScript lines.
- Account portion of `entry.js`: approximately 139 JavaScript lines.
- **Approximately 1,850 account-specific production lines**, excluding guest, tests, documentation and existing shared infrastructure. Recalculate a precise baseline and explain ambiguous ownership in the first Development step.

**Design objectives, in priority order:** (1) Remove **more than half** of baseline account-specific JavaScript (approximately 1,574 JS lines including 139 account-owned `entry.js` lines); aim to eliminate nearly all provider JS except independently justified native browser hooks. (2) Establish reusable browser-integration primitives so another provider can map private endpoints, streamed results and safe transfer without copying ChatGPT JavaScript; demonstrate this through concrete contracts/tests, not a hypothetical framework. (3) Minimize total provider-specific C# + JS code, with the prior **900-line / stretch 600–700 ambition retained as a secondary optimization**, not a hard completion gate if actual mappings/DTOs and provider-specific logic justify the remaining code. Count any shared helper used only by ChatGPT as provider-cost when reporting. Never weaken supported behavior or disguise one-provider logic as generic infrastructure. If a new generic mechanism has disproportionate complexity or violates browser ownership/security, stop for a fresh owner decision.

## Acceptance criteria

- [ ] **More than 50% of account-owned JavaScript lines are removed** compared with the reviewed baseline (aim near-zero). Every remaining provider JS line is justified by browser-native execution; mechanical moves/renames do not count. Provide reproducible before/after inventories for JS, C#, total account code and any one-provider-only shared helpers, plus a qualitative maintenance assessment.
- [ ] Model discovery, account authentication, project selection, new chats and continuations, conversation-unavailable restoration, per-turn model/thinking effort, progress reporting, final response/analysis metadata, citation handling, artifacts, uploads and downloads preserve existing behavior.
- [ ] ChatGPT Account's primary send/receive, preparation, state reconciliation and file APIs are expressed as typed private-API endpoints, DTOs and provider-specific interpretation/orchestration in C# on the reusable browser executor. No full send/receive protocol remains in provider JS; only narrowly justified native page hooks may remain. There is no parallel HTTP/streaming/client library maintained just for this account.
- [ ] Reusable transport owns HTTP validation, request serialization, **incremental bounded streaming/framing with ordered protocol events that reach C# while the request is active**, cancellation, response limits, and safe signed/binary file-transfer mechanics. Protocol events must not be coalesced into the latest user-facing progress snapshot: preserve order without silent event loss or duplication, and fail explicitly if bounded buffering/backpressure cannot be maintained. Handle split UTF-8 and SSE/NDJSON frames, completion, errors and cancellation. User-facing progress remains a separate best-effort status signal. Provider C# retains ChatGPT's request/conversation/citation/file-processing interpretation and logical security preparation; Electron/browser owns native web-page requirements and actual credentials. Shared primitives are demonstrably usable for another browser integration without copying provider JS. No redundant `HttpIntegrationBase`, second session owner, scheduler, model-selection state or generic provider-state framework.
- [ ] Electron remains the owner of authenticated session/cookies and native Sentinel/browser-generated requirements. Never synthesize, cache across the wrong session, expose, or transfer account credentials/challenge tokens to arbitrary destinations. Signed file URLs and redirects remain validated; no credential leakage to other origins. Cross-origin signed transfers must omit account credentials and authorization headers, reject unsafe redirects and apply bounded file sizes/streaming I/O rather than unbounded whole-file buffering.
- [ ] No prompt replay after an uncertain mutation; correct chat/message ancestry and final reply selection; stop/cancellation release local permits and do not imply provider-side cancellation. Shared authenticated connection can serve independent chats under existing limits. Same-ChatId ordering remains centrally owned.
- [ ] ChatGPT Guest and the other providers still work; no unrelated provider migration is required. Public Core, Agent and UI contracts remain compatible unless the owner explicitly approves a necessary change.
- [ ] Relevant automated tests, architecture checks and real Chromium verification pass at the final revision; live-account checks use an isolated profile and report any tests that could not be performed. Record actual evidence and limitations.
- [ ] Independent Plan Review PASS precedes development; independent Code Review PASS and independent Testing PASS follow development. Material corrections or blocking failures require a fresh independent verification of the affected role.
- [ ] Final work is committed/pushed only on `chatgpt-account-protocol-minimization`, reviewed against `integration-architecture-v2`, and **merged into `integration-architecture-v2` only after the owner's explicit approval**. Do not merge into `main`.

## How

1. **Measure first, then simplify by ownership.** Inventory every JS operation in `account-messages.js`, `account-results.js`, `account-stream.js`, `account-transport.js`, `account-files.js`, `account-models.js`, and the account portion of `entry.js`. Map each operation to existing typed HTTP, browser transport, session or provider-owned behavior. Record line and dependency counts before implementing changes.
2. **Extend the existing browser HTTP bridge rather than writing another provider JS client.** Move the generic `typedHttp` operation out of ChatGPT's `entry.js` into Electron/browser integration infrastructure with explicit provider-specific origin/path policies. Add only required operations for authenticated JSON, bounded incremental SSE/NDJSON, and controlled signed/binary uploads/downloads. Preserve stable session/authentication owners and cancellation semantics, and avoid unrestricted header/origin inputs. Keep provider-specific native requirements generation as a small named browser hook where actual page-runtime execution is necessary; do not synthesize Sentinel tokens in C# or cache them across turns. Explicitly design how request-local security preparation is associated with the one send operation.
3. **Migrate provider protocol by coherent vertical slices.** Models and projects are already typed. Next: C# request/prepare DTOs and bounded one-time POST with request-local native security headers; incremental stream events and progress interpretation in C#; conversation-state polling, ancestry, citations and metadata in C#; uploads/downloads through shared safe transfer with provider C# processing. Verify each slice against existing tests; remove its superseded JS code rather than ship parallel stacks. Keep existing JS implementation until the replacement is verified. Do not rely on a buffered 16 MiB response as the only source of model activity.
4. **Reuse only real cross-provider mechanics.** Shared HTTP execution, bounded streaming framing, safe file transfers, cancellation and polling cadence can be shared when actually transport-agnostic. ChatGPT model catalog shape, message tree, citation references, Sentinel proof flow, signed upload protocol and other provider-specific mappings must not be placed in shared libraries merely to meet the line target. Count one-provider-only helpers in the account total.
5. **Respect existing guarantees.** Account-scoped auth and per-chat context remain in existing browser bases. Do not introduce connection-global model-selection state or hidden retries; durable message IDs are created once by Core. The baseline still performs ChatGPT's account-level `setModelPreference` PATCH before sending. Treat that provider-side mutation as existing behavior, not redundant local state: preserve it unless isolated live tests establish that removing it retains model/effort selection, including two overlapping conversations with different selections. If that cannot be verified, retain the mutation and report the unverified concurrent-model limitation; do not add a global model lock or assert independent served-model selection. Do not loosen security restrictions, replay semantics or context isolation.
6. **Complexity stop:** The owner authorizes a *bounded extension of the existing* HTTP/streaming/browser bridge to remove majority-JS duplication. Stop only if it would require another credential/session owner, a second generic client, unbounded response buffering or polling, weakened streaming/attachment/security guarantees, or disproportionate shared-only-for-ChatGPT code; present concrete alternatives and cost. Missing the former 900-line secondary target alone is not a blocker when the new majority-JS and reusable-integration acceptance criteria are met.

## Steps

1. **Plan Review** — fresh independent agent re-examines the revised objective and proposed reusable browser HTTP/streaming/transfer design, ownership, quantitative accounting, merge destination and security/verification completeness. Correct the plan; repeat independent review after material corrections until PASS or owner decision; the earlier PASS applies only to the superseded goal.
   - Done when fresh Plan Review PASS for the revised architecture is recorded on this task branch.
2. **Development** — following fresh review of the owner's clarified design, implement reusable browser HTTP/streaming/transfer primitives and migrate the majority of ChatGPT Account protocol out of JavaScript into typed C# endpoint mappings/interpretation. Remove superseded JS, update tests and measure before/after JS and total code.
   - Done when **more than 50% account JS is removed**, the standardized shared browser integration mechanism works and legacy duplicate protocol paths are gone, all developer checks pass, and the task branch and plan are committed/pushed.
3. **Code Review** — fresh independent reviewer examines actual code ownership, total code cost, security, cancellation/replay, concurrency, protocol edge cases and test coverage; correct findings; repeat independent review after material corrections until PASS.
   - Done when an independent Code Review PASS is recorded against the current revision.
4. **Testing** — fresh independent tester validates automated suites and real Chromium/provider behavior where safely available, including failure cases; correct failures and repeat independent review/testing as workflow requires.
   - Done when independent Testing PASS, revision and limitations are documented.
5. **Deploy / merge to parent** — after explicit owner approval, update from the latest `integration-architecture-v2`, resolve only justified conflicts, rerun relevant verification, then merge **this task branch into `integration-architecture-v2`** using a PR targeting that branch or owner-approved equivalent. Verify the resulting remote parent SHA and task ancestry. Never target or modify `main`.
   - Done when remote `integration-architecture-v2` contains the approved implementation and the parent is verified; do not assume approval from test PASS.

Only one active agent/chat may work on this task at a time. Use new independent agents according to `.agents/workflow.md` and `.agents/mezhs.md`. Do not start Development before Plan Review PASS.

## Verification

- Measure account-specific JS removal against the approximately 1,574-line JS baseline as the primary result; separately measure account-owned C#, total provider code, all genuinely shared infrastructure changes, and any shared helper with just one provider caller. List removed modules and replaced browser operations; show that a second browser integration can use the typed transport without ChatGPT-specific JavaScript. Include diff and maintainability assessment, not just LOC.
- Run Release solution build, frontend typecheck/Vite build, `Mezhs.Integration.Contracts.Probe`, ChatGPT packaging/architecture checks and relevant `node --test` suites: `tests/chatgpt-account-api.test.js`, `tests/browser-provider-operations.test.js`, `tests/chatgpt-browser-package.test.js`, `tests/typed-http-feasibility.test.js`, `tests/integration-model-mismatch.test.cjs`, `tests/chatgpt-conversation-unavailable.test.js`, `tests/rich-model-ui-browser.test.cjs`, and relevant Electron context/cancellation tests.
- Cover authenticated model selection, projects, first turn/continuation, concurrent chats, absent/stale final responses, 401/429/stream interruption, abort before/after submission, uncertain POST no-replay, upload process failures, signed storage destination safety, artifacts, citations, thinking-effort metadata and state restoration. In an isolated authorized live profile, compare overlapping conversations with distinct model/effort selections before changing or removing the provider-level `setModelPreference` PATCH; without such evidence, preserve the existing behavior and explicitly mark the concurrent served-model guarantee unverified.
- Exercise bursty ordered stream events faster than Electron polling, split UTF-8/SSE/NDJSON frames, malformed/oversized frames, bounded-queue exhaustion, interrupted streams, cancellation and late events. Confirm the C# consumer receives protocol events while the HTTP request is still active, with no silent loss or duplication; do not use the existing last-progress snapshot as the event queue.
- Exercise large-file bounded transfer, redirects (including cross-origin), signed destination/header validation, partial failure/cleanup, and verify that cookies, bearer tokens, Sentinel values and signed URLs cannot leak through transfer requests, progress, errors or logs.
- Build the canonical .NET rich-UI test project explicitly before Node rich-UI tests to avoid stale fixture DLLs where affected.
- Real provider testing, if authorized and available, must use an isolated MEZS instance/profile and separate storage as `.agents/mezhs.md` requires; never reuse or copy the owner's active profile. State exactly what was/was not tested.
- Before merge, compare latest `integration-architecture-v2` and rerun impacted checks after reconciliation. Confirm PR base/destination is `integration-architecture-v2`, not `main`; record approval and final parent commit.

## Changes

### 2026-11-23 - Owner-authorized VSS clone testing guidance

**Owner direction:** Document the isolated authenticated-testing approach demonstrated on 2026-11-23 and continue the established workflow. The current branch revision before this change is 0a3abdd109033a67e71ea3ec89a34d0f0730d71e.

**Change:** .agents/testing.md describes a narrowly scoped, owner-authorized disposable VSS snapshot clone of an active Chromium profile when a separate inactive authenticated test profile is unavailable and the owner application must remain running. It documents process/profile identification, separate test API/Electron/storage, snapshot copying of locked Cookies and companion files, clone verification, linking only to the clone, real-provider checks, non-replay after uncertain submission, and safe cleanup. .agents/mezhs.md links to this exception. Existing inactive-profile guidance remains the default.

**Security boundary:** The clone carries credentials and must never be logged, committed, attached, reused indefinitely or connected to the owner active storage. Do not stop owner processes, link to the live profile or delete recursively through the junction. Owner authorization is required each time; this documentation is not blanket permission to copy live profiles. Snapshot consistency and browser authorization must be independently verified.

**Verification and outcome:** Documentation diff reviewed and git diff --check passed. No production implementation changed. The previous live tests passed new and continued messages, overlapping requests, project resolution and one file-reading turn, but the earlier uncertain stream failure and signed output-download verification are unresolved. Step 4 remains BLOCKED, not PASS. Continue Testing with independent evidence or a specific owner decision. Do not merge or deploy; eventual owner-approved merge target is integration-architecture-v2, never main. Subsequent agents must fetch this task branch, use its workflow and role instructions, and pass that requirement onward.

### 2026-11-23 — Step 4 authenticated live Chromium follow-up (BLOCKED)

**Revision/environment:** Fetched `origin/chatgpt-account-protocol-minimization`; live-tested `e32da493957c38a78cf1c0091b6a38cf8ea7b126`, with no production changes from previously automated-tested `035d95c6` (183/183 Node plus typed .NET, solution and browser checks PASS). Isolated task-branch API on `127.0.0.1:5374` (PID 18292), distinct Electron/process tree/storage. Owner MEŽS API PID 11816 and its browser processes stayed running. On owner direction to avoid interrupting active agents, test authorization was obtained through a disposable VSS snapshot clone of the active Chromium profile; this is an explicit *exception* to `.agents/testing.md` default prohibition on copying live credentials/cookies, not an inactive test profile. No credential material or signed URL was committed or included in this report. Destroy isolated credential-bearing clone after test.

**Live PASS:** Seven account models discovered with HTTP 200. Real new Instant turn `6bb642ea-a1e1-4d2a-8590-c03f5a152058` and its continuation `b13006ad-9e70-47fd-bae6-82ec52493a17` completed with exact expected markers and correct ancestry. Independent extended-thinking turn `5abe612f-7f77-4224-871c-aea3e8812b93` and Instant turn `f6de7572-26cb-468f-b0a0-26788ad1ea41` overlapped by about 20.8 s, each completed with distinct expected marker, correct parent and corresponding reply-model metadata; this proves concurrent processing and reported selections, **not** independent provider-side served-model attribution. The existing account-level model-preference PATCH remains. Configured `Mezhs` project was resolved on new chats and response project check accepted it. A UTF-8 input attachment was accepted (local HTTP 201) and real independent attached-file turn `88656c22-ebe1-4cff-a2b0-adc204a0d744` completed, reproducing text found only inside the file. Browser progress, thinking, reasoning and tool metadata were observed. This exercises live signed input upload and file processing.

**Unresolved observations:** First attached-file turn `8b937c1f-67f9-4c8c-9acb-f59a205b78f4` failed at last reported activity `Preparing browser HTTP request` with `ChatGPT stream failed after submission; delivery may have succeeded, so the turn must not be resent.` It was **not replayed**. A different later attachment turn passed, but the original exception, provider delivery and root cause remain unknown. A real tool-generated output-file turn `316ed9fc-d8b5-48f5-b67f-dd3ba0a1f442` completed with a Markdown `sandbox:/` link but returned zero structured MEŽS files. Both current C# and pre-migration JavaScript discover downloads only through structured `attachments` or asset pointers, not plain sandbox links: **no regression established, but real signed output-download path remains unverified**. No speculative code changes were made.

**Outcome: BLOCKED** (not PASS): unexplained live stream failure and incomplete real signed-output-download verification. Existing automated PASS applies to unchanged production source. No production correction or independent re-test trigger from this documentation-only update. Obtain further evidence or explicit owner acceptance of remaining testing limits before Step 4 PASS. Do not deploy or merge. Eventual merge destination is **`integration-architecture-v2` only**, after explicit owner approval; `main` is release-only. Every subsequent agent must first fetch latest remote `chatgpt-account-protocol-minimization`, use that branch's `.agents/workflow.md` and role instructions, not `main` or parent, and pass this requirement to all later agents.


### 2026-11-22 - Independent Step 4 re-verification at 035d95c6

**Revision:** Fetched task branch at 035d95c657f4e6ddeaa7533e86302e9076a62145. Compared with the previously verified 5e9eaa63 revision, only .agents/testing.md and this plan changed; no production implementation changed. Testing followed the task-branch workflow and Testing role.

**Automated results:** Release solution build PASS (zero errors, one existing unrelated WhatsApp CS0114 warning); Agent Web TypeScript/Vite production build PASS; 75/75 focused account/HTTP/streaming/transfer/context Node tests PASS; 44/44 guest/Grok/diagnostics/identity tests PASS; 64/64 rich-UI tests PASS after explicit canonical .NET rich-UI build, including real Chromium. Total 183/183 Node tests PASS. Typed C# ChatGPT protocol, account login/model/project/recovery harness, Integration Contracts Probe, architecture, provider lifetime, eight-integration cancellation contract, embedded resources, ChatGPT packaging and concurrent extraction all PASS. Electron provider operations 6/6 PASS; disposable-profile browser identity, diagnostics, interactive login visibility and hidden authorization checks PASS. Two initial harness invocations lacked a required probe directory and RUNNER_TEMP respectively; corrected reruns passed.

**Independent physical-line inventory:** Baseline b8f25cff: six account JS modules total 1435 lines versus 405 on this revision, with five deleted modules accounting for 1159 removed lines; mixed entry.js totals 256 versus 187 lines. Using the previously documented approximate account attribution in entry.js of 139 versus 65 lines gives account JS approximately 1574 versus 470, or a 70.1 percent reduction. Five account C# files total 312 baseline versus 1271 current; approximate account-specific C# plus JS totals 1886 versus 1741 physical lines, a 7.7 percent reduction. Shared infrastructure is excluded, and this is not a claim of an equivalent shared-code reduction.

**Live limitation:** No dedicated inactive, authorized ChatGPT Account test profile was established. The saved local chatgpt-auth.json export is from 2026-07-31 and its recorded authentication expirations precede the testing date. The active owner Electron/profile was not reused. Thus no branch-revision authenticated provider prompt, live Sentinel compatibility, actual project/continuation, overlapping distinct served model/effort comparison, or live signed file transfer was verified. Simulated tests and browser smoke tests cannot prove these outcomes. No production defect was confirmed by the checks performed.

**Outcome: BLOCKED.** Automated verification passed, but required authenticated live testing remains unavailable and no owner acceptance of that limitation is established. Do not enter Deploy or merge. Resume with separately authenticated isolated test browser or owner decision. Every subsequent agent must first fetch latest chatgpt-account-protocol-minimization and use its .agents/workflow.md and role instructions, not main or integration-architecture-v2, and pass this exact requirement onward. The authorized merge destination remains integration-architecture-v2 after explicit owner approval.


### 2026-11-22 - Step 4 live-session and inactive-profile verification

**Observed active authorization:** The currently running owner MEZS Agent API on `127.0.0.1:5060` successfully returned seven ChatGPT Account models for connection `chatgpt-sub`. The current Agent execution uses policy `high` and that same connection. This establishes working authorization for the running owner implementation; the old `chatgpt-auth.json` export expiry is not proof that the active session is unusable.

**Revision boundary:** The active main checkout was `cce2d823`; the task branch was `2022b60c`. Relevant ChatGPT account protocol, browser HTTP/transfer, and Electron transport sources differ. The running API uses its own integration registry and Electron session, with no established supported mechanism to attach the new task-branch implementation to that active transport. Active-session success cannot be presented as a test of the revised task-branch protocol.

**Isolated attempts:** A disposable worktree at `2022b60c`, separate installed Electron, API `127.0.0.1:5370`, separate storage and copied older profiles were used. The task-branch API compiled with zero warnings/errors and passed its health check. Copies of owner-unconfigured `chatgpt-account` (295 files) and `chatgpt-free` (785 files) profiles each returned HTTP 401 for `/v1/connections/chatgpt-inactive/models`; no live prompt was sent. An initial empty profile-copy operation and a malformed test YAML config were corrected before these authorization checks, so neither setup failure is evidence of a provider defect. The original owner profiles were not written by these tests.

**Cleanup and outcome:** Both isolated Electron sets, the isolated API, copied temporary profile directories, and the disposable Git worktree were removed. Six owner Electron processes remained running. No authenticated account prompt, live signed transfer, project/continuation or distinct served model/effort overlap was verified on the task-branch code. **Step 4 remains BLOCKED**, not PASS. Further verification needs a separate authorized test browser session or an owner-accepted limitation; do not deploy or merge.

### 2026-11-22 - Step 4 default credential source identified

**Discovery:** The main checkout contains a git-ignored `chatgpt-auth.json` saved ChatGPT account session export. A git-ignored shortcut to the file was created under the task worktree `profiles/` without copying or publishing its contents. The prior shortcut to the active owner browser profile remains reference-only. The main checkout credential source was found locally rather than supplied by the owner.

**Expiry and evidence:** The export was made on 2026-07-31; its recorded access token expired on 2026-08-04 and browser session on 2026-10-29, both before the 2026-11-22 test. No later account export or dedicated inactive ChatGPT test profile was found in the inspected local MEZS locations. Expiry metadata does not prove all individual cookies are unusable, but authenticated operation has not been established. No test credentials or cookie values were exposed.

**Workflow correction and outcome:** Testing guidance now directs agents to locate and inspect the git-ignored local default session export before requesting credential paths. Automated checks remain PASS, but real authenticated prompts, overlapping served-model/effort behavior and signed file transfers remain unverified. Step 4 therefore remains **BLOCKED**, not PASS; no Deploy/merge approval follows. The owner browser was not reused.

### 2026-11-22 - Step 4 isolated authenticated-test attempt

**Environment:** Disposable worktree at `4ee98358`, separately installed Electron, explicit config, API `127.0.0.1:5368`, dedicated storage and Chromium profile under the Windows temporary directory. The isolated API built with zero warnings/errors, returned healthy, and advertised the ChatGPT Account login connection. The owner application and its profile were not modified or reused.

**Observed live behavior:** Models returned HTTP 401 before sign-in. The interactive login endpoint returned HTTP 500; API logs identify `BrowserAuthorizationRequiredException: Browser login was closed before authorization completed`. Retrying produced the same HTTP 500. The tests establish that the browser-side login flow was dismissed, but not why; they do not establish a ChatGPT protocol defect. No authenticated provider message was sent.

**Credential handling:** The default owner ChatGPT account profile exists at `D:/Projects/Mezhs/data/connections/chatgpt-sub/profile`, but owner Electron processes were active. Only a git-ignored `.lnk` reference in the task worktree was made, not a junction and not an authenticated test profile. The default credential source was requested from the owner. The isolated test API and Electron processes were stopped; isolated test files remain for safe resumption.

**Outcome:** Step 4 remains **BLOCKED** for authenticated provider verification, pending a separate default login/credential source or an inactive dedicated test profile. Automated PASS evidence in the prior entry remains unchanged. The Testing workflow now explicitly describes credential-source inquiry, local shortcut hygiene, and isolated sign-in. No merge or Deploy authorization is implied.

### 2026-11-22 - Independent Step 4 Testing BLOCKED at `5e9eaa63`

**Revision and environment:** Fetched the latest remote task branch and fast-forwarded its existing worktree. Tested `5e9eaa63aef132514dd7e9679bb253c38107a853` following this branch workflow, Testing role and task plan. Used separate temporary browser profiles for Electron tests; did not access the active owner profile. No implementation corrections were made.

**Automated PASS:** Release solution restore/build (zero errors, one unrelated existing WhatsApp CS0114 warning), Agent Web TypeScript/Vite build, 75 of 75 focused Node browser/account/streaming/transfer/context tests, 44 of 44 additional Guest/Grok/diagnostic/identity tests, 45 of 45 rich-UI tests after explicit canonical .NET rich-UI build (including real Chromium), typed C# ChatGPT protocol flow, account login/model/project/recovery harness, cancellation contract for eight integrations, Integration Contracts Probe, architecture, Agent/provider lifetime and resources. Electron provider operation tests passed 6 of 6. Real disposable-profile Electron browser identity/OAuth session and diagnostics passed. ChatGPT packaging probe passed three immutable embedded modules, corruption rejection and 96 concurrent initializations. Broad Node suite completed with exit code zero with the previously documented unrelated Chromium group deletion test excluded. An initial supplemental Node command incorrectly included a PowerShell script; the corrected 44-test JavaScript rerun passed.

**Independent source inventory:** Against baseline `b8f25cff351aed34b3de2f7ef1483a01183a9049`, physical account-module JavaScript lines were 1435 baseline versus 405 current; five deleted modules account for 1159 removed lines. Mixed `entry.js` is 256 baseline versus 187 current, with approximate account-owned allocation of 139 versus 65 lines. Total account-owned JavaScript therefore approximately 1574 versus 470, a 70.1 percent reduction, exceeding the required majority removal. Five account-related C# files total 312 baseline versus 1271 current. Approximate combined provider C# and JavaScript is 1886 versus 1741 physical lines, a 7.7 percent reduction, excluding the additional shared transport and unchanged Guest code. Earlier plan counts of 315 retained account-transport lines and 75.9 percent JS removal are stale for this revision.

**Missing live verification:** No dedicated inactive authenticated ChatGPT Account test profile was available in the inspected test-profile location; active Electron processes belonged to the owner application and were not reused. No authenticated ChatGPT Account message submission, live Sentinel/provider protocol compatibility, actual project/continuation, overlapping distinct model/thinking-effort response comparison, live provider signed file transfer, or live provider error scenario was tested. Mocked protocol tests and disposable real Chromium tests do not establish these outcomes. The provider-level model preference PATCH was preserved; simultaneous distinct served models remain unverified. The previously documented unrelated Chromium group-focus test was excluded, not independently resolved. No product defect was confirmed by the checks performed.

**Outcome: BLOCKED.** The task-branch `.agents/testing.md` disallows PASS when required live verification is unavailable without explicit owner acceptance of the limitation. No such acceptance is established. Resume Step 4 with a separately authenticated inactive test profile and isolated MEZS API/storage/browser, or seek explicit owner acceptance of this testing limitation. Do not enter Deploy, merge, change `main`, or claim owner approval. Any subsequent agent must first fetch latest `chatgpt-account-protocol-minimization`, follow that branch `.agents/workflow.md` and role instructions rather than `main` or `integration-architecture-v2`, and pass this exact branch-scoped instruction onward. Merge destination remains `integration-architecture-v2` only after explicit owner approval.

**Evidence:** MEZS test executions 9593 through 9632; independent Step 4 Tester.

### 2026-11-22 — Independent Step 3 Code Review PASS at `e343804`

**Reviewed:** Task branch revision `e343804a870e01297bc1eb33fab4a1aca6daf85d` against the authorized `integration-architecture-v2` baseline and the task's acceptance criteria. Inspected the C# account protocol and conversation state, native ChatGPT security hooks, reusable typed/browser HTTP framing and ordered events, bounded signed transfers, authentication, cancellation, context isolation, tests and prior review corrections. Remote branch matched the reviewed revision; worktree and Git diff checks were clean.

**Findings:** No unresolved blocking defect was confirmed. Earlier material transport/security and regression corrections were rechecked through targeted coverage. The documented majority-removal of account-owned JavaScript remains achieved (approximately 75.9%, with mixed-file ownership explicitly approximate); no parallel legacy account send/receive stack was restored. No production correction was made during this iteration.

**Verification:** 67/67 focused Node browser/account/streaming/transfer/context/ordered-event tests PASS; typed C# ChatGPT account-protocol flow PASS (metadata, citations, restoration, no-replay, stream interruption, authentication, files and SSE processing); architecture PASS; account login/model discovery/recovery PASS; Agent/provider lifetime PASS; cancellation contract PASS for eight integration registrations; ChatGPT/Grok/Gemini resource packaging PASS; Integration Contracts Probe PASS (typed HTTP, adapters, SSE/NDJSON, cancellation, no-replay, model validation and catalog). NuGet restore succeeded in the fresh isolated worktree, after which full `dotnet build Mezhs.sln -c Release --no-restore -v quiet` PASS with zero errors and the existing unrelated WhatsApp CS0114 warning. An initial build and standalone contracts probe failed only for missing fresh-worktree NuGet assets; both succeeded after appropriate restore. `git diff --check` PASS. Branch HEAD was identical to remote at verification.

**Limitations:** Isolated live authenticated ChatGPT/Chromium, real provider signed uploads/downloads and overlapping conversations with distinct served models/thinking efforts have not been demonstrated by this review; those remain Step 4 Testing obligations/explicit limitations. The documented unrelated unfiltered Chromium group-focus issue was not reverified here. The parent `integration-architecture-v2` may need reconciliation before Deploy, with subsequent re-verification. PASS does not authorize a merge or release.

**Outcome: PASS.** Step 3 independent Code Review is complete; hand off to a fresh independent Step 4 Testing agent. Every handoff MUST instruct the next agent to fetch latest `chatgpt-account-protocol-minimization` and follow its `.agents/workflow.md` and role instructions, rather than `main` or `integration-architecture-v2`, and to pass the same instruction onward. Merge destination remains `integration-architecture-v2` only, subject to explicit owner approval.

**Authority:** Independent Step 3 Code Review and verified MEŽS command results for executions 9504–9517.


### 2026-11-22 — Independent Step 3 Code Review: bounded page-native Sentinel preparation (FAIL)

**Reviewed:** Task branch at `80e4ad078138f5124e452f539ab38d9021a3f34c`, compared with approved `integration-architecture-v2` baseline `b8f25cff351aed34b3de2f7ef1483a01183a9049`. Inspected the injected Sentinel page hook, browser HTTP/transfer boundaries, account C# protocol and regression coverage. Remote parent later advanced to `52d1c88`; no parent reconciliation occurred.

**Finding (medium; confirmed code path):** `nativeChatRequirementsHeaders` consumed a successful `/backend-api/sentinel/chat-requirements/prepare` response through unbounded page-side `response.json()`, outside the shared bounded HTTP reader and the previously corrected native auth/conduit JSON reader. The code permits arbitrary successful response buffering; no live provider outage or memory exhaustion is claimed.

**Correction:** Replaced unbounded page-side JSON parsing with a 1 MiB incremental UTF-8 reader, early cancellation of unread bodies on overflow or parse/decoding failure, and explicit rejection when no incremental reader exists. Added redirect rejection and cancellation of rejected HTTP response bodies. Extended the existing VM-backed Rspack signature tests to use actual streaming `Response` objects, with a regression checking early cancellation and no preparation replay.

**Verification (MEŽS host results):** 19/19 focused Node tests PASS; 73/73 expanded browser HTTP, signed-transfer, account, native Sentinel, conversation recovery and Electron operation/context tests PASS; `tests/chatgpt-account-protocol-flow.ps1` PASS; `tests/architecture.ps1` PASS; `Mezhs.Integration.Contracts.Probe` PASS; `dotnet build Mezhs.sln -c Release --no-restore -v quiet` PASS (0 errors, one previously documented WhatsApp CS0114 warning); `git diff --check` PASS. No isolated authenticated live ChatGPT/Chromium session, real provider signed transfer, or overlapping distinct-model/effort served selection was tested during this review.

**Outcome: FAIL** under `.agents/code-review.md` because production code and regression tests were corrected. Hand off to a fresh independent Step 3 Code Reviewer before Step 4 Testing. Do not merge without explicit owner approval; authorized merge destination remains `integration-architecture-v2`, never `main`.

**Authority:** Independent High-risk Step 3 Code Reviewer.


### 2026-11-21 — Independent Step 3 Code Review: bounded native JSON success responses (FAIL)

**Reviewed:** Task branch at `bef2b5772ac1275dce9636545543d372928fd801`, compared with the approved `integration-architecture-v2` baseline `b8f25cff351aed34b3de2f7ef1483a01183a9049`. Confirmed earlier transport corrections and inspected the native ChatGPT account authentication, Sentinel/conduit preparation, incremental streaming, ordered operation events, signed transfers, C# protocol, and regression tests.

**Finding (medium; confirmed code path):** `account-transport.js` used unbounded `response.json()` for successful `/api/auth/session` and unbounded `response.text()` for successful `/backend-api/f/conversation/prepare`. These requests run inside a provider-native hook, outside the shared 16 MiB typed HTTP reader, so a successful oversized body could be buffered without a byte bound. The finding establishes an unbounded success-response path, not an observed live provider outage or memory-exhaustion incident.

**Correction:** Added an incremental 1 MiB UTF-8 JSON reader that cancels unfinished native response streams, rejecting oversized/nonincremental responses before the one-time conversation POST. Added a Node regression for oversized auth and prepare success responses, requiring upstream cancellation and zero conversation submissions. Committed on the task branch as `3101028` and `6e17a78`; corrected revision `6e17a78ddecc921f7d00e320b40070a8d483a8b3`.

**Verification (MEŽS host command results):** 25/25 targeted Node HTTP/transfer/account tests PASS; 65/65 expanded account/browser/Electron/context/model-mismatch Node tests PASS; `tests/chatgpt-account-protocol-flow.ps1` PASS (metadata, citations, restoration, no replay, interrupted streams, auth, files, SSE integrity); `tests/architecture.ps1` PASS; `Mezhs.Integration.Contracts.Probe` PASS; `dotnet build Mezhs.sln -c Release --no-restore -v quiet` PASS (0 errors, one existing WhatsApp CS0114 warning); `git diff --check` PASS and task worktree clean. React SSR `useLayoutEffect` warnings were non-failing. No isolated authenticated live ChatGPT/Chromium session, actual signed provider transfer or overlapping served-model/effort check was executed.

**Outcome: FAIL** under `.agents/code-review.md` because code and regression tests were corrected. A fresh independent Step 3 Code Reviewer must review the new revision before Step 4 independent Testing. No owner merge approval or merge is implied. The authorized future merge destination is `integration-architecture-v2`, never `main`.

**Authority:** Independent High-risk Step 3 Code Reviewer.


### 2026-11-21 — Independent Step 3 Code Review: bounded error reads and headers-only adapter (FAIL)

**Reviewed:** feature commit `80e06d7d2f8225724378f228fa4ed0b336ca24de` against the required `integration-architecture-v2` baseline `b8f25cff351aed34b3de2f7ef1483a01183a9049`. The task worktree was initially clean. The remote parent has since advanced to `52d1c88`, to be reconciled only at the authorized integration stage.

**Finding 1 (medium, reproduced):** The ChatGPT authenticated `apiFetch` error path used `response.json()` without an incrementally enforced byte limit. An isolated 404 response probe consumed all 201 streamed chunks without upstream cancellation, notwithstanding the typed browser HTTP 16 MiB response bound. The probe establishes the unbounded error-read path, not a real provider outage or memory-exhaustion incident. **Correction:** Use a 64 KiB limited, incremental error parser retaining only an allowlisted provider code, cancel unread error data on overflow or decode failure, and preserve HTTP status. Added a Node regression that requires early cancellation.

**Finding 2 (low, code path):** `IntegrationHttpClient.ForHttpClient` ignored the newly introduced `HeadersOnly` request property and always consumed the response stream. That conflicted with the shared typed HTTP contract and could reject large otherwise-accessible responses. The actual ChatGPT continuation preflight already runs through the separate Electron adapter; this finding is limited to consumers of the plain `HttpClient` adapter. **Correction:** Return status/content type without reading the response body for headers-only GET. Added an Integration Contracts probe for a response larger than 16 MiB.

**Commits:** `3976f63` error-read limit; `504e9f3` headers-only fix; `4ea8dc7` Node regression; `91c338d` contracts regression. Corrected implementation reviewed in the task worktree at `91c338de2e65d30d4347b80f6951ef963aa23cda`.

**Verified:** 39/39 targeted Node tests, then 62/62 expanded account/browser transport/Electron Node tests; `tests/chatgpt-account-protocol-flow.ps1` PASS (metadata, citations, restoration, replay, interrupted stream, authorization, files and processing); `tests/architecture.ps1` PASS; `dotnet run --project tests/Mezhs.Integration.Contracts.Probe -c Release` PASS; `dotnet build Mezhs.sln -c Release --no-restore -v quiet` PASS with one existing WhatsApp CS0114 warning; `git diff --check` PASS and clean feature worktree. No authenticated isolated live ChatGPT/Chromium run, overlapping distinct-model/effort provider test, or known unrelated group-focus test was performed in this review.

**Outcome: FAIL** under `.agents/code-review.md` because code and tests were corrected. A new independent Step 3 Code Reviewer must inspect the corrected task branch without being given this review's conclusions; proceed to independent Step 4 Testing only after its PASS. No merge, release, or owner approval implied. Required merge destination remains `integration-architecture-v2`, never `main`.

**Authority:** Independent High-risk Step 3 Code Reviewer.


### 2026-11-21 - Independent Step 3 Code Review: architecture description corrected (FAIL)

**Reviewed:** chatgpt-account-protocol-minimization at 7b0bf105cbc3982e31d979394f5d862ae2904894, against the accepted integration-architecture-v2 baseline b8f25cff351aed34b3de2f7ef1483a01183a9049. Initial task worktree was clean.

**Finding (low, confirmed documentation mismatch):** docs/architecture.md still described streaming HTTP as buffered until completion and ChatGPT Account prompt/recovery as owned by provider-specific JavaScript, with the protocol refactor outside scope. The implementation instead frames incremental ordered bounded SSE/NDJSON events and handles the account protocol in typed C#. This is a misleading architectural description, not evidence of an application runtime failure.

**Correction:** Updated the architecture description to reflect incremental bounded event delivery, acknowledged backpressure, signed transfer controls, typed C# account protocol orchestration, retained browser-native JS hooks and no-replay behavior. No production implementation or test code was changed.

**Verification:** 40/40 focused Node tests, 25/25 further context/provider/transport tests, and 3/3 UI/model tests including real Chromium passed; typed C# ChatGPT account protocol, architecture, account login/model/recovery, agent/provider lifetime and integration-contract harnesses passed; Release solution build passed with one previously documented unrelated WhatsApp CS0114 warning; ChatGPT packaging probe passed with three embedded modules and 96 concurrent versioned initializations; git diff --check passed. Model-mismatch SSR tests emitted non-failing useLayoutEffect warnings. Tests do not establish isolated authenticated live ChatGPT behavior, real provider signed transfers or overlapping distinct served-model/effort behavior.

**Outcome: FAIL** under .agents/code-review.md because this reviewer corrected documentation. A fresh independent Step 3 Code Reviewer must inspect the corrected branch before independent Step 4 Testing. Owner merge approval remains required; eventual integration target is integration-architecture-v2, never main.


### 2026-11-21 — Independent High-risk Step 3 Code Review: bounded upload and processing event isolation (FAIL)

**Reviewed:** Full task diff against `integration-architecture-v2` baseline `b8f25cff351aed34b3de2f7ef1483a01183a9049`, initially at `4c82f8aa35a409fb2129f064b8b7061e5cf19717`. Reviewed C# account protocol, conversation ancestry and final reply, files, provider native hooks, Electron HTTP/framing/event backpressure, signed transfers, C# transport, cancellation, tests, instructions and earlier review decisions.

**Finding 1 (medium; deterministic upload race):** `electron/browser-transfer.js` checked a file's length with `stat()`, then created an unbounded read stream. A file appended between the size check and the read could transmit bytes beyond the validated snapshot. Fixed by bounding the source stream to the checked byte range, handling zero-byte files, and rejecting short source reads. Added a regression that appends bytes after the initial stat and checks only the original content is sent.

**Finding 2 (medium; confirmed code path):** `ChatGptAccountFiles.VerifyProcessing` retained an SSE `event:` name across blank boundaries and applied it to later bare JSON lines from the mixed SSE/NDJSON upload-processing endpoint. Thus a later matching-file JSON record lacking its own success event could be misclassified as a completed upload. Fixed by clearing SSE event names at blank frame boundaries and only inheriting a name for `data:` lines. Added a C# harness case with an earlier success name on a different file followed by a bare matching-file record; it now rejects false success before prompt submission.

**Corrections:** Pushed on the same task branch in `2d9b28b`, `325c553`, `5642088`, `5d2464a`. No second client, session owner, scheduler, prompt retry or change to accepted model-preference behavior. The unrelated automated `chore(codegraph): regenerate graph` subsequently advanced the branch to `52ec5e7` without changing reviewed production code.

**Verified (MEŽS host shell outputs):** Targeted Node HTTP/transfer tests **21/21 PASS**; broader ChatGPT/account/provider/HTTP/Electron/model-mismatch Node tests **63/63 PASS**; typed C# account-protocol harness **PASS** including new upload-processing regression; `dotnet build Mezhs.sln -c Release --no-restore -v quiet` **PASS** (one previously documented unrelated WhatsApp CS0114 warning); `tests/architecture.ps1` **PASS**; `git diff --check` **PASS**; working tree clean at the tested `5d2464a`. The larger node suite emitted React SSR `useLayoutEffect` warnings without test failure. Prior tests and this review do not establish live authenticated Chromium functionality, provider-side cancellation, actual signed transfers with ChatGPT, or concurrent distinct served-model/effort behavior.

**Outcome: FAIL.** Corrections require a **fresh independent Step 3 Code Reviewer**, followed only after independent review PASS by Step 4 independent Testing. No Testing PASS, owner merge approval, release or merge is implied. The only authorized eventual merge destination is `integration-architecture-v2`, never `main`.

**Authority:** Independent High-risk Step 3 Code Reviewer under `.agents/code-review.md` and accepted task plan.

### 2026-11-21 — Fourth independent Code Review: SSE event bound (FAIL)

**Reviewed:** feature commit 2a46bcea207b0d1d0984f90df3a5edf986d10d15; required parent baseline b8f25cff351aed34b3de2f7ef1483a01183a9049. Initially clean task worktree.

**Finding (medium; reproduced):** StreamFramer in electron/browser-http.js counted SSE data content bytes but not newline separators between data lines. A reproduction accepted 307,200 empty data lines with eventBytes=0 despite a 262,144-byte event limit. This permits oversized buffered SSE events; no unrelated live-provider failure is inferred.

**Correction:** Account for the one-byte newline separating subsequent SSE data lines; add regression covering oversized empty-line events. No added ownership or queueing mechanisms.

**Verification:** Targeted Node 20/20 PASS; expanded account/provider/Electron/HTTP/transfer Node 56/56 PASS; typed C# ChatGPT account protocol harness PASS; Release solution build PASS (one previously documented WhatsApp CS0114 warning); tests/architecture.ps1 PASS; JavaScript syntax checks and git diff --check PASS. No live authenticated Chromium test or overlapping distinct-model/effort verification; unrelated prior group-focus failure not rerun.

**Outcome: FAIL** because code and tests were corrected. Fresh independent Step 3 Code Review required before independent Testing and explicit owner approval for eventual merge to integration-architecture-v2. Do not merge to main.

**Authority:** Independent High-risk Step 3 Code Reviewer.


### 2026-11-21 - Third independent Code Review corrections; fresh review required (FAIL)

**Reviewed:** feature revision 6cd359b543a2aa91c6b252ce208fe3a14e829c7b relative to required integration-architecture-v2 baseline b8f25cff351aed34b3de2f7ef1483a01183a9049. The initial task worktree was clean.

**Finding 1 (medium, reproduced):** Interrupted signed downloads in electron/browser-transfer.js released the reader lock without cancelling an unread upstream body. An oversized streamed chunk produced the expected transfer failure and partial-file cleanup, but the isolated reproduction reported UPSTREAM_CANCELLED=false. Other rejected download responses and upload responses also left unread bodies open. Corrected reader cancellation on incomplete downloads, and early rejection/unused upload response cleanup. This is resource cleanup, not evidence of an unrelated application failure.

**Finding 2 (medium, code path):** A non-streaming browser HTTP response lacking an incremental reader could fall through to response.text() before the 16 MiB response limit was checked. That fallback cannot enforce bounded reads. Replaced it with an explicit protocol failure for non-null bodies without a reader; empty-body responses remain supported.

**Corrections:** electron/browser-transfer.js, electron/browser-http.js and targeted regression tests tests/browser-signed-transfer.test.js and tests/browser-http-streaming.test.js. Kept existing transfer/session/credential ownership; no new client, scheduler or provider state.

**Verification:** Targeted response and signed-transfer tests 19/19 PASS; broader account/provider/HTTP/Electron Node suite PASS (59 tests); C# ChatGPT account protocol flow PASS (restoration, no replay, metadata, citations, authorization and attachments); architecture checks PASS; dotnet build Mezhs.sln -c Release --no-restore -v quiet PASS with one documented WhatsApp CS0114 warning; git diff --check PASS. Initial read-only oversized-download reproduction showed the uncancelled response. No isolated authenticated live ChatGPT Chromium test or concurrent distinct model/effort provider test was performed; the unrelated known group-focus test was not rerun.

**Outcome: FAIL** per .agents/code-review.md because implementation and tests were corrected. A fresh independent Step 3 Code Reviewer must review the corrected revision; independent Testing, explicit owner merge approval and merge to integration-architecture-v2 remain pending. Do not merge to main.

**Authority:** Independent High-risk Step 3 Code Reviewer.


### 2026-11-21 — Fresh independent Code Review corrections; next review required (FAIL)

**Reviewed:** feature revision `e05721aac967d2747559c4d260fd3989c97bd801`, relative to approved `integration-architecture-v2` baseline `b8f25cff351aed34b3de2f7ef1483a01183a9049`. Initial feature worktree was clean.

**Finding 1 (medium, reproduced):** `electron/browser-http.js` released incremental body readers without cancelling unread responses following malformed/oversized streamed frames or oversized JSON. A 256 KiB+ SSE frame raised the expected error but reported `UPSTREAM_CANCELLED=false`. Corrected both streaming and JSON reader paths to cancel unfinished streams in `finally`, preserving the originating failure, with two regression tests. This establishes response-resource cleanup, not the cause of any unrelated application symptom.

**Finding 2 (medium, code path):** `electron/main.js` deleted terminal operation state on HTTP response `finish` when the event cursor had been acknowledged. Socket write completion does not prove that the C# client received the terminal result; later duplicate status polls could therefore get 404. Removed that eager deletion and retained the existing bounded 10-minute TTL, with a guard test. This preserves server-side retryability but does **not** implement automatic HTTP status-poll retry in C#: a transport failure can still end a call. No provider prompt is replayed.

**Corrections:** `electron/browser-http.js`, `electron/main.js`, `tests/browser-http-streaming.test.js`, `tests/electron-operation-stream-events.test.js`. No extra credential owner, browser session, scheduler, or provider-state framework.

**Verified:** 56/56 targeted account/provider/HTTP/transfer/Electron Node tests; C# ChatGPT account protocol flow harness (metadata, citations, restoration, no replay, interruption, authorization, uploads/downloads); Release solution build (`dotnet build Mezhs.sln -c Release --no-restore -v quiet`, only previously documented WhatsApp CS0114 warning); `tests/architecture.ps1`; JavaScript syntax checks and `git diff --check`. The terminal-result regression test verifies the source lifetime rule, not a complete lost-response network simulation. Did not execute authenticated live ChatGPT Chromium, overlapping distinct model/effort sessions, or the unrelated known group-dialog focus test in this review.

**Outcome: FAIL** per `.agents/code-review.md` because code and tests were corrected. Spawn a fresh independent Step 3 Code Reviewer to assess the committed corrections; independent Testing, owner merge approval and integration into `integration-architecture-v2` remain pending. Never merge directly to `main`.

**Authority:** Independent High-risk Step 3 Code Reviewer.


### 2026-11-21 — Independent Code Review corrections; fresh review required (FAIL)

**Change:** Reviewed implementation at `fe09cf71699db509268c590466128b310428b0eb`; identified an existing-conversation preflight regression: typed GET parsed the entire conversation body under the 16 MiB response limit, where the previous path only checked HTTP accessibility. Corrected on task branch in `1baaeda0c0fa8e90ae6a87c491c71b89adafdb83`: added a reusable headers-only typed HTTP GET, cancelled its unread Electron response body, and used it for ChatGPT continuation preflight. Also rejected streaming responses lacking an incremental reader instead of buffering unbounded response text. Added Node and C# regression coverage.

**Reason:** An accessible conversation with a large response body could fail before an otherwise valid continuation was submitted. The non-incremental streaming fallback contradicted the accepted bounded, request-active protocol event contract.

**Verification:** After correction, clean task worktree at `1baaeda`: 32/32 targeted Node tests, C# ChatGPT account protocol flow (including large continuation, no-replay, restoration, files), Release solution build and frontend builds, architecture and account-login scripts, Integration Contracts probe, ChatGPT packaging probe (3 published resources and 96 concurrent initializations), and agent/provider lifetime checks passed. Release build had one pre-existing WhatsApp CS0114 warning. The first packaging invocation omitted its required extraction directory; rerun with an isolated directory passed. `git diff --check` passed. Authenticated live ChatGPT, overlapping distinct-model selections and the known unfiltered unrelated group-focus test failure were not reverified by this review.

**Outcome:** **FAIL** per `.agents/code-review.md` because the reviewer corrected implementation and tests. A fresh independent Step 3 Code Reviewer must inspect the current revision before any Code Review PASS; Step 4 Testing and merge remain pending. No owner approval is implied.

**Authority:** Independent Code Review, Step 3, following the High-risk development workflow.

### 2026-11-21 — Development implementation and verification, ready for independent Code Review

**Change:** Moved authenticated ChatGPT Account new/follow-up turn protocol, models/projects, reply ancestry and polling, incremental event interpretation, citations/analysis/model-effort metadata and file-processing orchestration to typed C#. Deleted the obsolete `account-files.js` (215 lines), `account-messages.js` (350), `account-models.js` (34), `account-results.js` (315) and `account-stream.js` (245), totaling **1,159 removed production JavaScript lines**. Retained `account-transport.js` for browser-owned credentials, integrity observation, Sentinel and conduit preparation; `entry.js` for the provider bootstrap/native hooks and untouched Guest page operations; and `guest.js` for guest orchestration. Added reusable bounded authenticated HTTP framing, ordered acknowledged event delivery with backpressure, safe signed binary transfers, explicit framing errors, scoped Stop message identity and protected HTTP redirect handling. The existing account-level model-preference PATCH remains.

**Reproducible inventory:** Against reviewed parent `b8f25cff351aed34b3de2f7ef1483a01183a9049`, physical source lines including comments and blanks: the six `account-*.js` files were **1,435** (215+350+34+315+245+276) and are now **315** (one retained account transport). Mixed `entry.js` shrank **256 → 182**, including unchanged Guest page operations; `guest.js` stayed **12 → 12**. Using the reviewed plan's **~139 baseline account-owned entry.js lines** and a current **~65 account-owned entry.js lines** (remaining bootstrap/auth/hooks; attribution approximate), account-owned JS is **~1,574 → ~380 (−1,194, ~75.9%)**. The five deleted modules alone prove >50% removal independent of entry attribution (1,159/1,574 = ~73.6%). Five provider C# files `ChatGptIntegration.cs`, `ChatGptTypedHttpEndpoints.cs`, `ChatGptAccountProtocol.cs`, `ChatGptConversationState.cs`, `ChatGptAccountFiles.cs` total **312 → 1,261** (baseline first two were 215+97; current 205+114+295+429+218). The combined approximate provider-specific JS+C# measure is **1,886 → 1,641 lines (−245, ~13.0%)**. It counts entire mixed `ChatGptIntegration.cs` on both sides and excludes unchanged Guest; exact account-only allocation in mixed files is inherently approximate. Shared infrastructure grew by ~**600 lines** on six enumerated components: new `electron/browser-http.js` +234, new `electron/browser-transfer.js` +135, `BrowserHttpExecutor.cs` +59, `IntegrationHttpClient.cs` +59, `ElectronBrowserTransport.cs` +47, `electron/main.js` +66. Some shared infrastructure is currently exercised principally by ChatGPT; report it separately rather than claiming a net project-wide LOC reduction. Transport accepts endpoint mappings from other providers without carrying ChatGPT request, citation or response-tree interpretation.

**Verified evidence:** Targeted 43/43 Node protocol/context/event tests; 7/7 signed-transfer security tests; typed C# account-protocol harness including restore, no-replay, metadata, citations, malformed events and files; account login/model/project probe; architecture and Agent provider-lifetime scripts; `Mezhs.Integration.Contracts.Probe`; extracted assembly resource parity; `ChatGptPackagingProbe` with immutable versioned publication and 96 concurrent initializations; full `dotnet build Mezhs.sln -c Release` (success, one unchanged WhatsApp CS0114 warning); Agent Web and ClientApp TypeScript/Vite builds as part of solution build. `node --test --test-reporter=dot --test-skip-pattern="^Chromium group deletion:"` passed against a clean task worktree after installing WhatsApp's locked npm dependencies in ignored `node_modules`. Unfiltered `node --test` and a standalone `tests/group-delete-browser.test.cjs` both failed a group-dialog focus-restoration assertion. The group test, `MezhsChatApp.tsx` and Agent Web `package.json` are byte-identical to the reviewed parent; this establishes lack of modifications in those files, not whether the focus issue also occurs when running the parent revision. The missing WhatsApp npm dependency in the first broad run was resolved by `npm ci --prefix src/Mezhs.WhatsApp.Api --ignore-scripts --no-audit --no-fund` in this isolated worktree. Build-time npm audit printed existing dependency vulnerability counts; no unrelated dependency changes were made.

**Limitations:** No isolated authenticated live ChatGPT Chromium session was run, so live Sentinel/provider protocol drift, actual image/artifact transfer with the live backend, and simultaneous overlapping model/effort selections are **not verified**. The unchanged model-preference PATCH may remain account-global at the provider side; do not claim concurrent distinct-model execution is guaranteed. The unrelated Chromium group-focus failure prevents claiming an unqualified whole-repository test PASS. Preserve the task branch and request owner approval only at Deploy; do not merge into `integration-architecture-v2` or `main` during Code Review/Testing.

**Authority and trade-off:** Development implementation on the authorized task branch after independent Plan Review PASS at `e87dfdcc81e50b89d471b3d1c5bca64fb83eebb0`. The primary majority-JavaScript-removal objective is met and the old duplicate send path is removed; total project-wide source size grows with reusable transport and tests, requiring independent review of necessity and security. Handoff to a fresh independent Code Reviewer (Step 3), followed by independent Testing (Step 4). This is Development completion evidence, **not** an independent Code Review or Testing PASS.


### 2026-11-21 — Fresh independent Plan Review PASS for the JS-first design

**Change:** Step 1 independent Plan Review PASS against task branch revision `e87dfdcc81e50b89d471b3d1c5bca64fb83eebb0`, after the ordered-stream-event and bounded-transfer corrections. Development may proceed on this task branch under the revised majority-JavaScript-removal acceptance criteria.
**Reason:** Reviewed the updated plan, workflow, integration architecture and existing ChatGPT Account JS/C# plus typed HTTP and Electron transport contracts. The plan preserves single browser/session ownership, authenticated native security preparation, non-replayable submissions, per-chat concurrency and retained model-preference behavior. It explicitly requires request-active, ordered and bounded event delivery independent of best-effort progress snapshots; bounded, credential-separated file transfers and redirect checks; transparent account-specific versus shared code accounting; and relevant failure, Chromium and integration testing. No remaining blocking plan defect was identified.
**Authority:** Fresh independent Plan Reviewer following `.agents/plan-review.md`, against parent baseline `b8f25cff351aed34b3de2f7ef1483a01183a9049`.
**Trade-off:** This PASS validates the proposed approach, not implemented security or compatibility. The specific transport design, quantitative JS removal, no-loss event delivery and authorized live-provider behavior must be demonstrated during Development, Code Review and Testing. The eventual merge destination remains `integration-architecture-v2` and requires explicit owner approval.


### 2026-11-21 - Independent review: ordered event delivery and bounded file transfer

**Change:** Clarified that incremental protocol events need a bounded, ordered, non-coalescing delivery path to C# rather than the Electron latest-progress snapshot; explicitly require frame-boundary, overflow and cancellation checks. Clarified cross-origin credential separation, redirect validation and bounded file I/O with security regression tests.
**Reason:** The current Electron operation status exposes only its latest progress snapshot, which may overwrite events between polls; existing file code also buffers complete files. Neither mechanism by itself satisfies the new streaming and safe-transfer requirements.
**Authority:** Independent Plan Review against existing transport and account implementation.
**Trade-off:** Developer may choose the smallest bounded event transport compatible with existing operation ownership; no second session/client or stronger remote-cancellation guarantees are authorized.


### 2026-11-21 — Owner clarified JS-first browser integration standardization

**Change:** Owner resolved the Development complexity-stop decision: prioritize removing the majority of provider JavaScript and standardizing browser integrations so private APIs can be mapped primarily in C#, ideally with zero provider JS. Explicitly authorize a bounded redesign of the existing reusable browser HTTP/streaming/transfer bridge; exceptional JS is acceptable only where native page execution is truly necessary. Revise quantitative acceptance to **more than 50% account JS removed** and independently justify remaining JS. Retain the former 900-line total-code ambition as secondary, not a hard gate; measure total cost transparently.
**Reason:** The tested projects migration increased combined provider production code by 11 lines and revealed that chasing total LOC through a tiny C# migration missed the owner's actual aim. The owner clarified the goal directly after the blocked Development report.
**Authority:** Project owner, task follow-up on 2026-11-21.
**Trade-off:** Shared transport and C# protocol logic may grow while provider JS shrinks. No permission to compromise session ownership, Sentinel security, streaming progress, no-replay, files or concurrency. The earlier 2026-11-21 Development BLOCKED record remains historically accurate but its owner-decision blocker is **resolved**. Because this materially revises the architectural goal and acceptance criteria, fresh independent Plan Review is required before resuming Development. The required parent branch and merge approval rules are unchanged.

### 2026-11-21 — Development BLOCKED: typed-project slice does not minimize overall account code

**Change:** Project enumeration now uses the existing BrowserHttpExecutor and a ChatGPT-specific typed endpoint/DTO for paginated project listing, replacing the JavaScript getProjects operation. Updated packaging, operation, and account-login fixtures. This slice is tested, but Development is **blocked**, not complete; Code Review/Testing/Deploy stages are not authorized to proceed as a successful minimization.

**Reason:** At reviewed parent baseline f2ce33d versus this slice, six account-*.js modules are 1,435 to 1,435 physical lines, mixed entry.js 256 to 235, ChatGptIntegration.cs 215 to 230, ChatGptTypedHttpEndpoints.cs 97 to 114. Combined account and guest files **2,003 to 2,014, +11 lines**; using the plan's approximately 139-line account attribution for mixed entry.js gives approximately 1,850 to 1,861 account lines, still far above the 900-line design target. There are no new shared helpers, no removed legacy account modules, and no defensible overall code reduction.

**Verification:** 70 account/provider Node tests, 20 Electron context/lifecycle Node tests, one Chromium rich-UI test (after canonical .NET test project build), and account-login/model/project/restore flow passed. Release solution build (one unrelated WhatsApp CS0114 warning), ChatGPT packaging probe, integration contracts probe, Agent Web and ClientApp TypeScript/Vite builds passed. These do not establish real authenticated ChatGPT behavior, nor do they verify concurrent distinct-model/effort selections in separate authorized profiles.

**Authority:** Development measurements and the plan's complexity-stop/owner-escalation rules. **Owner decision pending; no change to approved requirements is assumed.**

**Trade-off:** The existing typed HTTP path buffers up to 16 MiB and accepts only static, whitelisted request headers; ChatGPT turn submission still needs incremental SSE interpretation, per-turn browser-owned Sentinel and conduit tokens, uncertain-POST handling, ancestry selection, and signed transfer protections. A larger bounded streaming/transport redesign (with fresh independent review/testing), or an owner-approved change to the quantitative target/scope, is needed before claiming a migration. The provider's setModelPreference PATCH remains; no new auth owner, retry or model lock was introduced.


### 2026-11-21 — Independent Plan Review PASS

**Change:** Step 1 independent Plan Review PASS against branch revision `b07ac47989a68bf2b11f288a3e22f7c21cd59fb7`; Development is unblocked.
**Reason:** Confirmed feasible ownership boundaries against the parent implementation, explicit `integration-architecture-v2` merge destination, measurable before/after code cost, security and no-replay requirements, provider model-preference preservation, regression coverage and conditional owner-decision stops. No blocking plan defects remain.
**Authority:** Independent Plan Reviewer following `.agents/plan-review.md`, parent revision `b8f25cff351aed34b3de2f7ef1483a01183a9049`.
**Trade-off:** The 900-line objective remains a conditional target rather than a guarantee; actual reduction, live concurrent model behavior and authenticated Chromium verification remain Development/Testing obligations.

### 2026-11-21 — Plan review: provider model-preference boundary

**Change:** Clarified that the existing ChatGPT account-level `setModelPreference` PATCH is a provider-side mutation, distinct from forbidden new connection-global integration state. Retain it until isolated overlapping-model live verification supports removal; otherwise document the unverified provider behavior.
**Reason:** Parent architecture explicitly records this unverified baseline limitation; the original blanket wording about global mutable model state could be misread as permission to remove a required provider mutation for line-count reduction.
**Authority:** Independent Plan Review against `integration-architecture-v2` and its architecture documentation.
**Trade-off:** A few provider-specific lines may remain without live verification; no stronger concurrency guarantee or model lock is introduced.

### 2026-11-21 — Initial task plan

**Change:** Created separate dependent task branch from `integration-architecture-v2` and defined a high-risk, independently reviewed ChatGPT Account minimization effort. Required final merge destination is `integration-architecture-v2` only.
**Reason:** Owner identified that the v2 account implementation still has excessive provider-specific JavaScript and requested a substantial architectural simplification without bypassing the parent branch.
**Authority:** Owner request in this task.
**Trade-off:** Ambitious size target is subordinate to full functionality, secure browser session ownership and non-replayable submission semantics; inability to meet it requires an explicit owner decision, not hidden scope reductions.

### 2026-11-22 - Independent Step 3 Code Review: cancellation probe adapter (FAIL)

**Reviewed:** Feature revision c85d760 against the approved integration-architecture-v2 baseline b8f25cff.

**Finding:** The cancellation contract fake implemented InvokeAsync but not InvokeWithEventsAsync, which the migrated ChatGPT Account protocol requires. Its first run timed out without observing a transport invocation. This demonstrates an obsolete test adapter, not a confirmed production cancellation failure.

**Correction:** Added InvokeWithEventsAsync to the cancellation fake, delegating to its existing cancellable InvokeAsync. No production behavior changed.

**Verification:** 75 focused Node tests, typed C# protocol, authentication, architecture, integration contracts, resource packaging, 96 concurrent packaging initializations, corrected cancellation contract for eight registrations, Grok checks, and one real-Chromium UI test passed. Release build passed with one unrelated WhatsApp CS0114 warning.

**Limitations:** Live authenticated ChatGPT, real signed provider transfers, and concurrent distinct served-model and effort combinations remain unverified. Parent reconciliation and owner-approved merge remain pending.

**Outcome: FAIL.** Because the review corrected a regression test, a fresh independent Step 3 Code Review is required before independent Testing. The eventual merge target is integration-architecture-v2, never main.

**Authority:** Independent High-risk Step 3 Code Reviewer.

### 2026-11-22 — Independent Step 3 Code Review: release unread authorization probe bodies (FAIL)

**Reviewed:** Full account protocol, native browser security hooks, bounded HTTP/transfer bridge, ordered events, conversation recovery, cancellation, dependencies and focused regression coverage at `974be2520e467f470209763e549546eb20fc22c0`, relative to the approved `integration-architecture-v2` baseline `b8f25cff351aed34b3de2f7ef1483a01183a9049`. Later parent reconciliation remains pending.

**Finding (low; reproduced):** Repeated ChatGPT authorization checks returned without consuming or cancelling two unneeded response bodies: failed `/api/auth/session` responses in `accessToken`, and successful `/backend-api/models` responses in `isAuthorized`. An isolated streamed-response regression first failed with `cancelled=0` for the auth error path. The success path also visibly lacked a body read/cancel. This establishes unreleased unread response streams in the code; it does not establish an observed live resource leak, outage, or transport failure.

**Correction:** Cancel unread rejected authentication response bodies before returning unauthenticated, and cancel the unused successful model-probe body after checking HTTP status. Cancellation failures are best-effort and do not overwrite the already-determined authorization outcome. Added a Node regression covering both paths. Three commits on this branch: `f29accb` (reproducing regression), `80b8cbb` (auth error cleanup), `1b361a0` (model-probe cleanup).

**Verification:** At corrected `1b361a0028965f2da675148b7beb26a11f8a0b62`, 66/66 focused Node browser/account/transport/context tests PASS; typed C# account protocol flow PASS; integration contracts PASS; cancellation contract for eight integrations PASS; account login/model/recovery checks PASS; architecture PASS; Release solution build PASS with 0 errors and one unrelated pre-existing WhatsApp CS0114 warning; `git diff --check` PASS and task worktree clean. No isolated authenticated live ChatGPT/Chromium test, real signed provider transfer, or overlapping distinct served-model/effort test was performed here; previously documented limitations remain.

**Outcome: FAIL** under `.agents/code-review.md` because this review corrected implementation and regression tests. Fresh independent Step 3 Code Review is required before Step 4 Testing. No merge approval is implied. Authorized eventual merge destination is `integration-architecture-v2` only; never `main`.

**Authority:** Independent High-risk Step 3 Code Reviewer.


### 2026-11-22 — Independent Step 3 Code Review: validate HTTP errors before streaming (FAIL)

**Reviewed:** Task branch after the prior `1b361a0` authorization-response cleanup, against the approved `integration-architecture-v2` base `b8f25cff`; current remote parent `52d1c88` remains one commit ahead. Inspected the account C# protocol, reply ancestry, artifact and upload mapping, browser-native Sentinel/auth, generic bounded HTTP, ordered Electron event queue, signed transfers, session/cancellation and focused regressions.

**Finding (medium, source-confirmed):** `electron/browser-http.js` forwarded streamed response data to the ordered provider-event consumer without validating a raw fetch response's HTTP status. A generic provider policy returning an ordinary 429 response containing SSE-like data would emit protocol events before returning 429; a non-streamed large error body could instead exceed the JSON limit before conveying its HTTP status. ChatGPT's current policy `apiFetch` already throws on unsuccessful statuses, so no ChatGPT provider incident is claimed. The shared reusable transport contract is affected independently of that wrapper.

**Correction:** Before any stream framing or bounded success-body read, return non-2xx HTTP status to the typed caller and cancel the unread response body, without forwarding error-body protocol events. Added a regression using a raw 429 SSE response asserting no events, preserved status and cancellation. Committed test `0b6a5d3`, test literal correction `1cc7d71`, and transport fix `9a76d8a` on this task branch. No second session/client, prompt replay, or provider-specific state was added.

**Verification:** Re-fetched the corrected `browser-http.js` source and executed an isolated V8 smoke harness with shims for the unavailable Node/WHATWG globals: PASS for raw 429 status, unread body cancellation, zero error events, ordered normal 200 SSE frames, and reader release. Confirmed GitHub accepted the branch commits. This is **not** a complete Node test execution; Windows host shell, full Node/.NET suites, package checks, real Chromium, live ChatGPT, signed provider transfers and simultaneous distinct model/effort behavior were not run in this review environment and remain unverified here.

**Outcome: FAIL** under `.agents/code-review.md` because code and a regression test were corrected. A fresh independent Step 3 Code Reviewer must inspect and run relevant checks on the resulting branch revision before Step 4 Testing. Owner-approved merge remains limited to `integration-architecture-v2`, never `main`; parent reconciliation is still pending and no approval or deploy is implied.

**Authority:** Independent High-risk Step 3 Code Reviewer.

### 2026-11-22 — Owner-approved repeated-step workflow and branch-scoped handoff

**Change:** Updated `.agents/workflow.md`, `.agents/plan-review.md`, `.agents/code-review.md` and `.agents/testing.md` on this task branch. Repeated iterations prioritize independently verifying the previous corrections, checking related occurrences of the same defect, and completing the normal role scope. Material corrections require a fresh independent iteration; nonmaterial cleanup alone does not. Required independent stages, role-owned fixes, live-testing obligations and explicit owner merge approval remain unchanged.

**Reason:** Twelve Step 3 reviews repeatedly found variants of response-bounds and cleanup problems. The old rule treated any correction, including nonmaterial documentation or test-harness cleanup, as automatic FAIL, without checking convergence.

**Authority:** Owner direction in MEŽS execution `exec_b24327385f524adebb1db982056a599b`.

**Handoff override:** For this task, first fetch the latest remote `chatgpt-account-protocol-minimization` revision; the next agent MUST follow the workflow and role instructions from that branch, not the parent or `main`. Each subsequent handoff MUST explicitly instruct the next agent to use that same branch's workflow and to pass this instruction to later agents. This owner-directed instruction is the only addition to the neutral next-role prompt. Continue Step 3 independent Code Review at the latest branch revision; do not bypass required Step 4 Testing or merge approval.
