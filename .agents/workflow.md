# Development workflow

Use the smallest safe process. **`main` is release-only. Create or reuse a task branch before the first plan, documentation, workflow, code or test commit. No direct commits to `main` during planning, development, review or testing. The project owner approves every implementation merge into `main`.**

## 1. [Choose the level](specification.md)

State the level and reason **before work starts**. Tell the owner; they may override the process. Reassess if the change grows.

| Level | Examples | Default process |
| --- | --- | --- |
| **Small** | Typo, isolated CSS, minor documentation | Task branch first; one chat can plan, change, check and pause for owner review. No mandatory extra agents or plan-only commit. |
| **Standard** | Most features and bug fixes | Task branch first, plan and implementation on that branch, independent code review, and targeted verification. Use a separate tester when independence adds value. |
| **High-risk** | Security, architecture, concurrency, migrations, sensitive data | Task branch first, plan and independent plan review on that branch; independent code review **and** independent testing. |

Do not downgrade high-risk work simply to save time. Record a reason for skipped checks or an owner override. The owner may explicitly change the process; **merge permission is always separate**.

**Context quality:** A new agent is useful when the current one loses track of decisions, repeats investigation, confuses versions or produces weaker reasoning. This is a judgment call, **not a fixed token threshold**. For a Small change, staying in the same chat is fine if the work remains clear.

### Mandatory complexity escalation (all stages)

During planning, development, plan/code review, testing and debugging, **stop the affected work and ask the owner before adding complexity** when a requirement seems disproportionate to its value, an initially simple feature needs unexpected coordination or persistent safeguards, a simpler approach exists if a guarantee is relaxed, or fixes repeatedly address consequences of the same design choice. **After two fixes driven by the same architectural decision, reassess the design before another fix.** An approved plan does not waive this rule.

Report concisely: **what** became complex, **which requirement causes it**, **simpler options**, **trade-offs**, and **your recommendation**. Require an explicit owner decision to retain the guarantee, relax it, redesign or stop; record that decision in the task plan or PR before resuming. Do not escalate ordinary implementation details without meaningful maintenance or architectural impact.

## 2. [Plan](specification.md)

- **All levels:** create a task branch named `<id>` from the current `main` **before the first change or commit**, including a plan or workflow change. Reuse the branch for revisions and fixes. If a task depends on unmerged work, explicitly base its branch on the dependent task branch and keep the relationship visible in the PR.
- **Small:** a brief plan in the task branch or PR description is enough.
- **Standard / High-risk:** create and commit `specifications/planned/<id>/plan.md` **on the task branch**, never directly on `main`. Plan reviews and corrections occur on that branch.
- Use Grill Me when needed. Review and refine the plan; independent plan review is required for High-risk changes, optional for Standard.
- When ready, hand off to development with a reference to the plan. A new chat is used **when an independent stage or context reset is needed**.

## 3. [Code](coding.md)

- Continue on the existing task branch `<id>` created before planning. Reuse it for fixes; do not create a second development branch merely because the plan was approved.
- Implement and run developer checks. Plans may change as code evolves.
- **Before independent code review/testing**, move any completed plan from `planned/` to `implemented/` **on the task branch**. No plan is copied or committed to `main` before release.
- Update `specifications/current/` only if behavior changed. Review these updates with the implementation.
- Use Codegraph where helpful; refresh the generated graph after relevant code changes, before review.
- Prepare a short release summary and proposed change type (major/minor/patch), then check the branch diff. **Do not edit the version or changelog in the development branch.**

## 4. [Review](review.md)

- Review the implementation **before final testing**. The reviewer also checks planned test coverage.
- **Small:** a careful same-chat self-review may be enough.
- **Standard:** use an independent reviewer.
- **High-risk:** independent review is mandatory.

## 5. [Test](testing.md)

- Run the checks appropriate to the level and risks. Developer checks and CI are not the same as independent testing.
- **Small:** same-chat checks and relevant CI can be sufficient.
- **Standard:** targeted tests; an independent tester when meaningful.
- **High-risk:** independent testing is mandatory.

## 6. [Owner approval and release](release.md)

- Report what changed, test/review evidence, remaining risks, branch/PR and exact commit.
- **Pause for the project owner's explicit approval**. Small changes may stay in one chat through this pause. The final tester can perform release after approval; a separate release agent is optional.
- Before merging, recheck the candidate commit and current `main`. Changed implementation or conflicts may require new review, tests and **renewed owner approval**.
- After approval, merge the completed task (including its final plan and workflow/documentation changes) into `main` **as part of the release**. **At merge finalization, update the version and changelog exactly once for the whole task**, then tag the resulting release commit and delete the merged branch. No direct plan/workflow commits to `main`, and no version/changelog edits during planning, development, review or testing.

### Handoffs and evidence

Every next-role prompt must start with `<Task name> — <Role>` on its **first line**, which MEŽS uses as the chat title. Keep prompts generic by default: include only task-specific references needed to locate the work, without repeating instructions already in `AGENTS.md` or `.agents/`. For independent code reviewers, do not include earlier findings, fixes, or suggested areas of focus.

A new chat is required for independent checks, not for every edit. Request a fresh chat if context quality becomes doubtful. **No agent PASS or plan approval grants merge permission.**
