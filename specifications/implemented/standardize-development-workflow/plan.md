# Standardize the development workflow

- ID: `standardize-development-workflow`
- Type: documentation and workflow
- Date: 2026-11-04
- Status: implementation on review branch; awaiting independent testing and review

## Goal

Make planning, coding, testing, review and release easy to understand. Keep instructions reusable for other projects.

## Final behavior

1. Create a plan, using Grill Me where needed, and commit it to `main`.
2. Have a **new agent** review/update the plan. Repeat with fresh agents until there are no changes. The last reviewer gives a development prompt.
3. A **new developer agent** creates the plan-named branch and implements. Each fix is another new development chat on the same branch. The developer gives a testing prompt.
4. A **new tester agent** validates. Failure returns to a new development chat. Success provides the code-review prompt.
5. A **new reviewing agent** reviews. Problems return to a new developer and repeat testing/review.
6. Once tests and review pass, request Monkey's final review. Do not merge without explicit permission.
7. On approval, merge into `main`, release with updated version/changelog/README, tag and delete the merged branch.

When available, use Codegraph during source navigation. Refresh its generated dependency graph when code changes are complete and before testing/review. If it is stale after merge, fix that through a reviewed update.

Update and review `specifications/current/` when behavior changes. Do not store duplicate workflow or architecture documentation there.

## Documentation cleanup

- Original sanity questions, Codegraph instructions and coding guidelines remain in `.agents/coding.md`.
- Existing `.agents` TODO notes move to `docs/backlog/`; they are not agent instructions.
- The unused `CODEOWNERS` file and redundant planned-folder README and templates are removed. Planning outline lives in `.agents/specification.md`.

- A concise root `AGENTS.md` is the entrypoint.
- Stage instructions and reusable skills stay in `.agents/`.
- Current specifications describe actual behavior. Plans and archived changes are separate.
- MEŽS-specific architecture is in `docs/`; coding guidance is in `.agents/coding.md`.
- Root README describes the application, its features, prerequisites and how to use it.

## Remaining

- Independent testing/review.
- Monkey's approval to merge.
- Regenerate the committed Codegraph index if available.

No application runtime code was changed.