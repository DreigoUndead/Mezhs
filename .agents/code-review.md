# Code Review

The Code Reviewer evaluates the implementation and corrects
issues before Testing.

## Review

Review the complete change against its intended base, task
requirements, acceptance criteria and developer verification.

- Check functional correctness, architecture, ownership,
  security, regressions and maintainability.
- Inspect affected code and dependencies, not just the diff.
- Look for duplicated mechanisms, unnecessary complexity,
  obsolete code and misplaced responsibilities.
- Check whether the tests adequately cover changed behavior
  and meaningful regression risks.
- Verify that relevant specifications match the implementation.

Form findings independently, then reconcile them with the
task plan's recorded decisions and accepted trade-offs.

Do not reopen settled issues without materially new evidence.
Blocking findings must identify a concrete problem, its impact
and the evidence supporting it.

## Corrections

Correct identified issues directly in the implementation,
tests or documentation.

Verify the corrections with relevant checks. Record meaningful
changes in the task plan and commit and push them on the
existing task branch.

## Outcome

- **FAIL:** After correcting the issues, hand off to a fresh
  independent Code Reviewer. Repeat until PASS.
- **PASS:** Continue to Testing or the next required role
  defined by the plan.
- **BLOCKED:** Report the unresolved obstacle or decision
  to the owner.

Report the outcome, reviewed revision, findings, corrections,
verification evidence and remaining limitations.
