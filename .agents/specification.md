# Planning and task classification

Start by checking the existing code and relevant `specifications/current/` documents.

## Choose a level

- **Small:** limited scope and low regression risk (e.g. a typo or isolated CSS). One chat may do the change, self-review, run checks and pause for owner approval. Record a brief goal and verification note in the PR description or branch.
- **Standard:** normal feature or bug fix. Put the plan in `specifications/planned/<id>/plan.md` and commit it to `main` before starting the implementation branch. Independent review is the default; independent testing depends on risk.
- **High-risk:** architecture, security, concurrency, sensitive data or migrations. Put the plan on `main`; have a **separate agent** review the plan before implementation. Independent code review and testing are mandatory.

State the chosen level, why it fits, and intended checks before coding. Escalate if new risks appear. The owner can explicitly override the process, except that implementation merging always needs their **separate explicit approval**.

Use [Grill Me](skills/grill-me/SKILL.md) when requirements are unclear. Keep the plan proportional to the task:

```markdown
# <Change title>
ID: <kebab-case-id>

## Goal and scope
What changes and why? What is out of scope?

## Approach
Which component owns the behavior?

## Acceptance criteria
- [ ] Observable result

## Verification
How do we prove it works?
```

For complex work, add constraints, risks, alternatives or tasks **inside the plan**.

A plan reviewer checks the plan against the code, edits as needed, and repeats review until stable. Then provide a development prompt referring to the final plan. A fresh agent is required for the **High-risk independent plan review**, not each minor plan edit.

Plans are allowed to evolve in development. The final plan describes the result; Git contains the earlier drafts. Move it from `planned/` to `implemented/` on the development branch **before final review/testing**.
