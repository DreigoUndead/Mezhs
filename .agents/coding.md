# Coding and engineering guidance

Read [the workflow](workflow.md). Use [software-change](skills/software-change/SKILL.md) for design/implementation/review and [systematic-debugging](skills/systematic-debugging/SKILL.md) to establish the cause of faults.

## Coding process

- Start a new agent for each development task or fix. For a new change, branch from `main` using the plan ID. For fixes, stay on the same development branch.
- Read the approved plan, affected current specifications and source. Find the existing owner before adding a second mechanism.
- Prefer semantic APIs/protocols to DOM automation. Choose **Reuse / Repair / Refactor / Redesign** based on real responsibility, not minimum diff size.
- Keep code, tests, final plan and current specifications aligned. Update current specs only when actual behavior changes; review those edits with the implementation.
- Prepare the complete release candidate on the development branch. Commit/push, then give a self-contained testing prompt to a **new agent**. Do not merge.

## Engineering sanity questions

Ask these throughout the work, especially before adding abstractions:

- Is this logic in the right place? Who owns the behavior and state?
- Does it duplicate an existing owner, API, protocol or mechanism?
- Am I touching HTML/DOM only because it is convenient?
- Is async needed for a real wait or lifetime boundary?
- What could this break? Which regressions and security boundaries matter?
- Does this add maintenance, unnecessary coupling or state?
- Is each condition and fallback necessary, or is the solution bloated?
- Can the architecture remove repeated decisions and opportunities for mistakes?
- Would a shared base/generator/framework **actually** reduce complexity?
- Is abstraction justified by real repetition rather than imagined future reuse?
- Is the result simple, robust, obvious, and difficult to misuse?

Prefer a coherent mechanism over duplicated decisions. Do not abstract a first occurrence solely for hypothetical reuse.

## Repository-specific guidance

- **Codegraph:** prefer live `codegraph` before broad manual source searches for dependencies, callers and impact. GitHub-only agents may use `.agents/codegraph/graph.json` as a navigation hint, **not source truth**. After code changes that affect dependencies, regenerate the committed graph before testing/review using `node .agents/codegraph/export.cjs` (Codegraph 3.0.0 and Node.js). Do not hand-edit generated graphs. `.codegraph/` is local cache: never commit it. Report missing tools or stale data.
- **Shared foundation:** `src/Mezhs.Common` owns generic primitives such as `Cast`, `TypeExtensions` and `Registry<TKey..., TValue>`. Reuse these where appropriate; keep domain rules in their actual owner.
- **Runtime boundaries:** preserve Agent API policy/queue ownership, Executor shell/process ownership and loopback security. Read [architecture](../docs/architecture.md) before touching them.
- **Working tree:** use local shell inspection/build/tests for a checked-out repository; GitHub tools for remote PRs and branches. Prefer dedicated patch/edit operations over shell one-liners. Confirm changes with a diff. Do not kill running applications just to get a build.

## Testing handoff

> Test branch `<id>` at commit `<sha>`. Read `.agents/testing.md` and the final plan in `specifications/implemented/<id>/`. Run relevant tests and verify changed current specifications. Return PASS/FAIL/BLOCKED with evidence. On failure, write a fix prompt for a **new developer agent**. On PASS, write a review prompt for a **new review agent**.