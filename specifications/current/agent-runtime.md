# Agent runtime — current behavior

This file describes the **current behavior contract**, not the implementation plan or internal architecture.

- Ordinary chats work without enabling Agent features. The Agent host adds policies and execution endpoints to the normal API.
- Agent API and Agent Web listen on loopback addresses. Agent Web only proxies to the loopback Agent API.
- An Agent may run host shell commands only when permitted by its selected policy. Host shell execution is **not sandboxed**.
- Shell commands use the configured workspace. Caller-supplied environment variables are rejected unless allowed by policy. `MEZHS_*` variables are reserved.
- Queued Agent tasks survive Agent API restarts. Started shell actions reconnect to their durable Executor records instead of running again blindly.
- Agent Web searches and filters Agent conversations only, supports shared groups, displays activity and counts, and offers individual and bulk chat deletion.
- The Agent host's standard single/bulk chat DELETE routes reject an entire request if any requested chat has queued/running core messages, nonterminal Agent executions or nonterminal Executor shells. Deletion does not cancel active work.
- Successful Agent-host deletion removes core transcripts and Agent execution/attachment state. Completed Executor execution history and shared uploaded files are retained. Durable deletion blocks prevent shell restarts and recover interrupted deletion on Agent API restart.
- Groups are shared between ordinary and Agent chats in the same API host. Deleting a group unassigns all its chats without deleting conversations.

See [architecture](../../docs/architecture.md) for how these contracts are implemented.

Update and review this specification whenever an implementation changes these behaviors. Do not duplicate feature-by-feature plans here.