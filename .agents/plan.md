# Planning

The Planner turns a development request into a scoped, executable plan.

## Understand the task

- Identify the goal, requirements, constraints and acceptance criteria.
- Inspect relevant existing code, architecture and specifications.
- Identify affected areas, dependencies, risks and possible regressions.
- Resolve consequential conflicts or decisions with the owner.
- Use [Grill Me](skills/grill-me/SKILL.md) when requirements are unclear.
  Do not ask questions the existing code or specifications can answer.

## Select the workflow

Choose the smallest process suitable for the task.

- **Small:** Isolated, low-risk changes such as labels, CSS, typos or
  minor documentation. A brief task description is sufficient.
  One chat may perform all necessary roles.
- **Standard:** Features, bug fixes and moderate refactoring.
  Written plan, independent Code Review and targeted verification.
  Add independent Testing or Plan Review when justified.
- **High-risk:** Security, architecture, concurrency, migrations or
  sensitive data. Written plan, independent Plan Review before
  development, independent Code Review and independent Testing.

Evaluate the need for each role based on actual complexity and impact.

For larger tasks, define multiple sequential Development, Code Review
or Testing steps when useful. Keep one active agent/chat per task.

Present the initial assessment, selected workflow level and intended
verification to the owner.

## Create the plan

Create or reuse a task-named branch before making repository changes.

Base a new branch on current `main`, or document any dependency on
another unmerged branch.

For Standard and High-risk tasks, create:

`specifications/planned/<id>/plan.md`

Follow [Specification](specification.md) for the document format.

Before committing the initial plan, present the proposed scope,
approach and consequential decisions to the owner. Resolve decisions
that require owner approval.

Keep the plan proportional to the task. Commit it on the task branch.

## Outcome

- **Complete, Plan Review required:** Hand off to an independent
  Plan Reviewer.
- **Complete, Plan Review not required:** Proceed to Development.
- **Blocked:** Report the unresolved decision or obstacle to the owner.

Follow [Next Role Prompt](next-role-prompt.md) when a new chat
is required.
