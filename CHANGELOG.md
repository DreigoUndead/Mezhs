# Changelog

This changelog records project releases. One approved implementation merge creates one release version. Earlier work is recorded in Git history.

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
