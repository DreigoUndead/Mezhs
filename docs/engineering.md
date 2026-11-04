# Repository engineering notes

Project-specific details are kept here so the agent workflow remains portable.

- Use Codegraph before broad source searches when analyzing dependencies. Prefer the local CLI. The committed `.agents/codegraph/graph.json` is a navigation aid, not compiler truth.
- The local `.codegraph/` cache must not be committed. Do not edit the committed graph by hand. After code changes are complete and before testing/review, regenerate with `node .agents/codegraph/export.cjs` (Codegraph 3.0.0 and Node.js). Report missing tooling.
- `src/Mezhs.Common` owns reusable primitives such as `Cast`, `TypeExtensions` and `Registry<TKey..., TValue>`. Put domain behavior in its actual owner.
- Prefer semantic transport/API interfaces to DOM automation where possible.
- Preserve the Agent API and Executor ownership split, command policy boundaries and loopback restrictions. Read [architecture](architecture.md) before changing runtime infrastructure.
- For local source edits, inspect the exact working tree; use remote repository tooling for remote tasks. Prefer patch/edit operations over fragile shell-generated file rewriting.