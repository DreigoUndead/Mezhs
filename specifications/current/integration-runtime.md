# Integration runtime — current behavior

This is the runtime behavior implemented on the `integration-architecture-v2` branch; see [architecture](../../docs/architecture.md) for owners and [the plan](../implemented/integration-architecture-v2/plan.md) for acceptance criteria.

- One integration instance per configured connection; immutable per-chat prompt contexts. Core owns chats, local messages, remote continuation mappings, history and persistence. Provider-generated outgoing IDs are persisted before submission, and reply parent IDs are retained. Existing local IDs, chat links, files and history are preserved.
- `ChatContext.GetHistoryAsync` selects chronologically ordered Core messages across connections by nullable limit/offset and oldest/newest direction, without providers opening SQLite directly.
- Integrations return a best-known executed/current/selected model. A warning appears when an explicit non-default requested model differs from the same-connection reply's returned model, without retry or automatic reselection; a selected-model fallback is not a provider verification.
- The direct integration model/login/diagnostic contract replaces legacy capability modules. `AllowFiles` gates input attachments; output files are handled without a capability flag. API and local-model bases remain minimal placeholders.
- Browser integration owns connection `limits.maxParallel` and `limits.minInterval`; local ChatId sends remain serialized. Browser account integrations reuse the shared authenticated Electron host, per-chat windows and account session; guest contexts are ephemeral. Agent retains/releases its chat context across shell waits. Cancellation never automatically replays an uncertain send.
- ChatGPT Guest, Gemini Guest and Grok Account retain existing DOM behavior pending a separately scoped provider-protocol refactor.
- The typed Electron HTTP bridge proves an authenticated, read-only ChatGPT models GET using Electron-owned auth. **It does not implement a shared typed normal-`HttpClient`/Electron executor, typed mutation submission or C# SSE interpretation.** This remains an unmet approved V2 acceptance criterion, not an authorized change of scope.

This file records observed implementation behavior, not independent review or final release approval.
