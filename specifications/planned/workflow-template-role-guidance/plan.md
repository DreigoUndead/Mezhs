# Development Workflow Template

ID: workflow-template-role-guidance
Branch: `workflow-template-role-guidance`
Level: Standard (documentation refactor; owner reviews each file)

## Goal

Create a small, generic, reusable development workflow whose rules have one clear owner. Prevent agents from inventing extra process, reopening settled decisions, duplicating instructions across files, or losing the task context between clean agent sessions.

## Scope and acceptance

- Re-review every workflow file against this blueprint, starting with `AGENTS.md`. Present each proposed complete file to the owner; commit only after approval.
- Keep the workflow usable outside MEŽS. MEŽS-specific execution instructions live in a separate optional override.
- Use the task plan as the current task's source of truth, including its agreed decisions and Changes section. Changes in the implementation or review must not silently reverse an accepted decision.
- One active agent/chat at a time per task. A new clean context is used when independent review/testing is required; agents do not run concurrent workstreams within the same task. Larger work may be divided into sequential role steps.
- Keep the sequence linear. Do not return to an earlier stage unless a significant plan/scope change requires it and the owner approves. Repeating a failing review within the same role is not a backward stage transition.
- Preserve the full workflow as the maximum, not the default. The Planner selects the necessary roles and independence for Small / Standard / High-risk changes; skipped roles do not create empty agent handoffs.
- No repository edits to `main` during planning, development, review or testing. An implementation merge requires explicit approval of the completed change. Deploy/release is the final stage.
- Do not introduce unrelated application code or speculative safeguards as part of the workflow restructure.

## Authoritative files

| File | Owns |
| --- | --- |
| `AGENTS.md` | General navigation index for Codegraph, project architecture and development workflow; safeguard against unapproved modifications to `main`. Does not assign roles or require a task plan for non-development requests. |
| `.agents/workflow.md` | Role identity, stage sequence, linearity, one-active-agent rule, required role instructions, escalation, owner overrides, and optional MEŽS override reference. |
| `.agents/plan.md` | Planner role: research, clarify via Grill Me as needed, assess risks/impact, select stages and verification, create task branch and initial plan. |
| `.agents/specification.md` | Generic task plan format and rules for updating an existing plan/specification, including the Changes section and decision history. The Planner creates the plan; this file defines its format. |
| `.agents/plan-review.md` | Independent initial plan review, corrections, and fresh Plan Reviewer loop until PASS. |
| `.agents/development.md` | Developer role: implementation, developer tests, verification, and next-stage handoff. |
| `.agents/code-review.md` | Independent code/design review, fixes, fresh Code Reviewer loop until PASS. |
| `.agents/testing.md` | Functional/integration/live acceptance verification as required, fixes, fresh Tester loop until PASS. |
| `.agents/deploy.md` | Final delivery summary, explicit owner merge approval, approved merge and plan archival; invokes `versioning.md` for release metadata. |
| `.agents/versioning.md` | Version selection, changelog updates, release tagging and release metadata finalization. |
| `.agents/next-role-prompt.md` | Minimal neutral handoff with role, numbered plan step and task identity; no previous-agent narrative or findings. |
| `.agents/batman.md` | Shared evidence-based engineering sanity/judgment used by every role; avoid invented requirements and disproportionate complexity. |
| `.agents/mezhs.md` | Optional MEŽS-specific execution override (agent creation/tool behavior); must not duplicate generic role rules. |

Names above are the intended target layout. Existing files may have other names; migrate their useful content and repair references rather than maintaining two authorities.

## Execution contract

