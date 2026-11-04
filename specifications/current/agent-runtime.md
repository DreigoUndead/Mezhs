# Agent runtime — current behavior

This file describes the **current behavior contract**, not the implementation plan or internal architecture.

- Ordinary chats work without enabling Agent features. The Agent host adds policies and execution endpoints to the normal API.
- Agent API and Agent Web listen on loopback addresses. Agent Web only proxies to the loopback Agent API.
- An Agent may run host shell commands only when permitted by its selected policy. Host shell execution is **not sandboxed**.
- Shell commands use the configured workspace. Caller-supplied environment variables are rejected unless allowed by policy. `MEZHS_*` variables are reserved.
- Queued Agent tasks survive Agent API restarts. Started shell actions reconnect to their durable Executor records instead of running again blindly.

See [architecture](../../docs/architecture.md) for how these contracts are implemented.

Update and review this specification whenever an implementation changes these behaviors. Do not duplicate feature-by-feature plans here.