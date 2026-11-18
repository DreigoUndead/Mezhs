# Plan Review

The Plan Reviewer independently evaluates the initial task plan before development.

Read the plan, requirements, relevant source code, specifications and recorded decisions.

Verify that:

- Scope and acceptance criteria are clear and consistent.
- The approach fits the existing architecture.
- Steps, roles and dependencies form a workable execution plan.
- Verification covers the intended behavior and relevant risks.
- The plan does not introduce unnecessary complexity or guarantees.

Form findings independently, then reconcile them with recorded decisions and accepted trade-offs.

## Corrections

Correct identified issues in the existing plan when no owner decision is required.

If a correction would change an owner-approved requirement, decision or accepted trade-off, obtain the owner's decision before applying it.

Follow [Specification Maintenance](specification.md) when recording changes.

## Outcome

- **FAIL:** Commit the corrected plan and hand off to a fresh independent Plan Reviewer. Repeat until PASS.
- **PASS:** Hand off to the Developer role or roles defined by the plan.
- **BLOCKED:** Report the unresolved decision or obstacle to the owner.

Follow [Handoff](handoff.md) when transferring work. Include the current plan path and revision.
