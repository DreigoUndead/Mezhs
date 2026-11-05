# Planning and task classification

Start by checking the user's request, existing code, relevant `specifications/current/` documents, and recorded owner decisions. **The initial planner must assess and tell the owner** whether the request is feasible, its likely complexity, material risks, and any contradictions with existing behavior, architecture, scope, or prior decisions. Distinguish evidence from assumptions, identify blockers or dependencies, and resolve material conflicts with the owner instead of silently proposing an infeasible or incompatible plan.

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

**Before committing a proposed plan to `main`**, the planner must present the owner with a concise summary of the goal/scope, intended approach, significant decisions or tradeoffs, risks, acceptance/verification strategy, and unresolved questions. Invite corrections and wait for **explicit owner approval** before the plan commit. If the plan materially changes before committing, present the updated summary first.

A plan reviewer checks the plan against the code and reports findings. **Before modifying any plan**, regardless of its Grill Me history, the reviewer must inform the owner of the concrete issue, intended change and reason, and obtain **explicit approval for that change**. For previously owner-agreed decisions or plans that went through Grill Me, first discuss each meaningful issue using [Grill Me](skills/grill-me/SKILL.md), one decision at a time, with evidence, options and a recommendation. Keep undecided findings outside the plan; never silently overwrite a decision. After approved corrections, repeat the necessary review and provide a generic next-role prompt as defined in [the workflow](workflow.md). A fresh agent is required for the **High-risk independent plan review**, not for each authorized plan edit.

Plans are allowed to evolve in development. The final plan describes the result; Git contains the earlier drafts. Move it from `planned/` to `implemented/` on the development branch **before final review/testing**.
