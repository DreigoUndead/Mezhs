# Development workflow

Use the smallest safe process. **The project owner approves every implementation merge into `main`.**

## 1. [Choose the level](specification.md)

The first planner evaluates the request before drafting a plan: feasibility, complexity, risks, and conflicts with existing requirements or architecture. Report the assessment to the owner, distinguishing known constraints from uncertainties, and resolve material blockers before proceeding. State the level and reason **before work starts**; the owner may override the process. Reassess if the change grows.

| Level | Examples | Default process |
| --- | --- | --- |
| **Small** | Typo, isolated CSS, minor documentation | One chat can plan, change, check and pause for owner review. No mandatory extra agents or plan-only commit. |
| **Standard** | Most features and bug fixes | Plan on `main`, development branch, independent code review, and targeted verification. Use a separate tester when independence adds value. |
| **High-risk** | Security, architecture, concurrency, migrations, sensitive data | Plan on `main` with independent plan review; independent code review **and** independent testing. |

Do not downgrade high-risk work simply to save time. Record a reason for skipped checks or an owner override. The owner may explicitly change the process; **merge permission is always separate**.

**Context quality:** A new agent is useful when the current one loses track of decisions, repeats investigation, confuses versions or produces weaker reasoning. This is a judgment call, **not a fixed token threshold**. For a Small change, staying in the same chat is fine if the work remains clear.

## 2. [Plan](specification.md)

- **Small:** a brief plan in the change branch or PR description is enough.
- **Standard / High-risk:** prepare `specifications/planned/<id>/plan.md`, present the owner with a summary of the proposed plan **before committing**, and commit it to `main` only after owner approval (using a PR if required).
- Plan reviewers must disclose proposed plan changes to the owner and receive approval **before editing**, regardless of whether a prior Grill Me session occurred.
- Use Grill Me when needed. Independent plan review is required for High-risk changes, optional for Standard.
- When ready, hand off to development with the generic prompt and a reference to the plan; do not restate its scope or acceptance criteria in the prompt. A new chat is used **when an independent stage or context reset is needed**.

## 3. [Code](coding.md)

- Use a branch named `<id>`. Reuse it for fixes.
- Implement and run developer checks. Plans may change as code evolves.
- **Before independent verification**, move any completed plan from `planned/` to `implemented/` **on the development branch**. The original plan in `main` stays until merge.
- Update `specifications/current/` only if behavior changed. Review these updates with the implementation.
- Use Codegraph where helpful; refresh the generated graph after relevant code changes, before review.
- Prepare a short release summary and proposed change type (major/minor/patch), then check the branch diff. **Do not edit the version or changelog in the development branch.**

## 4. [Review](review.md)

- Review the implementation **before final testing**. The reviewer also checks planned test coverage.
- **Small:** a careful same-chat self-review may be enough.
- **Standard:** use an independent reviewer.
- **High-risk:** independent review is mandatory.
- When an independent reviewer finds issues, **that reviewer fixes them** on the same branch, verifies and commits the corrections, then hands the result to a **new independent reviewer**. Repeat reviewer → corrections → new reviewer until the latest reviewed SHA receives PASS. Escalate a blocker to the owner instead of silently bypassing it.

## 5. [Test](testing.md)

- Run the checks appropriate to the level and risks. Developer checks and CI are not the same as independent testing.
- **Small:** same-chat checks and relevant CI can be sufficient.
- **Standard:** targeted tests; an independent tester when meaningful.
- **High-risk:** independent testing is mandatory.
- On testing failure, return to development, then repeat **affected independent review and tests** after changes. The reviewer-to-reviewer fresh-chat rule applies when a reviewer has made corrections; other minor operations do not require a new chat.

## 6. [Owner approval and release](release.md)

- Report what changed, test/review evidence, remaining risks, branch/PR and exact commit.
- **Pause for the project owner's explicit approval**. Small changes may stay in one chat through this pause. The final tester can perform release after approval; a separate release agent is optional.
- Before merging, recheck the candidate commit and current `main`. Changed implementation or conflicts may require new review, tests and **renewed owner approval**.
- After approval, merge into `main`. **At merge finalization, update the version and changelog exactly once for the whole task**, then tag the resulting release commit and delete the merged branch. No version/changelog edits during planning, development, review or testing.

### Handoffs and evidence

Every prompt handed to another role must **start on its first line** with `<Task name> — <Role>` (no preamble or Markdown heading), because MEŽS uses that line as the chat title. Use this generic template, omitting fields already provided by the runtime or not applicable:

```text
<Task name> — <Role>
Task ID: <id>
Repository: <owner/repo>
Branch/PR: <ref>
Commit: <sha>
Plan: <path, if applicable>
Follow AGENTS.md.
```

Keep handoffs **generic and minimal**, unless the owner explicitly requests a tailored prompt: give only the identifiers needed to locate the work, not procedures already defined in `AGENTS.md` and `.agents/`. In particular, a prompt to an **independent code reviewer** must not reveal earlier findings, fixes, suspected issues, or review focus, even when handed off by a reviewer who just fixed problems. Store findings and verification evidence in the PR or `verification.md`, tied to the relevant SHA, for auditability rather than steering the new reviewer through the prompt.

A fresh chat is required for independent checks and after **each code-review correction round**, not for every minor edit outside that loop. Request a fresh chat if context quality becomes doubtful. **No agent PASS or plan approval grants merge permission.**
