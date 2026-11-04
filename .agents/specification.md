# Planning

- Read the existing code and [current specifications](../specifications/current/) first.
- Every change starts with `specifications/planned/<id>/plan.md`. Use [the template](../specifications/templates/change.md).
- For complex features, use [Grill Me](skills/grill-me/SKILL.md) to resolve important decisions one question at a time.
- Write the goal, scope, approach and testable acceptance criteria. Keep small changes short.
- Commit the plan to `main` (through a PR when required). This is a documentation change, not a release.
- Give an independent agent this prompt:

> Review `specifications/planned/<id>/plan.md` against the existing code and current specifications. Fix unclear or incorrect parts. Commit any plan changes to `main`. Repeat until there are no changes. Then give a developer a prompt with the plan path, required behavior and acceptance criteria. Do not implement code.

Plans may change during development. Keep the final document aligned with actual behavior. Git history already records earlier ideas.