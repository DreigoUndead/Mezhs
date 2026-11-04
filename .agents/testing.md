# Testing

Use a separate agent from the developer.

1. Read the final plan, acceptance criteria and changed code on the development branch.
2. Run relevant tests and reproduce important workflows. Include regressions and failure cases.
3. Record the commit, commands, results and anything not tested. Never call a test PASS if it was not run.
4. Report **PASS**, **FAIL** or **BLOCKED**.

If something fails, give the developer a clear fix prompt. After fixes, **test again**.

If everything passes, give the next agent this prompt:

> Review branch `<id>` at commit `<sha>` against `main`. Follow `.agents/review.md`. Read the final plan, full diff and testing evidence. Look for bugs, unnecessary complexity and missed requirements. Report PASS, FAIL or BLOCKED. If issues exist, send them to the developer. If not, prepare a short approval summary for the project owner.