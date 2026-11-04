# Planning

- Inspect existing code and relevant `specifications/current/` documents.
- Use [Grill Me](skills/grill-me/SKILL.md) for unclear or complex design decisions.
- Create `specifications/planned/<id>/plan.md` using this outline. Keep it short for small changes:

```markdown
# <Change title>
ID: <kebab-case-id>

## Goal
What problem are we solving? What should users observe?

## Scope and approach
What changes, what stays unchanged, and which components own it?

## Acceptance criteria
- [ ] Observable result

## Verification
How will the result be tested?

## Risks or open questions
Only meaningful unresolved concerns.
```

- For major features, add sections for behavior, constraints, alternatives and tasks **inside the plan**, as needed. Do not create separate template or instructions files for each variation.
- Commit the plan to `main` (through a PR if required).
- Hand this to a **new agent**:

> Review `specifications/planned/<id>/plan.md` against existing code and current specifications. Correct unclear or incorrect parts, then commit any plan changes to `main` following repository rules. If anything changes, write a self-contained plan-review prompt for a fresh agent. If nothing needs changing, write a development prompt with the plan path, goal and acceptance criteria. Do not implement code.

Plans can evolve during development. The final version describes the delivered behavior; Git already holds the earlier revisions.