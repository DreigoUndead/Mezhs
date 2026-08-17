# Repository-local engineering skills

These skills are intentionally small and composable.

- `repository-change-workflow` — mandatory workflow gate for every repository modification. Each reviewable task stays on one branch and may be merged only after the user reviews the completed change and explicitly approves the merge.
- `software-change` — engineering/design judgment for non-trivial implementation, refactoring, architecture work, and code review. It continuously challenges ownership, duplication, regression risk, necessity, complexity, abstraction timing, maintainability, interface choice, async usage, and elegance, then chooses Reuse / Repair / Refactor / Redesign.
- `systematic-debugging` — evidence-first root-cause investigation for bugs and unexpected behavior. Once the cause is established, it hands the scope decision back to `software-change`.

There is deliberately no eFlex-specific skill in this repository. eFlex was used as evidence for the general reasoning principles, but Mezhs should acquire project-specific guidance only when its own architecture has enough real patterns and invariants to justify it.

## Repository change gate

Every modification to this repository — code, configuration, tests, documentation, tooling, or `.agents` content — must follow `repository-change-workflow`. That skill is the source of truth for branch/review/merge behavior.

## Design intent

The skills are not rigid coding rules. They are a sanity loop:

- Is this the right place?
- Does this duplicate an existing responsibility?
- Is there an existing API/protocol/state mechanism that already owns this behavior?
- Am I touching HTML/DOM only because it is convenient rather than necessary?
- Does this operation actually require async?
- What can it break?
- Does it increase maintenance or coupling?
- Is the logic necessary?
- Is the solution bloated?
- Can architecture remove opportunities for mistakes?
- Is abstraction justified by real repetition/inconsistency rather than hypothetical reuse?
- Is the result simple, direct, robust, obvious, and difficult to misuse?

A shared base/generator/framework mechanism is valuable when it reduces repeated decisions and error space, not merely duplicate lines. Conversely, do not create such a mechanism before real use shows what is actually common.
