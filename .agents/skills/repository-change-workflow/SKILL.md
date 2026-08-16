---
name: repository-change-workflow
description: Mandatory for every task that modifies this repository, including code, configuration, tests, documentation, tooling, or agent files. Requires a fresh branch for each requested change and explicit user review/approval before merge.
---

# Repository Change Workflow

Apply this workflow to **every repository modification**, including trivial edits.

## Invariant

No requested change is written directly to `main`, and no completed change is merged before the user reviews it and explicitly approves the merge.

## Before changing anything

1. Start from the current `main` branch.
2. Create a **fresh branch for this change request**.
3. Do not reuse a branch from an earlier task, even if the work is related.
4. Make all commits for the requested change on that branch.

The user's request to fix, add, refactor, edit, or implement something authorizes work on the branch. It does **not** authorize a merge.

## After implementation

1. Verify the change with the strongest appropriate checks.
2. Review the resulting diff for unrelated or accidental modifications.
3. Open a pull request against `main` so the completed change is available for user review.
4. Stop with the pull request unmerged.

## Merge gate

Merge only when all of the following are true:

- the implementation already exists on its branch;
- the completed diff or pull request has been made available to the user for review;
- the user then explicitly approves merging it.

Do not infer merge approval from the original implementation request, prior standing permission, successful tests, lack of objections, or a request to "finish" the feature. Approval must apply to the completed change after it is available for review.

If the user requests revisions, update the same review branch and present the revised diff again. Do not merge until the user explicitly approves the final reviewed state.
