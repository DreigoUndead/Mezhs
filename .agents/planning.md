# Planning

The Planner defines the task's scope and execution plan.

## Workflow level

Choose the appropriate level for the task.

| Level | Typical changes | Default process |
| --- | --- | --- |
| **Small** | Labels, isolated CSS, typos, minor documentation | One chat can perform all required roles. No separate plan file or independent review required. |
| **Standard** | Most features, bug fixes and moderate refactoring | Written plan, independent Code Review and targeted verification. Additional independent roles when justified. |
| **High-risk** | Security, architecture, concurrency, migrations, sensitive data | Written plan, independent initial Plan Review before development, Code Review and Testing. |

Assess feasibility and risks before planning, and present the initial assessment to the owner. State the selected level, rationale and intended verification before development.

## Task branch

Create or reuse the task branch before the first repository change, including the plan itself.

Base it on current `main`, or document its dependency on another unmerged task branch.

## Task plan

For Small changes, a brief task description or PR summary is sufficient.

For Standard and High-risk changes, create and commit `specifications/planned/<id>/plan.md` on the task branch before implementation.

The plan defines the scope, approach, execution steps, required roles, dependencies and verification. Keep it proportional to the task.

Before committing the initial plan, present the proposed scope, approach and consequential decisions to the owner.

```markdown
# [Task title]

ID: [task-id]

## Goal and scope
[What should change and what is excluded.]

## Acceptance criteria
- [ ] [Observable result]

## Approach
[Important design decisions and responsibilities.]

## Workflow
Level: [Small / Standard / High-risk]
Roles: [Required roles and independence]

## Steps
1. [Step name] — [Responsible role]
   - Deliverable: [Expected result]
   - Dependencies: [If any]
   - Done when: [Completion condition]

## Verification
[Required checks, including live testing where applicable.]

## Decisions and risks
[Only when relevant.]
```

For larger tasks, define independent workstreams when they can be developed in parallel. Assign their roles, dependencies and integration steps.

Include additional technical details, constraints, risks or rollout instructions only when needed. Do not turn the plan into a transcript of the implementation.

For changes to an existing plan, follow [Specification Maintenance](specification.md).

Use [Grill Me](skills/grill-me/SKILL.md) when requirements need clarification.