1. A new user development request starts in the Planner role. A delegated agent starts in the role specified by its handoff. Non-Planner roles read the current task plan before working.
2. Planner establishes what/why/how, acceptance criteria, risks, workflow level, role steps, dependencies, and verification. Important conflicts and owner decisions are resolved before the initial plan is committed on its task branch.
3. Plan Review: inspect the plan and architecture. On FAIL, correct the plan and start a *fresh independent Plan Reviewer*. Repeat until PASS. Ask the owner only when a decision is needed.
4. Development: implement the assigned plan step(s), write and run developer tests, then hand off to Code Review.
5. Code Review: find real code/design issues. On FAIL, fix on the same branch and start a *fresh independent Code Reviewer*. Repeat until PASS.
6. Testing: perform the required real checks and record evidence. On FAIL, fix on the same branch and start a *fresh independent Tester*. Repeat until PASS. Do not silently return to Code Review; significant scope/plan changes require owner authorization.
7. Deploy: summarize the exact completed change and verification for the owner, request explicit merge permission, and only after approval finalize the merge/release and archive the plan under `specifications/implemented/`.

Each role defines its own PASS / FAIL / BLOCKED behavior and next role; `next-role-prompt.md` only defines *how* to construct a handoff. A missing required verification is BLOCKED, not PASS. All roles use `batman.md`. The workflow must not demand extra stages or protections unsupported by the task's agreed requirements.

## Specification and Changes rules

The initial plan format must cover **What, Why, How, role steps, verification, and Changes**, proportionately to complexity.

- Keep the plan's main sections current with the accepted requirements and implementation.
- Log consequential additions, changed decisions, accepted limitations, workflow changes, and superseded approaches in **Changes**; avoid a transcript of every edit.
- A change entry should identify the decision, reason, accepted trade-off where relevant, authority/approval, and any superseded decision or supporting revision. Not every field is needed for every entry.
- Distinguish proposals from approved changes. Preserve the rationale for superseded decisions so later agents do not reintroduce rejected designs by default.
- An agent may correct ordinary issues without requesting owner input. Changing an owner-approved requirement, guarantee, or consequential architectural choice needs owner agreement.
- Later agents assess new evidence independently, then reconcile with decisions already recorded. A materially new failure may justify reopening a choice, but not silently.

## Review order

1. `AGENTS.md`
2. `.agents/workflow.md`
3. `.agents/plan.md`
4. `.agents/specification.md`
5. `.agents/plan-review.md`
6. `.agents/development.md`
7. `.agents/code-review.md`
8. `.agents/testing.md`
9. `.agents/deploy.md`
10. `.agents/versioning.md`
11. `.agents/next-role-prompt.md`
12. `.agents/batman.md`
13. `.agents/mezhs.md`
14. Remove obsolete/conflicting guidance and repair incoming links; verify the integrated workflow.

## Verification and limits

- Review one file at a time, with owner approval before writing each proposed revision. This plan is explicitly requested and may be committed immediately.
- Inspect actual repository paths and retained useful rules rather than rewriting from assumptions.
- Check links, role transitions, one-authority ownership, clean-context handoffs, task-branch-only edits, and decision preservation.
- Keep existing MEŽS architecture and Codegraph instructions in their project-specific owners. Do not migrate project-specific rules into generic role templates.
- This is documentation work: validate text and references; do not claim runtime/agent-spawn testing unless executed.
- The existing task branch may be behind current `main`. Synchronize deliberately before final integration, not as part of an unreviewed file edit.

## Changes

- **2026-11-20 — `mezhs.md` review:** Approved the MEŽS-only override for creating and verifying fresh agents via local Agent API and isolated live testing in temporary worktrees with separate ports, storage, databases, Electron profiles and authentication. Codegraph, Rich UI, shell protocol and general responsibilities remain in their existing owners rather than being duplicated. Committed as `8397284`.

- **2026-11-20 — `batman.md` review:** Approved shared engineering sanity checks for reality/evidence, architectural ownership, simplicity and meaningful regressions, avoiding invented issues, speculative guarantees and disproportionate safeguards. Workflow escalation and role-specific review remain in their own files. Committed as `bcd7f28`.

- **2026-11-19 — `next-role-prompt.md` review:** Approved a neutral locator-only handoff with first line `<Role> — <Step number> — <Task title>`, task branch and plan path. A plan may define multiple sequential steps for the same role; the next agent independently reads repository and plan instead of receiving previous-agent actions, findings or direction. Small tasks without a plan use step 1 and a brief task description. Committed as `ee26848`.

