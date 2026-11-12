# Agent runtime — current behavior

This file describes the **current behavior contract**, not the implementation plan or internal architecture.

- Ordinary chats work without enabling Agent features. The Agent host adds policies and execution endpoints to the normal API.
- Agent API and Agent Web listen on loopback addresses. Agent Web only proxies to the loopback Agent API.
- An Agent may run host shell commands only when permitted by its selected policy. Host shell execution is **not sandboxed**.
- Agent command-shaped markers nested inside Markdown code or comments, raw CodeBlock/WritingBlock source, or supported static rich-layout containers are presentation only. Only separately recognized Agent commands enter policy validation and execution; incomplete protected presentation regions stay inert.
- Shell commands use the configured workspace. Caller-supplied environment variables are rejected unless allowed by policy. `MEZHS_*` variables are reserved.
- Queued Agent tasks survive Agent API restarts. Started shell actions reconnect to their durable Executor records instead of running again blindly.
- Agent Web searches and filters Agent conversations only, supports shared groups, displays activity and counts, and offers individual and bulk chat deletion.
- The normal Core chat, message, category and remote-conversation state is persisted in a Core-owned SQLite database. Legacy JSON/JSONL history is imported once, transactionally; original files remain untouched as backups.
- Agent-host single/bulk DELETE routes request normal Agent cancellation and immediately delete Agent chat/execution/staged-attachment references before Core chat/message records, one SQLite transaction per store. They do not wait for Agent tasks or active Core messages; concurrent work may fail, make deletion fail, or leave orphaned Agent records. No deletion-specific admission gate is maintained.
- Normal Agent cancellation requests termination of associated active Executor shells during deletion, without waiting for shell termination. Executor owns its own execution records and kill behavior; shared uploaded files are retained. There is no cross-store rollback or durable deletion tombstone. An interruption after the Agent commit may leave a Core-only chat, which is safe to delete on retry.
- Groups are shared between ordinary and Agent chats in the same API host. Deleting a group unassigns all its chats without deleting conversations.

See [architecture](../../docs/architecture.md) for how these contracts are implemented.

Update and review this specification whenever an implementation changes these behaviors. Do not duplicate feature-by-feature plans here.