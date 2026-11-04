# Standardize the development workflow

- ID: `standardize-development-workflow`
- Type: documentation and workflow
- Date: 2026-11-04
- Status: implementation ready for independent testing and review

## Goal

Make planning, implementation, testing, review and releases clear to developers and AI agents. Keep instructions short and reusable across projects.

## Final behavior

1. Make a plan. Use Grill Me when needed.
2. Commit the plan to `main`. Another agent reviews and updates the plan until no changes remain. It then provides a development prompt.
3. A developer creates branch `<id>`, implements the plan, keeps it current, and provides a testing prompt.
4. An independent agent tests. Any failures return to development. Once tests pass, it provides a code-review prompt.
5. An independent agent reviews. Issues return to development and testing.
6. With tests and review passing, request Monkey's review. **Wait for explicit merge permission.**
7. Each approved implementation merge into `main` creates a new release; update the changelog, README, version and current specifications.

## Delivered

- Single root `AGENTS.md`; removed old duplicate agent README.
- Split and shortened reusable instructions and prompts.
- Retained existing engineering skills; moved project-specific coding facts to current specifications.
- Added living/current specifications, historical plan, templates, release files and PR guidance.
- Preserved the existing runtime architecture documentation.

## Remaining

- Independent testing and review of the branch.
- Monkey's final review and explicit merge approval.
- Refresh the generated Codegraph index where tooling is available.

## Scope

Documentation and workflow changes only. No runtime refactors.