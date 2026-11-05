# Code review

Review comes **before final testing**. Read [the workflow](workflow.md) for the level selected.

- **Small:** the development agent can review its own small diff and then verify it. Use an independent reviewer if a concern appears or the owner requests one.
- **Standard:** use a separate reviewer.
- **High-risk:** separate, independent review is mandatory. Involve a relevant specialist when useful.

Review the changed code against `main`, the final plan (or Small-change description), and the developer's checks.

- Check functional correctness, architecture, security, regressions, simplicity and maintainability. Use [software-change](skills/software-change/SKILL.md) for detailed judgment.
- Check that the **planned tests** cover requirements, errors, edge cases and changed dependencies. The reviewer does not claim tests passed without executing them.
- Verify changes to `specifications/current/` match real behavior. Check plan archiving and the proposed release summary. **Version and changelog edits must be absent until the merge into `main`.**
- Return **PASS / FAIL / BLOCKED**, with affected files, severity and actionable evidence.

On **FAIL**, the reviewer is responsible for correcting the issues on the **same branch**, running relevant checks, and committing/pushing the changes. Record the findings, changes and evidence in the PR or `verification.md`, tied to the corrected SHA. Then hand off to a **different, fresh independent reviewer**. The reviewer who made corrections cannot certify their own commit as an independent PASS. Repeat until the new reviewer finds no further issues and reports **PASS** on the exact reviewed SHA.

The next reviewer's prompt must use [the generic handoff format](workflow.md#handoffs-and-evidence) and must **not** mention prior findings, fixes, problem areas, hypotheses, or suggested focus. Historical evidence stays in the repository/PR for audit, not in the handoff prompt. If corrections require an unresolved owner decision, permission, or unavailable resource, report **BLOCKED** with evidence rather than guessing or passing.

On **PASS**, hand off to the tester/verifier (when required) using the same generic prompt format; the stage requirements remain in [testing.md](testing.md). **No reviewer PASS grants merge permission.**
