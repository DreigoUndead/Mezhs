# Development workflow

Use this process for every change.

**Every step uses a new chat/agent.** This includes repeated steps after fixes. End each step with a self-contained prompt for the next agent. Include the branch, plan path, results and what to do next. Keep fixing the **same development branch**.

## 1. Plan

- Write `specifications/planned/<id>/plan.md`. Use [Grill Me](skills/grill-me/SKILL.md) if needed.
- Commit the plan to `main` (via a PR if required).
- Give a **new agent** a plan-review prompt.
- If the plan changes, start another new agent to review it again.
- When no changes are needed, give a **new agent** a development prompt.

## 2. Code

- Start a new chat/agent.
- Create branch `<id>` from `main`. For later fixes, use the existing branch.
- Implement the plan. Update the final plan when the design changes.
- Update `specifications/current/` **if the change affects documented behavior**.
- Run relevant developer checks. If Codegraph is available, use it to navigate code. Refresh its generated graph after code changes are complete, before testing/review.
- Give a **new agent** a testing prompt.

## 3. Test

- Start a new chat/agent to test the branch against the final plan.
- If tests fail, give a **new development agent** a fix prompt. After the fix, start a **new testing agent**.
- If tests pass, give a **new agent** a code-review prompt.

## 4. Review

- Start a new chat/agent to review code, tests and the final plan.
- Review updates to `specifications/current/` when relevant. Check that they match the resulting behavior.
- If issues are found, give a **new development agent** a fix prompt. Then repeat testing and review in new chats.
- If review passes, declare the work ready. Ask the **project owner** to review it.

## 5. Release

- **Wait for explicit owner permission** to merge the completed implementation into `main`.
- Before approval, prepare version, changelog, README, final plan and any needed current-spec updates **on the development branch**. They are part of the reviewed change.
- After approval, merge into `main` and tag the version. `main` is a release.
- If the generated Codegraph is stale after merge, refresh it through a separate reviewed update. Do not hand-edit generated files.

See [planning](specification.md), [coding](coding.md), [testing](testing.md), [review](review.md) and [release](release.md) for details.

**Rules:** Plans can evolve. Failures always return to development. Every handoff uses a new chat. Passing checks never authorize a merge.