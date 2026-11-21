# ChatGPT Account protocol minimization

ID: `chatgpt-account-protocol-minimization`
Branch: `chatgpt-account-protocol-minimization`
Level: High-risk
Base and required merge destination: `integration-architecture-v2`
Base revision: `b8f25cff351aed34b3de2f7ef1483a01183a9049`

## What

Reduce the amount of code necessary to maintain ChatGPT Account by completing its migration from the six legacy JavaScript protocol modules and account operations in `entry.js` to the existing reusable integration architecture. Make the integration primarily endpoint definitions, provider-specific DTOs, and genuinely ChatGPT-specific protocol interpretation/orchestration. Reuse existing `BrowserAccountIntegrationBase`, `BrowserAccountSession`, typed HTTP transport, and their already established lifecycle and cancellation owners.

**Branch and merge requirement (owner instruction):** This task MUST be developed, reviewed, tested and merged back into **`integration-architecture-v2`**, never directly into `main`. Open any PR against `integration-architecture-v2`; after independent review/testing and **explicit owner merge approval**, merge `chatgpt-account-protocol-minimization` into `integration-architecture-v2`. If the parent advances, reconcile that parent into the task branch and reverify before the final merge. Do not merge the task branch into `main`, change `main`, or publish a release as part of this task.

Scope: ChatGPT Account sending, projects/models, request preparation, streamed response/progress, final conversation retrieval, file transfer, and the narrowly necessary shared transport primitives. Existing ChatGPT Guest, Gemini Guest, Grok Account, Core/Agent/UI semantics, and public API contracts must remain functional.

Out of scope: changing provider behavior merely to reduce line count; dropping files, project support, citations, reasoning/effort metadata, or cancellation; replacing genuine browser-owned ChatGPT security requirements; speculative provider frameworks; extra login/session ownership; altering concurrency guarantees; unrelated refactors; version/changelog/release updates.

## Why

The v2 architecture's typed authenticated HTTP executor currently serves model discovery, but message submission still follows a second, legacy JavaScript protocol implementation. That leaves ChatGPT Account as an expensive template for new integrations. The goal is **less total provider-specific production code and simpler ownership**, not just JavaScript-to-C# translation.

Measured baseline on the parent at `b8f25cf` (physical lines, including blanks/comments; account attribution in the mixed `entry.js` is approximate):
- `ChatGptAccountIntegration`: 179 C# lines; `ChatGptTypedHttpEndpoints.cs`: 97 C# lines.
- Six `account-*.js` modules: 1,435 JavaScript lines.
- Account portion of `entry.js`: approximately 139 JavaScript lines.
- **Approximately 1,850 account-specific production lines**, excluding guest, tests, documentation and existing shared infrastructure. Recalculate a precise baseline and explain ambiguous ownership in the first Development step.

**Design objective:** target **900 or fewer account-specific production lines** (approximately half the present size), with a stretch goal of 600–700 if safely attainable. This is a design target, **never permission to remove supported behavior or disguise one-provider code as shared infrastructure**. If a sound design cannot reach it, document the minimum justified code, the actual net reduction, and stop for an owner decision before final acceptance; do not manufacture abstraction layers or silently weaken requirements.

## Acceptance criteria

