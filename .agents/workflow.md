# Development workflow

Use this process for every change. **Every step, retry and handoff uses a new chat/agent.** Give the next agent a self-contained prompt with the branch, plan, findings and next action. Fixes stay on the same development branch.

## 1. [Plan](specification.md)

- Write `specifications/planned/<id>/plan.md`. Use Grill Me if needed.
- Commit the plan to `main` (through a PR if required).
- Start a new agent to review and update the plan.
- If it changes, start a new review agent. Repeat until no changes are needed.
- The final plan reviewer provides a development prompt for a new agent.

## 2. [Code](coding.md)

- Start a new agent. Create branch `<id>` from `main`. For later fixes, reuse that branch.
- Implement the plan. Update the plan as the design evolves.
- Update `specifications/current/` **when documented behavior changes**. These edits are tested and reviewed with the implementation.
- Use Codegraph for code navigation when available. Refresh generated Codegraph data after coding, before testing/review.
- Prepare the release files on the branch and give a new agent a testing prompt.

## 3. [Test](testing.md)

- A new testing agent tests the final behavior.
- If tests fail, give a **new development agent** a fix prompt. Then start a **new testing agent**.
- When tests pass, give a new agent a code-review prompt.

## 4. [Review](review.md)

- A new review agent reviews the code, test evidence, final plan and any current-spec updates.
- If issues are found, give a **new development agent** a fix prompt. Then repeat testing and review with fresh agents.
- When review passes, declare the change ready and request the **project owner's review**.

## 5. [Release](release.md)

- **Wait for explicit owner approval** of the completed implementation.
- Only after approval, merge to `main`, verify the release, tag its version and **delete the merged development branch**.
- `main` is always releasable. The reviewed development branch must already contain version, changelog, README, final plan and any necessary current-spec updates.

Passing tests, reviewing code or committing a plan does **not** authorize an implementation merge.