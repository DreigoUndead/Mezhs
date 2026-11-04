# Code review

Review comes **before final testing**. Read [the workflow](workflow.md) for the level selected.

- **Small:** the development agent can review its own small diff and then verify it. Use an independent reviewer if a concern appears or the owner requests one.
- **Standard:** use a separate reviewer.
- **High-risk:** separate, independent review is mandatory. Involve a relevant specialist when useful.

Review the changed code against `main`, the final plan (or Small-change description), and the developer's checks.

- Check functional correctness, architecture, security, regressions, simplicity and maintainability. Use [software-change](skills/software-change/SKILL.md) for detailed judgment.
- Check that the **planned tests** cover requirements, errors, edge cases and changed dependencies. The reviewer does not claim tests passed without executing them.
- Verify changes to `specifications/current/` match real behavior. Check plan archiving and relevant release metadata.
- Return **PASS / FAIL / BLOCKED**, with affected files, severity and actionable evidence.

On failure, return to development on the **same branch**. Retest and re-review changed areas. Handoff to a new chat when independence is required or context quality deteriorates.

On PASS, give testing/verification instructions:

> Role: final tester or verifier. Task: `<id>`; level: `<level>`; PR/branch: `<link>`; reviewed SHA: `<sha>`; final plan: `<path or PR summary>`; review evidence: `<link>`. Follow `.agents/testing.md`. Execute the required checks and assess any changed current specs. On failures return to development and affected review. On PASS, present the result for owner approval. **Do not merge without that approval.**