- [ ] The account-specific implementation has a reproducible before/after line inventory, including total changed production code and any new supposedly shared helpers used only by ChatGPT. Files merely renamed or moved do not count as a reduction. Dead superseded modules and branches are removed.
- [ ] Model discovery, account authentication, project selection, new chats and continuations, conversation-unavailable restoration, per-turn model/thinking effort, progress reporting, final response/analysis metadata, citation handling, artifacts, uploads and downloads preserve existing behavior.
- [ ] ChatGPT Account's primary send/receive flow uses the v2 typed endpoint/executor composition where practical, with provider-specific endpoint definitions, DTOs and response interpretation readable together in the integration. There is no parallel complete HTTP/streaming/client library maintained just for the account.
- [ ] Reusable transport owns appropriate HTTP validation, request serialization, streaming framing, cancellation, response limits/progress, and file-transfer mechanics. ChatGPT-specific request preparation, protocol interpretation and security operations remain provider-specific. No redundant `HttpIntegrationBase`, second session owner, scheduler, model-selection state or generic provider-state framework.
- [ ] Electron remains the owner of authenticated session/cookies and native Sentinel/browser-generated requirements. Never synthesize, cache across the wrong session, expose, or transfer account credentials/challenge tokens to arbitrary destinations. Signed file URLs remain validated; no credential leakage to other origins.
- [ ] No prompt replay after an uncertain mutation; correct chat/message ancestry and final reply selection; stop/cancellation release local permits and do not imply provider-side cancellation. Shared authenticated connection can serve independent chats under existing limits. Same-ChatId ordering remains centrally owned.
- [ ] ChatGPT Guest and the other providers still work; no unrelated provider migration is required. Public Core, Agent and UI contracts remain compatible unless the owner explicitly approves a necessary change.
- [ ] Relevant automated tests, architecture checks and real Chromium verification pass at the final revision; live-account checks use an isolated profile and report any tests that could not be performed. Record actual evidence and limitations.
- [ ] Independent Plan Review PASS precedes development; independent Code Review PASS and independent Testing PASS follow development. Any blocking corrections repeat the required independent role.
- [ ] Final work is committed/pushed only on `chatgpt-account-protocol-minimization`, reviewed against `integration-architecture-v2`, and **merged into `integration-architecture-v2` only after the owner's explicit approval**. Do not merge into `main`.

## How

1. **Measure first, then simplify by ownership.** Inventory every JS operation in `account-messages.js`, `account-results.js`, `account-stream.js`, `account-transport.js`, `account-files.js`, `account-models.js`, and the account portion of `entry.js`. Map each operation to existing typed HTTP, browser transport, session or provider-owned behavior. Record line and dependency counts before implementing changes.
2. **Design the smallest reusable bridge.** Expand the existing `IntegrationHttpClient`/`BrowserHttpExecutor` transport composition only for demonstrated missing capabilities. Browser-side generic HTTP execution should not be defined inside ChatGPT's module if multiple providers need it. Preserve provider-specific hooks for real ChatGPT Sentinel requirements and conduit preparation; do not try to simulate those in C#. Treat streaming progress incrementally if the current buffered typed HTTP contract is inadequate; retain accurate progress semantics.
3. **Migrate provider protocol by coherent vertical slices.** Suggested order: safe authenticated JSON endpoints/models/projects; turn preparation and non-replayable submission; streaming interpretation and final conversation ancestry; attachments/artifacts. Keep the existing protocol working until its replacement is verified; remove old machinery slice-by-slice rather than leave two competing stacks at completion. C# DTOs and protocol interpretation are preferred where browser execution is not required.
4. **Reuse only real cross-provider mechanics.** Shared HTTP execution, bounded streaming framing, safe file transfers, cancellation and polling cadence can be shared when actually transport-agnostic. ChatGPT model catalog shape, message tree, citation references, Sentinel proof flow, signed upload protocol and other provider-specific mappings must not be placed in shared libraries merely to meet the line target. Count one-provider-only helpers in the account total.
5. **Respect existing guarantees.** Account-scoped auth and per-chat context remain in existing browser bases. Do not introduce connection-global model-selection state or hidden retries; durable message IDs are created once by Core. The baseline still performs ChatGPT's account-level `setModelPreference` PATCH before sending. Treat that provider-side mutation as existing behavior, not redundant local state: preserve it unless isolated live tests establish that removing it retains model/effort selection, including two overlapping conversations with different selections. If that cannot be verified, retain the mutation and report the unverified concurrent-model limitation; do not add a global model lock or assert independent served-model selection. Do not loosen security restrictions, replay semantics or context isolation.
6. **Complexity stop:** If secure typed HTTP sending requires another credential/session owner, a second generic client framework, unbounded polling, loss of streaming activity/attachment support, or materially more shared-only-for-ChatGPT code, stop and present the minimal alternatives and measured cost to the owner before proceeding.

