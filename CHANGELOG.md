# Changelog

This changelog records project releases. One approved implementation merge creates one release version. Earlier work is recorded in Git history.

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
