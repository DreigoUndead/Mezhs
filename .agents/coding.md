# Coding guidance

Follow [the workflow](workflow.md) and the existing [software-change skill](skills/software-change/SKILL.md); use [systematic-debugging](skills/systematic-debugging/SKILL.md) for faults.

- Establish the existing owner, call chain, source of truth, lifetime and real constraints first; use semantic APIs instead of DOM where available. Prefer one coherent owner to duplicated state or mechanisms.
- Choose Reuse / Repair / Refactor / Redesign deliberately. Avoid speculative abstraction, unnecessary async, hidden fallbacks and unrelated cleanup.
- Inspect the local checkout when coding locally; use the GitHub connector for remote state. Prefer dedicated edit/patch tools over shell-generated source rewriting.
- Use **Codegraph** before broad source searching. For local agents use live `codegraph`; GitHub-only agents may consult `.agents/codegraph/graph.json` as a *navigation hint*, not compiler truth. `.codegraph/` is local cache, never commit it.
- `.agents/codegraph/graph.json` is generated, not hand-edited. When source dependency relationships change, regenerate via `node .agents/codegraph/export.cjs` (Codegraph 3.0.0 + Node.js); report when unavailable or out-of-date.
- `src/Mezhs.Common` owns genuinely generic primitives: `Cast`, `TypeExtensions`, `Registry<TKey..., TValue>`. Reuse them where appropriate; keep feature/domain rules outside Common.
- Preserve MEŽS's trust boundaries, agent shell policy and independent Executor ownership; see [architecture](../docs/architecture.md).
- Include tests with changes, verify the resulting diff, update final specification and release candidate, and **hand off a populated independent testing prompt**. Coding is not approval.