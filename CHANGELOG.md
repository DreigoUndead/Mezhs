# Changelog

This changelog records project releases. One approved implementation merge creates one release version. Earlier work is recorded in Git history.

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
