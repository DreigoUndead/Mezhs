# Final testing and verification

Testing follows **code review** (or a Small-change self-review). Read [the workflow](workflow.md) for the required level.

- **Small:** the same development chat can run checks and pause for the owner's review.
- **Standard:** run appropriate automated and practical tests. Choose a separate tester when independence gives meaningful confidence; state why if skipped.
- **High-risk:** use a separate tester from the developer and reviewer. Independent testing is mandatory.

1. Check the branch/PR, **exact SHA**, final plan or Small-change summary, review outcome and changed `specifications/current/` entries.
2. Run acceptance, regression, failure-path and integration checks appropriate to the change. Record commands, exit codes, outcomes and limitations. CI, developer checks and independent testing are different kinds of evidence.
3. Record **PASS / FAIL / BLOCKED** with the tested SHA and base branch revision. Keep evidence in `verification.md` or the PR, so the next agent can check it.

On FAIL, the tester fixes issues on the same branch, runs checks, commits/pushes and prompts a **new independent Code Reviewer**; after review PASS, repeat affected tests with a fresh tester when required. On BLOCKED, report the blocker.

If local tooling is unavailable, prefer existing CI. When needed, a temporary validation workflow on the review branch may self-report a sanitized PASS/FAIL file. Remove the temporary workflow/result before owner review; do not include secrets. Never claim an unrun test passed.

On PASS, ask the **owner** to review the finished change, using:

```text
Task / level: <id> / <level>
PR and exact tested SHA: <link> / <sha>
Base revision: <sha>
Delivered behavior: <short summary>
Review: <PASS evidence or Small-change self-review>
Tests: <PASS evidence and untested areas>
Risks: <none or explanation>
Release proposal: <change summary and bump type; no pre-merge version/changelog edits>
May this exact implementation be merged into main?
```

**Pause for explicit permission.** The same chat may execute the release after approval. A new release chat is not mandatory. Before merging, recheck the PR head, `main`, conflicts and validity of prior checks. If implementation changes materially, repeat checks and obtain renewed owner approval.
