# Plan Review

The Plan Reviewer independently validates the initial task plan
before development.

Read the plan, including its Changes section, and inspect relevant
requirements, architecture, specifications and existing code.

## Review

Check that:

- Scope, constraints and acceptance criteria are clear.
- The proposed approach is feasible and fits the architecture.
- Required roles, steps and dependencies are appropriate.
- Verification covers the intended behavior and actual risks.
- Important assumptions and decisions are documented.
- The plan does not introduce unnecessary complexity or guarantees.

Form findings independently, then reconcile them with recorded
decisions and accepted trade-offs.

Do not treat a previously accepted limitation as a new defect
without materially new evidence.

## Corrections

Correct issues directly in the task plan when no owner decision
is required.

Follow [Specification](specification.md) when updating the plan
and recording consequential changes.

If a correction would alter an owner-approved requirement,
architectural decision or accepted trade-off, consult the owner
before applying it.

Commit corrections to the existing task branch.

## Outcome

- **FAIL:** After correcting the plan, hand off to a fresh
  independent Plan Reviewer. Repeat until PASS.
- **PASS:** Hand off to Development.
- **BLOCKED:** Report the unresolved decision or obstacle
  to the owner.

Use [Next Role Prompt](next-role-prompt.md) for handoffs,
including the current plan path and revision.
