# Standardize MEŽS development workflow

- ID: `standardize-development-workflow`
- Type: maintenance / documentation / governance
- Date: 2026-11-04
- Status: implemented on review branch; independent testing, review and Monkey merge approval pending
- Bootstrap: Monkey requested implementation while the new plan-first process did not yet exist. This migration alone permits plan and implementation in one PR.

## Desired result

Multiple contributors and MEŽS agents use the repository, not private chat history, to plan, code, test, review and release changes.

## Scope and approach

- Replace duplicate `.agents/README.md` with one root `AGENTS.md`.
- Split instructions into specification, coding, testing, review, workflow and release; preserve existing engineering/debugging skills, Codegraph rules, and historical references.
- Require approved plan PR before separate implementation branch, with evolving final specification.
- Produce independent testing and code-review handoff prompts; Monkey explicitly approves **every completed implementation merge**.
- Keep planned/implemented/current specification states and minimal templates.
- `main` is releasable; each implementation merge advances `VERSION`, `CHANGELOG.md`, and README; plan-only merges do not.
- Clean up existing documentation references; move architecture detail out of root README. No runtime behavior refactors.

## Acceptance

- [x] Single concise entrypoint and separate stage-specific guidance
- [x] Workflow, skill, templates and current-specification baseline
- [x] Release documentation prepared on implementation branch
- [x] Old entrypoint and obsolete references cleaned up
- [ ] Independent testing and review of candidate
- [ ] Monkey approves complete PR; merge and tag release

## Verification plan

Inspect all new links, reference consistency, PR diff, Python governance check, main-branch CI and full code regression suite. No app/runtime changes are intended.
