# Specification Maintenance

This file defines how to update existing task plans and specifications.
It applies to every role that changes them.

## Keep specifications current

- Update the existing document when implementation, requirements, workflow steps or accepted decisions change.
- Keep the main content consistent with the current intended result.
- Do not preserve obsolete requirements as though they remain active.
- Do not restart Planning or Plan Review merely because a document changed.

## Record decisions

Record consequential changes to architecture, requirements, guarantees, workflow or accepted limitations.

Use a concise decision record:

### D-01 — [Title]

**Decision:** [What was decided]
**Reason:** [Why]
**Trade-off:** [Accepted consequences or limitations]
**Authority:** [Who decided, with evidence or reference]
**Supersedes:** [Previous decision, if any]

Omit fields that do not apply.

Do not record ordinary edits, minor corrections or every review finding as separate decisions.

## Preserve decision history

- Update the main specification to reflect the latest accepted decision.
- Preserve the rationale for superseded decisions.
- Reference the replacement decision rather than silently deleting history.
- Distinguish proposed changes from accepted decisions.
- Do not silently reverse accepted decisions.
- If new evidence challenges an existing decision, identify what changed and escalate when the decision requires owner authority.

## Consistency

Keep relevant task plans and current specifications consistent with the actual implementation.

Use the existing task document as the source of truth. Avoid copying the same decisions into multiple documents unless they describe different responsibilities.

Ensure the next role can identify the current requirements, accepted limitations and reasons for consequential changes.
