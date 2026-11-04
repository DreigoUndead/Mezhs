# Engineering guidance for this repository

Project-specific rules live here, not in the reusable `.agents/` guides.

- Use Codegraph before broad source searches when analyzing dependencies. Prefer the local CLI. The committed `.agents/codegraph/graph.json` is a navigation aid, not compiler truth.
- The local `.codegraph/` cache must not be committed. Do not edit the committed graph by hand. After dependency changes, regenerate with `node .agents/codegraph/export.cjs` (Codegraph 3.0.0 and Node.js). Report missing tooling.
- `src/Mezhs.Common` owns reusable primitives such as `Cast`, `TypeExtensions` and `Registry<TKey..., TValue>`. Put domain behavior in its actual owner.
- Prefer semantic transport/API interfaces to DOM automation where possible.
- Preserve the Agent API and Executor ownership split, command policy boundaries and loopback restrictions. Read [architecture](architecture.md) before changing runtime infrastructure.
- For local source edits, inspect the exact working tree; use remote repository tooling for remote tasks. Prefer patch/edit operations over fragile shell-generated file rewriting.