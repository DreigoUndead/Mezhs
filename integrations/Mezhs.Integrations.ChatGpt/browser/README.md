# ChatGPT browser integration

Authenticated ChatGPT Account private-API request construction, turn orchestration,
SSE interpretation, persisted conversation retrieval, citations, file metadata and
model/effort attribution live in C# in `ChatGptAccountProtocol.cs`,
`ChatGptConversationState.cs`, `ChatGptAccountFiles.cs`, and
`ChatGptTypedHttpEndpoints.cs`.

The existing `BrowserAccountSession` owns the shared authenticated Electron
host, authorization and per-chat contexts. `BrowserHttpExecutor` executes typed
C# endpoint mappings, and the reusable Electron `browser-http.js` provides
same-origin request validation, bounded incremental SSE/NDJSON event framing,
ordered acknowledged backpressure, and safe error handling. Electron
`browser-transfer.js` performs bounded signed binary transfers separately from
account credentials. The legacy account JavaScript send/receive and file modules
have been removed; there is no second account client or scheduler.

## Native CommonJS module ownership

Only three source modules are embedded and deployed side by side, unchanged:

- `entry.js`: provider descriptor, authorization policy, native-hook and
  guest operation registration; `typedHttp` and signed transfer mechanics
  are owned by Electron, not provider operations.
- `account-transport.js`: browser-owned token/cookie/integrity state,
  authenticated fetch, and per-turn native ChatGPT Sentinel and conduit
  preparation. These require the live browser security runtime; generated
  credentials or challenges are never returned to C#.
- `guest.js`: the unrelated anonymous browser integration (unchanged).

`ChatGptAccountProtocol` retains the provider's model-preference PATCH; the
effect of distinct overlapping model/effort selections cannot be guaranteed
without independent authenticated live verification.

## Packaging and checks

The .NET integration embeds `browser/modules/*.js` as
`Mezhs.Integrations.ChatGpt.BrowserModule.*`; the common browser base extracts
an immutable content-addressed directory and loads `entry.js` using Node's
ordinary `require()`. There is no generated JS bundle.

Run `node --test tests/*.test.js`, the `tests/chatgpt-account-protocol-flow.ps1`
and `tests/account-login-flow.ps1` C# probes, `dotnet build Mezhs.sln -c Release`,
`tests/integration-resources.ps1`, and the isolated
`tests/ChatGptPackagingProbe`. The account protocol's native hook and generic
stream framing can be tested without reintroducing account-specific JS.
