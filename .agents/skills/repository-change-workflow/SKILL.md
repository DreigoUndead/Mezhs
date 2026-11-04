---
name: repository-change-workflow
description: Mandatory single authoritative MEŽS planning, development, independent testing, review and Monkey-controlled release gates for every repository change.
---

# Repository Change Workflow

**Read and follow [the authoritative workflow](../../workflow.md).** Do not maintain different rules in this skill.

All repository changes need a plan. Plan PRs are approved and merged first; implementation work stays in one branch, is independently tested and reviewed, and **must not merge into main without Monkey's explicit approval of the completed candidate**. This applies to code, tests, configuration, tooling and agent instructions.

The `standardize-development-workflow` bootstrap exception is defined in the workflow itself.