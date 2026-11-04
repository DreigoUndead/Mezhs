# MEŽS change workflow

**Authoritative process.** Applies to code, tests, configuration, documentation and agent instructions. The [repository-change-workflow skill](skills/repository-change-workflow/SKILL.md) invokes this process; it is not a second source of rules.

## Identity and branches

Every change receives a lowercase kebab-case ID, e.g. `sandbox-executor-routing`.

1. **Plan:** `plan/<id>` adds `specifications/planned/<id>/plan.md` (and optional `spec.md` / `tasks.md`). Monkey explicitly approves the plan PR; merge it into `main` before coding. No product-version bump for plan-only merges.
2. **Coding:** branch `<id>` from the latest `main` containing that plan. Keep all fixes and review revisions on this one implementation branch. Update plans as the design evolves; the final plan reflects actual functionality.
3. **Prepare release candidate:** in the implementation PR, move the plan to `specifications/implemented/<id>/`, update affected `specifications/current/`, add `verification.md`, update `VERSION`, `CHANGELOG.md` and root `README.md`. Review/test this content before merge.
4. **Independent testing:** a fresh MEŽS testing agent follows [testing](testing.md), validates the exact candidate against the *final* spec and returns PASS/FAIL/BLOCKED and a populated review prompt on PASS.
5. **Independent code review:** a fresh MEŽS reviewer follows [review](review.md), examines the complete diff and evidence, returns PASS/FAIL/BLOCKED and a Monkey approval package on PASS.
6. **Monkey review:** Monkey explicitly approves or rejects the **completed, identified PR/candidate**. No green checks, agent PASS, original task, or silence imply approval.
7. **Release:** after approval, verify PR head/checks, merge to `main`, verify release state and tag `v<version>`. `main` remains releasable.

## Failed checks and changing requirements

- FAIL/BLOCKED means **stop**. Return findings to coding on the same branch; re-test/re-review what changed. No reviewer silently repairs production code.
- Architecture, approach, and implementation details may evolve freely; keep the final specification current. Raise a change of core product purpose, security boundary or unrelated feature to Monkey.
- Evidence references a commit SHA. An evidence-only update to `verification.md` may advance the SHA without invalidating tests of unchanged code; revalidate substantive behavior/config/release changes.
- Separate unrelated changes into separate plans. Never edit `main` directly.
- A plan-only approval is not implementation merge authorization.

## One-time bootstrap

The change `standardize-development-workflow` is explicitly allowed to combine its initial plan and implementation in one review PR because the two-branch process did not exist before adoption. **Monkey still must approve this implementation merge.**