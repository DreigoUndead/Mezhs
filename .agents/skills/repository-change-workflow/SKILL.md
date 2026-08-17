---
name: repository-change-workflow
description: Mandatory for every task that modifies this repository, including code, configuration, tests, documentation, tooling, or agent files. Keeps one coherent review branch for the task and requires explicit user review/approval before merge.
---

# Repository Change Workflow

Apply this workflow to **every repository modification**, including trivial edits.

## Invariant

No requested change is written directly to `main`, all changes belonging to the same reviewable task stay together on one branch, and no completed change is merged before the user reviews it and explicitly approves the merge.

## Before changing anything

1. Start the reviewable task from the current `main` branch.
2. Create **one branch for the task/review set**.
3. Put all related implementation, configuration, tests, documentation, tooling, and `.agents` changes required by that task on the same branch.
4. Do not create extra branches for individual files, cleanup discovered while implementing the same requirement, or review revisions to that task.
5. Start another branch only for a genuinely separate reviewable task, or when the user explicitly asks to split the work.

The goal is that the user can compare one branch against `main` and see the complete proposed change without hunting across branches.

The user's request to fix, add, refactor, edit, or implement something authorizes work on the review branch. It does **not** authorize a merge.

## After implementation

1. Verify the change with the strongest appropriate checks.
2. Review the resulting branch diff for unrelated or accidental modifications.
3. Open or update the single pull request against `main` so the complete change is available for user review.
4. Stop with the pull request unmerged.

## Merge gate

Merge only when all of the following are true:

- the complete implementation already exists on its review branch;
- the completed branch diff or pull request has been made available to the user for review;
- the user then explicitly approves merging it.

Do not infer merge approval from the original implementation request, prior standing permission, successful tests, lack of objections, or a request to "finish" the feature. Approval must apply to the completed change after it is available for review.

If the user requests revisions, update the same review branch and present the revised diff again. Do not create another branch for those revisions, and do not merge until the user explicitly approves the final reviewed state.
