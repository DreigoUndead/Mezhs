# Specification

Defines the format and maintenance rules for task plans and specifications.

## Task plan format

Keep the document proportional to the task. Include the information
the next role needs to understand and execute the work.

```markdown
# [Task title]

ID: [task-id]
Branch: [task-branch]
Level: [Small / Standard / High-risk]

## What

[Scope, constraints and exclusions.]

## Why

[Problem being solved and intended outcome.]

## Acceptance criteria

- [ ] [Observable result]

## How

[Approach, important architectural decisions and responsibilities.]

## Steps

1. **[Role]** — [Work and expected deliverable]
   - Dependencies: [If any]
   - Done when: [Observable completion condition]

## Verification

[Required checks, including live testing where applicable.]

## Changes

[Changes and new decisions made after the initial plan.]
```

Include only the roles and steps required for the task.

Include affected files, interfaces, assumptions, risks, migration steps,
rollback instructions or exact verification commands only when necessary
for correct execution.

Define important decisions; leave ordinary implementation details
to the Developer.

## Updating the specification

Keep the main sections consistent with the current accepted
requirements, approach and implementation.

Record each meaningful addition or change in the `Changes` section,
including changes to scope, approach, role steps, verification,
accepted limitations or decisions.

Ordinary wording and formatting corrections do not need entries.

Use a concise entry:

```markdown
### [Date] — [Change title]

**Change:** [What changed]
**Reason:** [Why it changed]
**Authority:** [Who decided or approved it, with reference]
**Trade-off:** [Accepted consequence, if any]
**Supersedes:** [Earlier decision, if any]
```

Omit fields that do not apply.

## Decision continuity

- Keep the active requirements in the main sections of the plan.
- Preserve the rationale for superseded decisions in `Changes`.
- Distinguish proposed changes from accepted decisions.
- Do not silently reverse an accepted decision or reintroduce a
  previously rejected approach.
- Reconsider a decision when materially new evidence or requirements
  justify it. Identify what changed.
- Obtain owner approval before changing an owner-approved requirement,
  guarantee or consequential architectural decision.

The plan remains the source of truth for its task. Avoid duplicating
its decisions across multiple documents or turning its Changes
section into a transcript of ordinary implementation work.
