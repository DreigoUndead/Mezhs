# Changelog

This changelog records project releases. One approved implementation merge creates one release version. Earlier work is recorded in Git history.

## [0.9.2] - 2026-11-20

### Fixed
- Replace exceptions for expected Rich UI parser validation failures with explicit bounded failure results; retain fail-closed Agent command handling, inert Unknown/SVG fallback, diagnostic output and resource limits.
- Preserve existing parser AST and command-candidate behavior while eliminating routine debugger first-chance exception noise.
- Make first-chance regression tests resilient to parser type and namespace renames by inspecting stack-frame declaring types instead of a hardcoded class-name string.

### Verification and approval
- Owner explicitly approved merging PR #72 on 2026-11-20. The task merged into `main` as `24b4519bf0634f14bec3116be6c0f158d8b022f0`; plan and verification were archived under `specifications/implemented/parser-expected-failures-no-exceptions/`.
- Single-agent, **non-independent** self-review and developer tests followed the owner's explicit workflow override.
- The Release build succeeded with 0 errors and one unrelated WhatsApp CS0114 warning. C# parser tests passed 121/121 with 0 expected parser first-chance exceptions. Relevant browser tests passed 66/66 and provider JavaScript tests passed 120/120.
- Read-only historical replay of 6,666 assistant messages matched the original parser with 0 AST and command differences across 8,325 candidates; parser first-chance exceptions dropped from 310 to 0. Unicode differential matched all 2,214 cases.
- The implementation commit `6d979bdccad32f0df411d3664058cf9217f5b2a2` passed the hosted MEŽS Core workflow (run 44776810417). A positive-control test of the original parser DLL detected 27 first-chance exceptions, compared with 0 on the updated parser.

### Known limitations
- Final bot-only Codegraph commit `b91836d0ddb3aec81744378f22a59c2216fec8f1` had a **failed** MEŽS Core workflow (run 44785761061); its cause was not established. Its code diff from the approved archived head is limited to `.agents/codegraph/graph.json`. No passing Core CI result was established for the final merged PR head.
- Unrelated `tests/group-delete-browser.test.cjs` focus-restoration check failed in isolation; it was not counted as passing or fixed in this task.
- Independent review and live authenticated desktop testing were not performed under the owner's one-agent override.

## [0.9.1] - 2026-11-20

### Changed
- Replace fragmented development-agent instructions with a reusable role-based workflow covering proportional planning, independent review, development, testing, Deploy and release versioning.
- Keep task-plan decisions and meaningful changes across clean agent handoffs, with neutral next-role prompts and explicit owner approval before merging.
- Separate MEŽS-specific agent creation, execution and isolated live-test guidance from generic workflow instructions.
- Remove obsolete workflow files, update active links and PR guidance, and archive the approved plan under `specifications/implemented/` during Deploy.
- Reconcile `docs/agent-tools.md` with the current Agent command protocol after integrating `main`; retain automated Codegraph generation without manual graph edits.

### Verification and approval
- Owner explicitly instructed merging PR #70 on 2026-11-20; task branch head `b93d431373f94aa146309e8b905e3a785c76f0cc` was merged in `a9a49787cd743d0b2bb111c7f24e497dfee30f50`.
- MEŽS Core Windows CI passed at prior head `84819605a2947693e7b522dbf6a51a396859ea98` (run `44651604501`); the final branch commit was documentation-only plan archival and its Core CI was in progress when the merge was authorized.
- Codegraph regeneration passed on the final branch commit (run `44733417298`). Relative Markdown targets in 65 files were checked with no missing paths.
- No application source behavior was changed by this task.

### Known limitations
- A fresh independent integrated Code Review after branch synchronization was not completed; the owner directed merging with that gap disclosed.
- No separate local live application testing was performed for this documentation refactor. Final archived-head Core CI had not completed when the merge was performed.

## [0.9.0] - 2026-11-20

