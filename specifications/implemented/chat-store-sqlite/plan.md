# SQLite ChatStore and simplified Agent chat deletion
ID: chat-store-sqlite
Level: High-risk — persistent data migration, concurrent message activity, and Agent execution lifecycle.

## Goal and rationale

Replace the file-backed core `ChatStore` with SQLite, using the existing `Mezhs.Sqlite` infrastructure in the same style as `AgentStore`. Simplify Agent chat deletion without sharing databases or implementing cross-store rollback/recovery.

Why:
- Today `ChatStore` uses `chat.json`, append-only `messages.jsonl`, `categories.json`, in-memory dictionaries and directory staging. Multiple files cannot participate in one atomic deletion or update.
- SQLite makes **each owner's own operations** transactional and removes most file/JSON persistence locks, staging and recovery complexity.
- Core, Agent and Executor have distinct responsibilities. Making all three stores participate in one coordinated delete is unnecessary for the agreed behavior.

## Ownership and explicit trade-offs

- `Mezhs.Api` owns chats, messages, categories and remote conversation state in its **own** SQLite database (e.g. `<storage.root>/chats.sqlite`). Core must not depend on `Mezhs.Agent.Api` or Executor.
- `Mezhs.Agent.Api` owns `AgentStore` in its existing separate `agent.sqlite`. It may orchestrate calls to the core `ChatStore`, never the reverse.
- Executor owns its **own** SQLite database, running processes, restart behavior and historical executions. **Chat deletion never calls Executor to block, kill, cancel, inspect or delete anything.** Already-running shells may finish; their output is not consumed by the deleted Agent. Their external side effects and historical records remain possible.
- Deliberately **no distributed transaction, two-phase commit, cross-store rollback, durable deletion tombstones or cross-store reconciliation**. If the process crashes or Core deletion fails after Agent deletion commits, the remaining Core chat is acceptable and can be deleted with a subsequent request. A retry must work even if no Agent rows exist.
- Preserve unrelated `FileStore` files; do not migrate or delete uploaded file contents as part of chat deletion.

## Implementation

### 1. Convert core ChatStore to SQLite

- Reuse `Mezhs.Sqlite.SqliteDatabase`; keep the existing `ChatStore` service/API ownership and public HTTP contracts.
- Persist chat metadata (including titles, grouping, timestamps and remote states), all message fields and categories in SQLite. Use appropriate keys/indexes and database transactions for create/update/delete, including bulk deletion and category unassignment.
- Replace append-on-every-status-change JSONL persistence with SQLite updates/upserts. Preserve existing message ordering, reply/replay links, statuses, activity, attachments references, remote state and derived sidebar metadata.
- Remove file-deletion staging and obsolete file-backed caches/locking where no longer needed; keep only synchronization actually required for live message/Agent admission. Do not introduce Agent-specific behavior into core.
- Keep standalone Main API behavior and Agent host's inherited core API endpoints compatible.

### 2. Safely migrate existing chat data

- On startup, import current `chats/*/chat.json`, `messages.jsonl`, `categories.json` and supported legacy `connections/*/chats/` layouts when transitioning from the file store. Preserve IDs, timestamps, categories, titles, remote state and all message content/metadata.
- Where JSONL contains multiple saved versions of one message, use its last persisted version. Detect and report invalid/unimportable records rather than silently dropping data.
- Make migration **one-time, transactional and restart-safe**: record completion in SQLite atomically with the import, retain legacy files as a backup, and never reimport them on later starts (especially after a chat has been deleted). A failed import must leave old files untouched and must not mark migration complete.
- Add migration fixtures/tests for real and legacy layouts, interrupted migration, repeated startup, and deletion followed by restart.

### 3. Replace Agent-host chat deletion coordination

Keep the existing single/bulk `DELETE /v1/chats` routes; no parallel Agent endpoint. The shared `MapMezhsApi` maps Core deletion by default and accepts an optional Agent-host deletion callback. Avoid a one-method deletion interface and Core pass-through handler; retain the Agent-specific coordination class.