## Steps

1. **Plan Review** — independent agent examines feasibility, ownership, quantitative accounting, baseline behavior, merge destination and security/verification completeness. Correct the plan and repeat independent review until PASS or owner decision.
   - Done when Plan Review PASS is recorded on this task branch.
2. **Development** — record baseline metrics and design ownership; implement the simplest safe ChatGPT Account migration in incremental slices; remove obsolete paths; update tests and this plan's Changes section.
   - Done when a measured reduction is demonstrated, affected code is committed/pushed to the task branch, developer checks pass, and no hidden legacy parallel protocol remains.
3. **Code Review** — fresh independent reviewer examines actual code ownership, total code cost, security, cancellation/replay, concurrency, protocol edge cases and test coverage; correct findings, repeat until PASS.
   - Done when an independent Code Review PASS is recorded against the current revision.
4. **Testing** — fresh independent tester validates automated suites and real Chromium/provider behavior where safely available, including failure cases; correct failures and repeat independent review/testing as workflow requires.
   - Done when independent Testing PASS, revision and limitations are documented.
5. **Deploy / merge to parent** — after explicit owner approval, update from the latest `integration-architecture-v2`, resolve only justified conflicts, rerun relevant verification, then merge **this task branch into `integration-architecture-v2`** using a PR targeting that branch or owner-approved equivalent. Verify the resulting remote parent SHA and task ancestry. Never target or modify `main`.
   - Done when remote `integration-architecture-v2` contains the approved implementation and the parent is verified; do not assume approval from test PASS.

Only one active agent/chat may work on this task at a time. Use new independent agents according to `.agents/workflow.md` and `.agents/mezhs.md`. Do not start Development before Plan Review PASS.

## Verification

- Measure actual total production lines before/after, separated into account-owned C#/JS, shared infrastructure changes, and other providers; list removed modules, added abstractions and any newly shared helpers with a single caller. Include git diff and maintainability assessment, not just LOC.
- Run Release solution build, frontend typecheck/Vite build, `Mezhs.Integration.Contracts.Probe`, ChatGPT packaging/architecture checks and relevant `node --test` suites: `tests/chatgpt-account-api.test.js`, `tests/browser-provider-operations.test.js`, `tests/chatgpt-browser-package.test.js`, `tests/typed-http-feasibility.test.js`, `tests/integration-model-mismatch.test.cjs`, `tests/chatgpt-conversation-unavailable.test.js`, `tests/rich-model-ui-browser.test.cjs`, and relevant Electron context/cancellation tests.
- Cover authenticated model selection, projects, first turn/continuation, concurrent chats, absent/stale final responses, 401/429/stream interruption, abort before/after submission, uncertain POST no-replay, upload process failures, signed storage destination safety, artifacts, citations, thinking-effort metadata and state restoration. In an isolated authorized live profile, compare overlapping conversations with distinct model/effort selections before changing or removing the provider-level `setModelPreference` PATCH; without such evidence, preserve the existing behavior and explicitly mark the concurrent served-model guarantee unverified.
- Build the canonical .NET rich-UI test project explicitly before Node rich-UI tests to avoid stale fixture DLLs where affected.
- Real provider testing, if authorized and available, must use an isolated MEZS instance/profile and separate storage as `.agents/mezhs.md` requires; never reuse or copy the owner's active profile. State exactly what was/was not tested.
- Before merge, compare latest `integration-architecture-v2` and rerun impacted checks after reconciliation. Confirm PR base/destination is `integration-architecture-v2`, not `main`; record approval and final parent commit.

## Changes

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