### Added
- Automatically regenerate and commit Codegraph on non-main task-branch pushes when the generated graph changes.

### Changed
- Pin Codegraph 3.0.0 and native dependency 2.0.0; reject degraded parser output before committing.
- Use graph-only bot commits, branch-specific concurrency and non-force pushes; prevent graph-only self-trigger loops.
- Preserve the manual exporter as a fallback and require verification against the final branch SHA.

### Verification and approval
- Independent High-risk final testing PASS at PR #67 head d29cad9b00ff5c8d94cef62e4366aff19b580d6a against main ed98bb5c3f9c0a07fbf6589b4b6819fdba0519ee.
- Windows Codegraph workflow run 44623330636 succeeded; independent testing evidence: https://github.com/DreigoUndead/Mezhs/pull/67#issuecomment-7025777531
- Owner explicitly approved merging PR #67 on 2026-11-20 and overrode the uncompleted independent code-review gate.

### Known limitations
- Independent code review PASS was not recorded before the owner-approved merge.
- Full hosted Core CI did not pass on the bot-created final SHA. Available local exact-SHA regressions passed; Agent runtime resilience remained environment-blocked by missing PowerShell 7.
- Git race and degraded-output tests used the actual workflow Bash step and disposable local Git fixtures, not a hosted concurrency test.

## [0.8.3] - 2026-11-19

### Fixed
- Match complete rich UI and Agent command tags independently of line placement, with balanced containers and fail-closed command authorization.
- Show unsupported, malformed and invalid rich controls as inert Unknown diagnostics while preserving valid registered children and independent UI siblings.
- Keep SH, ATTACH and DONE root-only inside known and Unknown wrappers, SVG, escape and opaque editors; reject invalid command batches before side effects.
- Preserve indented rich layouts, ordinary Markdown tables and fenced content, and safely interpret bounded data-only style objects.

### Verification and approval
- Independent High-risk full-diff code review PASS; the owner accepted malformed nested model HTML rendering as a non-blocking presentation limitation.
- Independent Windows final testing PASS at PR #64 head c5126c04c1e2f20bfd01731c16548cf3a0ce2888 against main d4a782e18bde189e63d8e0c83007209398f9860f: Release build, 97 C# parser checks, 67 Chromium/Node tests, 19 shared UI tests, 9 adversarial parser and 11 live Chromium checks, real Agent protocol/policy/attachment and hosted failure-path tests.
- Verification details: https://github.com/DreigoUndead/Mezhs/pull/64#issuecomment-7021222114
- Project owner explicitly approved merging the completed PR #64 on 2026-11-19.

### Known limitations
- No full authenticated live-provider Electron desktop session was tested; Windows application hosts and real headless Chromium were exercised using isolated controlled integration responses.

## [0.8.2] — 2026-11-17

### Fixed
- Keep typing responsive in long Agent and ordinary chat histories by moving unsent per-conversation drafts out of the application-level render state and subscribing only the relevant composer controls.
- Preserve draft handoff, newer edits and failed-send restoration across new chats, existing chats and asynchronous submissions.
- Skip unchanged Agent chat-list polling renders while retaining active execution elapsed-time updates; restore ordinary transcript and execution-history JSX without manually maintained large memoization wrappers.

### Verification and approval
- Independent Standard code review **PASS** for PR #63 at `b9a791e17418e1a95edb52009f7498d50822bf8b`.
- Exact-head MEŽS Core Windows CI **PASS** (run `43963941068`), including frontend builds, real Chromium Agent long-history typing/render isolation, unchanged polling, live transcript updates, scoped drafts and existing handoff/restore/scroll regressions, plus Core/Agent/provider checks.
- No separate independent final-testing execution; owner explicitly approved merging this reviewed, CI-green candidate on 2026-11-17.
- Owner-approved PR #63 merge commit: `f50fb9bf7a02c9a7569423076243c47b7ca45672`.

