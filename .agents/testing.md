# Testing

**Start a fresh chat/agent for each testing attempt.** Do not reuse the developer's chat or the previous tester's chat.

1. Read the final plan, acceptance criteria, current behavior specs (if relevant) and the actual changed code.
2. Test the implementation and regressions. Use the strongest practical evidence, not developer claims.
3. Record the branch, commit SHA, commands, results and untested areas.
4. Return **PASS**, **FAIL** or **BLOCKED**.

On FAIL/BLOCKED, produce a **self-contained fix prompt for a new development chat**. Include how to reproduce the problem. After fixes, a **different new testing chat** must test again.

On PASS, give this prompt to a **new review chat**:

> Review branch `<id>` at `<sha>` against `main`. Follow `.agents/review.md`. Read the final plan, full diff, test evidence, and any changes to `specifications/current/`. Check correctness, design, security, regressions and release documentation. Return PASS, FAIL or BLOCKED. On failure, give a fix prompt for a new development chat; on success, prepare a short owner-review summary.

Never describe tests you did not run as passed.