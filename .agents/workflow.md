# Development workflow

Use this process for every change. Keep it simple.

## 1. Plan

- Create `specifications/planned/<id>/plan.md`. Use [Grill Me](skills/grill-me/SKILL.md) when requirements are unclear.
- Commit the plan to `main`. Plans contain documentation only. Use a planning PR if required by repository rules.
- Give another agent a prompt to **review and update the plan**.
- If changes are needed, update the plan on `main` and repeat the review.
- When no changes are needed, the plan reviewer gives a **development prompt**.

## 2. Code

- Create a branch named `<id>` from the latest `main`.
- Implement the plan. Update it as the design evolves. The final plan should describe the result.
- Prepare tests and release files on this branch.
- When coding is done, give a **testing prompt**.

## 3. Test

- A separate agent tests the branch against the final plan.
- If tests fail, send findings back to the developer. Fix them and test again.
- When tests pass, give a **code-review prompt**.

## 4. Review

- A separate agent reviews the code, tests and final plan.
- If issues are found, send them back to the developer. Then repeat testing and review.
- When the review passes, declare the work ready. Ask the **project owner** to review the completed change.

## 5. Release

- **Wait for explicit owner permission** to merge the completed implementation into `main`.
- On approval, merge. `main` is a release: update the version, changelog, README, and current specifications as part of the reviewed branch, not afterward.
- Move the final plan to `specifications/implemented/<id>/` in that same branch.
- Tag the merged release.

See [specification](specification.md), [coding](coding.md), [testing](testing.md), [review](review.md), and [release](release.md) for each step.

**Rules:** Fix problems on the same development branch. Failed checks never approve a merge. A plan commit is not permission to merge code.