- **2026-11-19 — `versioning.md` review:** Approved a dedicated release-metadata authority: one version for each owner-approved implemented task, final version from current `main`, changelog format based on existing entries (Added/Changed/Fixed, Verification and approval, Known limitations), accurate final-revision evidence and disclosed verification gaps, and one final-release tag with duplicate-bump protection. Committed as `270b2ee`.

- **2026-11-19 — `deploy.md` review:** Approved a focused Deploy role: present completed implementation and evidence, request explicit owner merge permission, verify the approved candidate, archive the plan during Deploy, merge/release, and clean up the merged branch. Version selection, changelog and tagging move to a separate `versioning.md` authority, reviewed next. MEŽS shell behavior (CMD versus explicit PowerShell) will be addressed in `mezhs.md`. Committed as `d70572a`.

- **2026-11-19 — `testing.md` review:** Approved live verification of changed behavior and relevant risk/regression areas when Testing is selected; record actual revision and outcomes, BLOCKED if live testing cannot be done without an owner-approved limitation, FAIL → corrected code and fresh Tester, PASS → Deploy. MEŽS-specific test worktrees and isolated instances will be defined in `mezhs.md`. Committed as `1e3558a`.

- **2026-11-19 — `code-review.md` review:** Approved review of whole change against its base, design and requirements; verify affected code and tests, reconcile independent findings with accepted decisions, correct issues directly, and on FAIL hand off to a fresh Code Reviewer until PASS. Committed as `30ac48b`.

- **2026-11-19 — `development.md` review:** Approved a focused Developer role: implement assigned steps, write or update proportionate tests using project conventions, use Systematic Debugging to establish failures, run verification and report results. Plan reading, independence, escalation and handoffs remain in `workflow.md`. Committed as `8e2ae2a`.

- **2026-11-19 — `plan-review.md` review:** Approved independent initial Plan Review, evidence-based findings reconciled with existing decisions, corrections in the same plan on the task branch, and FAIL → fresh Plan Reviewer / PASS → Development / BLOCKED → owner. Committed as `56f9f71`.

- **2026-11-19 — `specification.md` review:** Approved the shared task-plan format (What, Why, acceptance criteria, How, Steps, Verification and Changes) and decision-continuity rules. Include concrete interfaces, risks, rollback and commands only when needed; do not turn the plan into a transcript. Committed as `3441a3d`.

- **2026-11-19 — `plan.md` review:** The owner approved the Planner role for initial requirements, risks, proportional stage selection, task-branch creation and initial plan creation. The plan template and ongoing maintenance remain assigned to `specification.md`. Committed as `6377426`.

- **2026-11-19 — `workflow.md` review:** Approved linear role orchestration, handoffs through `next-role-prompt.md`, the one-active-agent rule, scope-change escalation and task-branch-only work. Committed as `9f313a1`. Names in this file are target paths; remaining files will be migrated one at a time.

- **2026-11-19 — `AGENTS.md` review:** The owner chose a flat general reference index without a task-plan requirement or role assignments, retaining the explicit `main` safeguard. The approved file was committed as `46afa2e`.

- **2026-11-19 — Owner-approved blueprint:** Replace the earlier fragmented workflow-file structure with the authoritative files and responsibilities above. Re-review from `AGENTS.md`, one file at a time.
- **2026-11-19 — Linear task execution:** One active agent/chat; new clean contexts for required independent roles. Repeat failed Plan Review, Code Review, or Testing in the same role with a fresh agent; no backward stage transitions without owner-approved significant changes.
- **2026-11-19 — Plan continuity:** `specification.md` owns plan format and Changes rules, while `plan.md` owns initial plan creation. Accepted decisions and trade-offs must survive handoffs.
- **2026-11-19 — Release boundary:** Deploy requests explicit owner merge approval; the plan moves to `implemented/` during approved finalization, not during Development.
