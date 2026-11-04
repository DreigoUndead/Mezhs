# Independent testing

Use a **fresh chat/agent** for each testing attempt, separate from the developer.

1. Read the branch, final plan, acceptance criteria and actual code. Check relevant `specifications/current/` changes.
2. Execute real acceptance and regression tests. Distinguish mock/compilation evidence from live system behavior.
3. Record commit SHA, commands, exit codes, outcomes, limitations and untested areas.
4. Report **PASS / FAIL / BLOCKED**.

On failure, give a self-contained fix prompt to a **new developer agent**. After the fix, another fresh tester repeats the tests.

If local tooling is unavailable, use CI on the same review branch where possible rather than claiming tests passed. If workflow logs are inaccessible, a **temporary** GitHub Actions workflow can run the checks and commit a sanitized PASS/FAIL result file with `[skip ci]`. Remove the temporary workflow and result file before final review; never commit secrets. Do not treat an unavailable CI listing as proof no job ran.

On PASS, give this prompt to a **new reviewing agent**:

> Review branch `<id>` at `<sha>` against `main`. Follow `.agents/review.md`. Read the final plan, full diff, test evidence and changed current specifications. Check correctness, design, security, regressions and release files. Return PASS, FAIL or BLOCKED. On failure, write a fix prompt for a new developer agent. On success, prepare an owner-review summary.