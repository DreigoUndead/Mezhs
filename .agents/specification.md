# Planning

- Inspect existing behavior and relevant [current specifications](../specifications/current/) before proposing a change.
- Create `specifications/planned/<id>/plan.md` using [the template](../specifications/templates/change.md).
- Use [Grill Me](skills/grill-me/SKILL.md) for unclear or complex decisions.
- Write the purpose, scope, approach and testable acceptance criteria. Keep small changes short.
- Commit the plan to `main` (through a PR if required).
- Give this prompt to a **new chat/agent**:

> Review `specifications/planned/<id>/plan.md` against the current code and specifications. Fix unclear or incorrect parts. Commit plan changes to `main` as allowed by repository rules. If anything changed, give a new plan-review prompt for another chat. If no changes are needed, give a development prompt with the plan path and acceptance criteria. Do not implement code.

A plan can change during development. The final plan describes the implemented behavior. Git holds the older versions.