1. Normalize and validate the complete requested set of Core chat IDs.
2. Under a small **in-process admission/stop gate**, prevent new Agent work from entering those chats. Stop queued/running **Agent reasoning/worker** activity and wait until those workers can no longer write Agent/Core chat data. A deletion-only stop must **not** reuse cancellation that kills Executor shells, and must not wait for those shells to finish.
3. Ensure ongoing core message operations cannot write into removed chats after deletion. If Agent/core processing cannot quiesce, report failure **before** destructive commits.
4. Delete Agent chat, execution and staged-attachment **references** in **one AgentStore SQLite transaction**.
5. Delete Core chat and messages in **one ChatStore SQLite transaction**. Successful response only after both commits. Bulk requests use one transaction per store; preserve validation semantics.
6. On failure after step 4, return an error; do **not** try to reconstruct Agent data. Repeating deletion against the remaining Core chat must be safe even when Agent data is already absent.

- Remove the current deletion-specific `Preparing`/`Deleting` guard/reconciliation machinery and `Executor.BlockChats`/`UnblockChats`/active-shell checks from the Agent deletion path. Any Executor-internal compatibility cleanup belongs exclusively to Executor code; never manipulate its database from Agent/Core.
- Keep the normal Main API deletion purely Core-owned. Continue preventing concurrently admitted messages from recreating deleted chats, without a distributed protocol.
- Adjust UI confirmation/error handling only where needed to reflect the new stop/detach semantics. Search/group/rename and other chat-management UI behavior is not part of this redesign.

## Acceptance criteria

- [ ] Core chat, message, category and remote state operations persist correctly across restarts using only Core SQLite; no Core dependency on Agent/Executor.
- [ ] Existing file-backed data migrates losslessly and exactly once; deleted chats never reappear from retained legacy JSON files.
- [ ] Main API single/bulk chat deletion uses a local SQLite transaction, without Agent/Executor knowledge.
- [ ] Agent host stops and joins Agent activity before Agent-first/Core-second deletion; it neither kills nor waits for independently running Executor work.
- [ ] Deletion does not touch Executor storage or historical executions; detached shell completion cannot recreate a deleted Agent/chat or resume reasoning.
- [ ] Concurrent Agent submissions/claims, core message updates and repeated/delete retries are handled without stale writes.
- [ ] A forced failure/crash between commits may leave Core-only data, but restart remains usable and a later Core delete succeeds. No cross-store rollback or tombstone-recovery mechanism is added.
- [ ] Existing APIs, UI chat-management features and unrelated shared files behave as before.

## Verification and integration

- Build full .NET solution and both Web UIs; run the core/Agent API, storage, concurrency, deletion and restart smoke suites.
- Add migration and partial-failure injection tests; check JSONL status deduplication, metadata/remote-state preservation, category operations, bulk transactions, deletion/restart non-resurrection, Agent stop without shell kill, and late detached shell completion.
- The owner waived further separate plan review on 6 November 2026 because the design had already been agreed through prior task history. Independent code review and independent final testing remain mandatory. Update `specifications/current/` when behavior changes, and move this plan to `specifications/implemented/` on the same task branch before code review. Planning-only commits must never target `main`.
- **Branch and integration dependency:** This plan is being staged on the existing `agent-chat-management` branch (draft PR #47), whose deletion plan specifies the old guard/Executor behavior. This plan supersedes **that deletion behavior only**, not the UI feature. Create the dedicated `chat-store-sqlite` task branch from `agent-chat-management` **before plan review or implementation**; commit subsequent plan revisions, implementation, checks and review fixes on that task branch. Keep the PR stacked against #47 until the dependency is resolved. No plan, workflow or implementation commits may go directly to `main`. Both PRs require separate owner approval before release.
- Report exact commits, tests, residual risks and proposed release bump. Leave `VERSION` and `CHANGELOG.md` alone until an owner-approved merge.
