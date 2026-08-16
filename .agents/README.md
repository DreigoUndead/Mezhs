# Repository-local engineering skills

These skills are intentionally small and composable.

- `repository-change-workflow` — mandatory workflow gate for every repository modification. Each reviewable task stays on one branch and may be merged only after the user reviews the completed change and explicitly approves the merge.
- `software-change` — engineering/design judgment for non-trivial implementation, refactoring, architecture work, and code review. It continuously challenges ownership, duplication, regression risk, necessity, complexity, abstraction timing, maintainability, interface choice, and elegance, then chooses Reuse / Repair / Refactor / Redesign.
- `systematic-debugging` — evidence-first root-cause investigation for bugs and unexpected behavior. Once the cause is established, it hands the scope decision back to `software-change`.

There is deliberately no eFlex-specific skill in this repository. eFlex was used as evidence for the general reasoning principles, but Mezhs should acquire project-specific guidance only when its own architecture has enough real patterns and invariants to justify it.

## Repository change gate

Every modification to this repository — code, configuration, tests, documentation, tooling, or `.agents` content — must follow `repository-change-workflow`.

Use one branch for the complete reviewable task. Keep all related code, configuration, tests, documentation, tooling, agent-rule edits, and review revisions for that task on that branch so the user can compare that single branch against `main`. Do not create separate branches for individual files or incidental parts of the same change. Start a different branch only for a genuinely separate task or when the user explicitly asks to split the work.

After the change is implemented and verified, open or update one pull request for review and stop. Do not merge it automatically. A merge is allowed only after the user has had the completed diff/PR available to review and then explicitly approves merging it. The original request to implement, fix, refactor, or add something is authorization to prepare the change, not authorization to merge it.

## Interface selection

Prefer the system's semantic interface over its presentation layer. If an existing API, protocol/network request, transport contract, command, or state mechanism already owns the behavior, use that instead of reproducing the behavior by clicking or scraping HTML.

HTML/DOM automation is a last resort. Before using it, establish from evidence that no suitable non-UI mechanism exists or that the requested behavior is inherently UI-only. Do not choose DOM automation merely because the same action is visible in the UI.

For web integrations, inspect the application's actual request/API flow before inventing selectors, click sequences, arbitrary sleeps, or page-state assumptions. If DOM interaction is unavoidable, keep it isolated in the provider-specific boundary and minimize the fragile surface.

## Design intent

The skills are not rigid coding rules. They are a sanity loop:

- Is this the right place?
- Does this duplicate an existing responsibility?
- Is there an existing API/protocol/state mechanism that already owns this behavior?
- Am I touching HTML/DOM only because it is convenient rather than necessary?
- What can it break?
- Does it increase maintenance or coupling?
- Is the logic necessary?
- Is the solution bloated?
- Can architecture remove opportunities for mistakes?
- Is abstraction justified by real repetition/inconsistency rather than hypothetical reuse?
- Is the result simple, direct, robust, obvious, and difficult to misuse?

A shared base/generator/framework mechanism is valuable when it reduces repeated decisions and error space, not merely duplicate lines. Conversely, do not create such a mechanism before real use shows what is actually common.
