# Development Workflow

This workflow applies to software-development tasks. Use only the
stages needed for the task; the full workflow is not the default.

## Starting work

1. Identify your active role or roles.
   - For a new development request, start with Plan.
   - For an existing task or handoff, use the assigned role.
2. Read the instructions for your active role and
   [Batman](batman.md).
3. If you are not the Planner, read the task plan, including its
   Changes section, and work on the branch specified there.
   For a Small task without a plan file, use its task description.
4. Record changes and new decisions in the task plan's Changes
   section, following [Specification](specification.md).

## Roles

- **Plan:** [plan.md](plan.md) — establish requirements, scope,
  risks and execution steps.
- **Plan Review:** [plan-review.md](plan-review.md) — independently
  validate and correct the plan.
- **Development:** [development.md](development.md) — implement
  the plan and run developer tests.
- **Code Review:** [code-review.md](code-review.md) — independently
  review and correct the implementation.
- **Testing:** [testing.md](testing.md) — verify behavior and
  correct failures.
- **Deploy:** [deploy.md](deploy.md) — obtain merge approval and
  finalize the release.

The task plan determines which stages are required and may define
multiple sequential steps within a stage.

## Execution

Follow the selected stages in order:

Plan → Plan Review → Development → Code Review → Testing → Deploy

Only one agent or chat may actively work on a task at a time.

When a role is complete, hand off to the next required role using
[Next Role Prompt](next-role-prompt.md). Start a fresh agent/chat
when independence is required. One chat may perform multiple roles
only when the plan permits it and independent judgment is not required.

Each role follows the repeated-step rules below and its own
PASS, FAIL or BLOCKED requirements.

Do not return to an earlier stage unless the scope or plan changes
significantly and the owner approves that transition.

## Repeated steps

When Plan Review, Code Review or Testing repeats after FAIL:

- Read the prior iteration's findings and corrections in the task
  plan. Independently verify the latest corrections and possible
  regressions first, then complete the role's normal scope.
- For each defect, examine its underlying cause and related
  occurrences before correcting it. Fix related defects together;
  do not expand into speculative improvements.
- A correction materially affecting requirements, architecture,
  behavior, security or the validity of required verification needs
  a fresh independent iteration before PASS. Nonmaterial editorial
  or test-harness cleanup can be verified in the current iteration.
  The impact, not the file type, determines this distinction.
- PASS requires completed mandatory verification and no unresolved
  blocking findings. Record nonblocking observations without
  automatically repeating the step.
- Check whether repeated iterations are resolving findings. If the
  same underlying problem recurs or progress stalls, reassess the
  approach before another handoff; consult the owner when a
  consequential decision is needed. Never turn a failed or blocked
  requirement into PASS merely to stop the loop.

## Escalation

- Consult the owner before changing consequential requirements,
  architecture, accepted guarantees or workflow obligations.
- Stop and reassess the design before adding disproportionate
  complexity or stronger guarantees than agreed.
- After two fixes driven by the same architectural decision,
  reassess that design before another fix.
- The owner may override workflow steps. Record the override
  in the task plan. An override is not permission to merge.

`main` is reserved for approved releases. Changes must remain on
the task branch until the owner explicitly approves the completed
implementation for merging.

## Environment

When operating inside MEŽS, also follow [MEŽS](mezhs.md)
for environment-specific instructions and agent creation.
