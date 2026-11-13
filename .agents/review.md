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


## Findings, decisions, and review continuity

- **Review independently, then reconcile.** Form findings from the current diff and actual requirements first. Before marking them blocking, compare them against the task plan's recorded findings, fixes, owner decisions, and accepted trade-offs. Do not rediscover and re-block an already resolved or explicitly accepted concern without materially new evidence.
- **Keep the implemented plan current.** Record review findings, their fixes or dispositions, and agreed trade-offs in the task's plan file as the ongoing source of truth. Include enough evidence or commit references to explain why a concern was fixed, accepted, or rejected. Subsequent reviewers use this record to distinguish new problems from settled decisions.
- **Do not loop back to plan review for review bookkeeping.** Updating the plan with findings, fixes, or accepted limitations does **not** restart the plan-review stage. Continue with code re-review when code changes, then the required independent testing. If a finding would materially change approved requirements or architecture, escalate the proposed change to the owner under the existing workflow's complexity rule rather than silently restarting plan review.
- **Keep severity proportional.** A plausible timing race or crash scenario is not automatically a blocker. State the violated approved guarantee or security boundary, concrete user impact, likelihood, existing recovery path, and cost of the safeguard. Escalate proposed stronger guarantees instead of quietly requiring them.

On FAIL, the current reviewer fixes issues on the **same branch**, checks and commits/pushes, then prompts a **new independent Code Reviewer**. Repeat until PASS. On BLOCKED, report the blocker.

On PASS, hand off to testing/verification as required by [testing.md](testing.md).
