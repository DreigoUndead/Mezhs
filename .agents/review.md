# Code review

Review comes **before final testing**. Read [the workflow](workflow.md) for the level selected.

- **Small:** the development agent can review its own small diff and then verify it. Use an independent reviewer if a concern appears or the owner requests one.
- **Standard:** use a separate reviewer.
- **High-risk:** separate, independent review is mandatory. Involve a relevant specialist when useful.

Review the changed code against `main`, the final plan (or Small-change description), and the developer's checks.

- Check functional correctness, architecture, security, regressions, simplicity and maintainability. Use [software-change](skills/software-change/SKILL.md) for detailed judgment.
- For architecture-affecting changes, inspect the actual code and relevant recent changes for cumulative coupling, duplicated mechanisms, or drifting responsibilities. Report concrete evidence, not speculative concerns.
- Check that the **planned tests** cover requirements, errors, edge cases and changed dependencies. The reviewer does not claim tests passed without executing them.
- Verify changes to `specifications/current/` match real behavior. Check plan archiving and the proposed release summary. **Version and changelog edits must be absent until the merge into `main`.**
- Return **PASS / FAIL / BLOCKED**, with affected files, severity and actionable evidence.

On FAIL, the current reviewer fixes issues on the **same branch**, checks and commits/pushes, then prompts a **new independent Code Reviewer**. Repeat until PASS. On BLOCKED, report the blocker.

On PASS, hand off to testing/verification as required by [testing.md](testing.md).
