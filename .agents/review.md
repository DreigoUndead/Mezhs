# Code review

**Start a fresh chat/agent.** The reviewer must be different from the developer and tester.

- Read the final plan, diff against `main`, and independent test evidence.
- Check behavior, regressions, simplicity, architecture, security and maintainability. Use [software-change](skills/software-change/SKILL.md) for detailed review.
- If `specifications/current/` changed, verify each change describes the **real resulting behavior**. Do not require unnecessary or duplicate specifications.
- Verify relevant release documentation and the final plan match the implementation.
- Return **PASS**, **FAIL** or **BLOCKED** with clear evidence.

On failure, give a **new development chat** a fix prompt. All subsequent testing and review use **new chats**. Do not fix implementation code as the reviewer.

On PASS, request owner review with:

```text
Branch / PR: <link>
Commit: <sha>
What changed: <short summary>
Testing: PASS (<evidence>)
Review: PASS (<evidence>)
Known limitations: <details or none>
Current specifications: <updated and reviewed, or not needed>
Proposed version: <version>
Please review. May this exact implementation be merged into main?
```

**Wait for explicit owner permission.** Reviewer approval never authorizes a merge.