# Agent runtime — current behavior

This file describes the **current behavior contract**, not the implementation plan or internal architecture.

- Ordinary chats work without enabling Agent features. The Agent host adds policies and execution endpoints to the normal API.
- Agent API and Agent Web listen on loopback addresses. Agent Web only proxies to the loopback Agent API.
- An Agent may run host shell commands only when permitted by its selected policy. Host shell execution is **not sandboxed**.
- Shell commands use the configured workspace. Caller-supplied environment variables are rejected unless allowed by policy. `MEZHS_*` variables are reserved.
- Queued Agent tasks survive Agent API restarts. Started shell actions reconnect to their durable Executor records instead of running again blindly.
- Agent Web searches and filters Agent conversations only, supports shared groups, displays activity and counts, and offers individual and bulk chat deletion.
- The normal Core chat, message, category and remote-conversation state is persisted in a Core-owned SQLite database. Legacy JSON/JSONL history is imported once, transactionally; original files remain untouched as backups.
- Agent-host single/bulk DELETE routes request normal Agent cancellation and immediately delete Agent chat/execution/staged-attachment references before Core chat/message records, one SQLite transaction per store. They do not wait for Agent tasks or active Core messages; concurrent work may fail, make deletion fail, or leave orphaned Agent records. No deletion-specific admission gate is maintained.
- Normal Agent cancellation requests termination of associated active Executor shells during deletion, without waiting for shell termination. Executor owns its own execution records and kill behavior; shared uploaded files are retained. There is no cross-store rollback or durable deletion tombstone. An interruption after the Agent commit may leave a Core-only chat, which is safe to delete on retry.
- Groups are shared between ordinary and Agent chats in the same API host. The Core `DELETE /v1/categories/{categoryId}` route still only unassigns chats; it never deletes them.
- The normal API Web UI group-delete dialog offers **Unassign conversations**, **Delete conversations**, and **Cancel** with a refreshed group count, and requires reconfirmation if membership changed since opening. The destructive option uses the existing single/bulk chat deletion endpoint (including Agent host cleanup when applicable) and then category DELETE. Requests are time-bounded, and the UI attempts to reconcile uncertain outcomes and clear a deleted active selection. The dialog is a native keyboard-modal with focus restoration. This is **not an atomic cross-request operation**: chats moved to another category after final preflight may still be deleted by ID, newly assigned chats may instead be unassigned, and a partial failure may leave the category for retry. Agent Web's group delete control remains unassign-only.

See [architecture](../../docs/architecture.md) for how these contracts are implemented.

Update and review this specification whenever an implementation changes these behaviors. Do not duplicate feature-by-feature plans here.