### Known limitations
- Automated checks establish render isolation, not measured end-to-end keyboard latency in the owner's live Windows app; ordinary chat draft isolation is also covered by a scoped harness rather than direct parent-render instrumentation of the full chat app.

## [0.8.1] — 2026-11-16

### Changed
- Distinguish MEŽS-owned integration admission waits: capacity displays `Waiting for integration slot (N/N)…` (occupied slots / configured limit) and configured start spacing displays `Waiting for integration start interval…`.
- Keep Agent execution status Running while an admitted provider reports waiting, thinking, or progress; use neutral `Continuing after command result…` wording for other follow-up activity.
- Expose transient admission reason and positive `maxParallel` in Agent chat list/detail for the exact active running execution and model message. No persisted status or concurrency/scheduler changes.

### Verification and approval
- Independent Standard code review PASS at `406adb0bd193c33751155f5f1f8206e24818031e` (PR #62).
- MEŽS Core CI PASS on that exact head (GitHub Actions run `43839565954`), including real-host admission-projection and real-Chromium Agent status regressions, with .NET and Agent Web builds.
- No separate independent final-testing cycle; owner accepted the reviewed scope and green CI and explicitly authorized merging PR #62 on 2026-11-16.
- Owner-approved merge commit: `8457c462f5c65805047dc1be84096687e9db2788`.

### Known limitations
- `N/N` shows fully occupied integration slots, not queue position. The real-host admission test covers `1/1`; the Chromium UI tests the `3/3` label with a mock API.

## [0.8.0] — 2026-11-16

### Added
- Canonical, bounded C# Rich UI/Markdown parser and typed document contract shared by ordinary chat and Agent responses.
- Safe declarative layouts, text, citations, charts and SVG rendering; editable raw `CodeBlock` and `WritingBlock`; and host-owned radio question controls.
- Validated structural/list contexts for nested Agent shell and attachment commands, with global `DONE` ordering.

### Changed
- Replace competing frontend markup/expression processing with a single C# source of truth and typed React rendering. Unsupported or provisional markup remains inert.
- Harden command authorization, Markdown literal/opaque boundaries, SVG/URL restrictions, and rejection of invalid command-bearing documents before side effects.
- Synchronize `docs/supported-rich-ui.md` with supported declarative syntax; retain the canonical Agent protocol reference in `docs/agent-tools.md`.

### Verification and approval
- Independent High-risk code review **PASS** at `4558c76618567519204f676161967e722715169b` (PR #53 review `5916965055`).
- MEŽS Core GitHub Actions on that exact head: **SUCCESS**, run `43757137455`.
- **Independent final testing on that exact head was not run.** The owner explicitly overrode this High-risk workflow gate on 2026-11-16 and authorized merging PR #53. Earlier independent final-testing evidence applied only to an earlier commit; it is not claimed as current-head signoff.
- Owner-approved PR #53 merge: `0de3c60ed7f173c8c2bffc6aa5cd4ed44c34ec76`.

### Known limitations
- The Rich UI contract is a finite declarative subset, not arbitrary JavaScript/React/MDX execution. Adversarial independent final testing after the latest review remains unperformed under the owner's explicit override.

## [0.7.0] — 2026-11-15

### Added
- Shared `instructions.yaml` with a single `mezhs_default` text block referenced from all three default Agent policies.
- Automatic adjacent, Git-ignored personal `mezhs.local.yaml`, `agent.local.yaml`, and `instructions.local.yaml` copies on implicit startup; existing personal edits survive subsequent starts.
- Host operating-system substitution for `{OS}` when composing an Agent's initial prompt, without mutating stored policy snapshots.
- Canonical Agent tools and command-protocol reference at `docs/agent-tools.md`.

### Changed
- Honor explicit `--config` and `--mezhs-config` paths without creating or redirecting to implicit personal copies. Explicit Agent configurations can load an adjacent instruction sidecar when referenced.
- Resolve direct whole-field `{id}` instruction references while keeping ordinary literal braces inert; reject malformed, missing, duplicate and nested references with diagnostics.
- Update setup/configuration guidance, relevant Agent runtime specification, and release-stage reference-document maintenance policy. Keep version/changelog updates at release finalization only.

### Tests
- Independent Standard code review PASS at `097d5ef3934ac020351fdd162b9e1a1b9d9ec7b5`.
- Independent final testing PASS: Agent and solution Release builds, policy/prompt/negative-path regressions, clean standalone publish and two startup runs, personal edits preserved, explicit config and invalid-reference paths, API/command protocol, 78 frontend tests, concurrent execution/cancellation and restart/recovery. Evidence: PR #61, comment `6897003759`.
- Exact-head GitHub Actions MEŽS Core CI PASS: run `43724564043`.

### Known limitations
- On Windows PowerShell 5.1, independently executed policy and resilience scripts needed temporary UTF-8 BOM / HTTP status compatibility wrappers; the unchanged repository tests passed in the intended PowerShell CI environment.
- Codegraph regeneration was unavailable for this task. Browser provider sessions were covered through existing CI and controlled regressions rather than independently verified live authenticated sessions.

## [0.6.0] — 2026-11-15

### Added
- Transient Agent **Waiting** status (orange dot) while an Agent-owned model message is genuinely blocked by its browser integration's configured capacity or start-spacing gate; return to **Running** immediately upon admission.
- Dedicated per-send admission signals and exact Agent execution/message association, without persisting Waiting or introducing a second scheduler.
- Independent Core config flag `--mezhs-config <path>` for Agent API and startup validation of policy connection IDs against Core-owned connections.

### Changed
- Make `mezhs.yaml` the sole owner of Core connections, provider settings, integration workspaces/model defaults, and `maxParallel` / `minInterval` limits. Keep Agent-only listener, execution workspace, storage and policy overrides in `agent.yaml`; reject obsolete Core sections with a migration error.
- Compose normal Core API services and Agent features in a single Agent API host and shared `IntegrationRegistry`, preserving the standalone Core API and loopback-only Agent listener. Independent processes do not coordinate browser admission.
- Preserve explicit execution model, policy default and Core connection default precedence; preserve provider-default selection, existing-chat inheritance and connection overrides.
- Keep provider-reported activity, including `waiting`/thinking/progress, separate from admission-based Agent status, and preserve unread/terminal/cancellation status precedence.

### Tests
- Independent High-risk code review PASS at implementation `bc55e64fb6b94212712be193bec12f467b2b687f`.
- Independent testing PASS: 16 executed checks for config/DI, capacity and 5-second spacing, Agent/ordinary chat admission, status projection, cancellation/error/recovery, Chromium UI/accessibility, model precedence, file attachments, graceful shutdown and scoped builds. Full evidence and execution IDs: `specifications/implemented/integration-config-ownership-wait-status/verification.md`.
- GitHub Actions MEŽS Core CI passed for the reviewed branch head `f37adf1e509cff8c3a7cfd5025e537750467ed52` (run 43714317064).

### Known limitations
- Windows PowerShell 5.1 cannot execute the unmodified resilience script's `-SkipHttpErrorCheck`; equivalent assertions passed with a temporary compatibility wrapper. Negative-startup process termination was inconclusive in one harness, while direct missing-file loader rejection passed.
- Live authenticated/guest Electron provider sessions were not exercised during independent verification; browser admission and Agent status integration were validated using controlled providers and isolated Chromium UI.
- Browser admission sharing applies only within the owning API process; separately running Core and Agent API instances do not share gates. Asynchronous remote-provider cancellation remains best-effort.

## [0.5.1] — 2026-11-14

### Changed
- Compact Agent execution-status dots in the sidebar and selected-chat header reveal their text on hover or keyboard focus, with accessible labels/tooltips and reduced-motion support; remove the duplicate static header execution badge.
- Show completed Agent executions as unread (blue) until their conversation is opened (green). Track the last viewed root execution ID per chat in browser-local storage; automatically mark a completion read while its conversation is selected.
- Extend the existing batched Agent chat projection with the matching root execution ID without new per-chat status requests; preserve active/paused/terminal status precedence. Regular API Web remains unchanged.

### Tests
- Independent Standard code review and targeted verification PASS on PR #56 at `4c43b0897999072675cc7f81999d9852b9b67f3c`, including Chromium unread/focus checks, shared UI regressions, live Agent API smoke, frontend and .NET builds.
- MEŽS Core CI passed at the tested commit.

### Known limitations
- Read markers are local to each browser origin; they are not synchronized between devices or tabs, and concurrent tabs may overwrite the stored marker map.
- A related group-deletion Chromium test failed once in a parallel run, then passed in isolated runs, on the unchanged base, and in the final sequential suite; the intermittent cause was not established.
- Unread UI Chromium tests use mock API responses and reduced-motion mode; the live Agent API projection was verified separately.

## [0.5.0] — 2026-11-13

### Added
- Parallel local model sends for separate browser-backed ChatIds, with independent authenticated Electron windows inside one shared connection-owned host and fresh isolated guest sessions for each turn.
- Per-connection browser limits: `maxParallel` (default 1; 0 unlimited) and `minInterval` (milliseconds; default 0), enforced by a cancellation-aware admission gate.
- Accessible Agent execution status indicators in the sidebar and selected-chat header, backed by batched existing execution state.

### Changed
- Share account authentication without serializing entire model turns; deduplicate interactive login attempts and retain per-chat cancellation and progress routing.
- Retain an Agent's browser conversation context through model-to-shell-to-model cycles and release it at execution end; retire ordinary chat windows after sends.
- Configure ChatGPT Account for two concurrent local sends spaced by at least 5 seconds; explicitly retain 1/0 defaults for ChatGPT Guest, Gemini Guest and Grok Account.
- Rename the unreleased `minIntervalBetweenStartsMs` setting to `minInterval`; the old key is not accepted.

### Tests
- Independent high-risk review and automated testing of the implementation prior to the final configuration follow-up, followed by targeted limits, cancellation, Electron context, Agent lifetime and Release solution checks on the final candidate; CI passed on the final candidate.

### Known limitations
- Real authenticated concurrent provider responses, simultaneous different served models/efforts, and full Electron GUI/resource measurements were not independently verified.
- Browser cancellation stops local work best-effort; remote provider processing may continue. Dead Electron hosts are not automatically restarted.
- Existing account-level ChatGPT model-preference mutation remains until live provider evidence warrants changing it.

## [0.4.1] — 2026-11-13

### Changed
- Instruct MEŽS agents to prefer the internal GitHub tool when available for the task.
- Remove the overlapping GitHub-tool clause from general coding guidance while preserving local working-tree instructions.

No application runtime changes were made by this release.

## [0.4.0] — 2026-11-10

### Added
- Three-choice group deletion dialog in regular API Web: unassign conversations, permanently delete group conversations, or cancel, with the current group count and clear retention disclosures.
- Native accessible modal with keyboard focus management, request timeouts, pre-deletion membership reconfirmation, and best-effort reconciliation after uncertain server responses.

### Changed
- Reuse existing Core/Agent chat deletion and category removal APIs without changing Agent Web group deletion behavior or existing Agent/Core partial-failure semantics.
- Handle case-insensitive group identifiers consistently in filtering, counts, and deletion.

### Tests
- Add Chromium group-deletion regression scenarios for modal focus, changed membership, lost responses, interrupted requests, mixed-case IDs, and successful deletion.
- Extend shared UI regression checks. Developer CI passed; separate high-risk independent testing was explicitly waived by the owner.

### Known limitations
- Group membership validation is not atomic with ID-based deletion: a conversation moved to another group after the final preflight may still be deleted. This owner-accepted risk is documented in the implementation plan and current specification.

## [0.3.1] — 2026-11-10

### Changed
- Automatically select newly created groups in the regular API Web UI after successful creation and category refresh, using the server-returned category ID.
- Preserve Agent Web's existing group-creation selection behavior.

### Tests
- Update shared UI regression assertions for the regular API Web group-selection behavior.

## [0.3.0] — 2026-11-10

### Added
- Safe, bounded SVG rendering for model replies.
- Restricted interactive DIL questions with guarded host submission.
- Rich UI documentation, security regressions, and Chromium coverage.

### Changed
- Improved supported rich Markdown rendering while keeping unsupported markup inert.

## [0.2.1] — 2026-11-09

### Added
- Observed Agent activity-stage durations and shell command durations in downloadable debug logs, with timing interpretation guidance.
- Progress reporting for ChatGPT Account browser session acquisition, authentication, model selection, turn preparation, submission and final response retrieval.

### Changed
- Clarify Agent waiting and command-result fallback labels without changing submission, execution, connection serialization or persistence behavior.
- Extend ChatGPT Account progress regression coverage.

## [0.2.0] — 2026-11-07

### Added
- Agent and base Web chat management: search, categories/groups, renaming, activity information, and confirmed single/bulk deletion.
- Core SQLite chat, message, category, and remote-state persistence with one-time transactional import of existing JSON and JSONL history.

### Changed
- Simplify Agent chat deletion to request cancellation of Agent work and associated Executor shells, then delete Agent data followed by Core chat data in separate transactions without waiting for shell completion.
- Preserve Executor execution history, unrelated uploaded files, and legacy chat files retained as migration backups.
- Require task-branch-first planning, documentation, workflow, and implementation changes; keep `main` release-only.

### Fixed
- Chat/group UI synchronization and deletion disclosures, atomic SQLite message admission, orphaned legacy message-log migration handling, and successful staged attachments after deletion-guard removal.
- Windows PowerShell 5.1 API, architecture, and Executor lifecycle test compatibility.

## [0.1.4] — 2026-11-05

### Changed
- Start each next-role prompt with the task name and role; keep handoffs generic, minimal and free of prior-review steering.
- Have code reviewers correct issues and hand off to a fresh independent reviewer until PASS.
- Require initial feasibility, complexity, risk and conflict assessment; summarize proposed plans before committing and notify the owner before plan-review edits.
- Simplify development workflow instructions by removing duplicated handoff guidance.

No application runtime changes were made by this release.

## [0.1.3] — 2026-11-05

### Fixed
- Refresh local web library dependencies before Agent Web and Web UI builds, preventing stale installed copies from causing TypeScript export and component-prop errors.
- Preserve package lockfiles during build-time npm dependency reconciliation.

## [0.1.2] — 2026-11-05

### Fixed
- Isolated drafts across chats and agents; retained edits made during pending requests and restored prompts after failed submissions.
- Preserved user prompt numbering, whitespace and line breaks.
- Corrected scroll restoration, bottom-follow behavior and asynchronous conversation-history loading.
- Prevented stale chat requests from overwriting a different selected conversation.

### Added
- Conditional scroll-to-top and scroll-to-bottom controls.
- Safe rich-response rendering improvements for captions, inline titles and supported chart data.
- Chromium regression tests for drafts, scrolling, resizing, attachments and agent execution history.

## [0.1.1] — 2026-11-05

### Added
- Risk-based development workflow for Small, Standard and High-risk changes, with explicit owner approval before implementation merges.
- Agent guidance for planning, coding, code review, testing and release; task handoffs, final-specification tracking and a project-facing getting-started guide.

### Changed
- Review before final testing; allow one-chat handling for trivial changes and independent agents where risk warrants.
- Consolidated the agent entrypoint and engineering sanity checks; separated historical backlog from workflow instructions.
- Release versions and changelog entries are finalized only once when merging into `main`.

No application runtime changes were made by this release.
