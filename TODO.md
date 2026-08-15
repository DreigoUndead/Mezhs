# TODO

These are intentionally deferred while the integration architecture branch is stabilized. Fix correctness issues before adding new integrations.

## Multi-connection chats

Restore the core MEŽS invariant that one local chat can use multiple accounts/connections/integrations.

- `ChatRecord` must not own a single `ConnectionId`.
- Keep the connection on each message/request (`StoredMessage.ConnectionId`).
- Remote continuation state (`RemoteChatUrl`, conversation IDs, parent IDs) must be stored per chat + connection, not globally on the chat.
- Revisit file ownership: local uploads should be reusable when the next message in the same chat uses another connection, rather than being permanently connection-bound.
- Update API/React so an existing chat can send its next message through any selected connection.
- Add regression coverage that alternates two connections in one local chat.

## Reduce unnecessary async complexity

Keep async where there is real external waiting (HTTP/browser/process/network), but simplify local state work.

- Review `ChatStore` local JSON/JSONL operations; synchronous file I/O behind a normal lock may be simpler and sufficient.
- Replace `MessageService` fire-and-forget `Task.Run` processing with an owned host/background queue so shutdown and failures have one owner.
- Simplify ChatGPT idle-disposal scheduling; the current cancellation + `Task.Run` + semaphore lifecycle is more machinery than the behavior should require.
- Re-check every remaining `async` method and keep it only where it removes real blocking or is required by an external contract.

## Remove integration factory ceremony

Dynamic DLL discovery is useful; the current factory layer probably is not.

- Remove `IIntegrationFactory`, `IntegrationFactory`, and one factory class per integration where they only map type strings to constructors.
- Discover concrete integration registrations directly (for example with a small integration attribute/descriptor).
- Keep validation with the integration/registration that owns the setting instead of a separate switch in a factory.

## Clean browser TypeScript contract names

`BrowserModule.d.ts` currently prefixes every contract type with `MezhsBrowser...` even though the declarations already live in an isolated integration typecheck context.

- Rename to concise contract names such as `BrowserModule`, `BrowserWindow`, `PromptRequest`, `SendContext`, `SendResult`, `Artifact`, etc.
- Keep the `.ts` integration source as the single runtime/typechecked source; do not reintroduce duplicate generated `.js` files.

## Simplify connection identity/metadata

Review the connection fields after multi-connection chat support is restored.

- Keep a stable machine identity separate from the editable display name unless there is a simpler scheme that preserves stored data across renames.
- Enforce unique connection names if that improves UI/config sanity.
- Remove redundant metadata such as `integrationName` if the UI/core does not need it independently from connection name + integration type.
