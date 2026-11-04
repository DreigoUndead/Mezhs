# Changelog

This changelog records project releases. One approved implementation merge creates one release version. Earlier work is recorded in Git history.

## [0.1.1] — 2026-11-05

### Added
- Risk-based development workflow for Small, Standard and High-risk changes, with explicit owner approval before implementation merges.
- Agent guidance for planning, coding, code review, testing and release; task handoffs, final-specification tracking and a project-facing getting-started guide.

### Changed
- Review before final testing; allow one-chat handling for trivial changes and independent agents where risk warrants.
- Consolidated the agent entrypoint and engineering sanity checks; separated historical backlog from workflow instructions.
- Release versions and changelog entries are finalized only once when merging into `main`.

No application runtime changes were made by this release.
