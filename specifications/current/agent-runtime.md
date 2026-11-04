# Agent runtime — current behavior

- Normal MEŽS works without Agent features. The Agent API extends the in-process core API; it is not a second API service.
- Both Agent API and Agent Web bind to loopback. Agent Web proxies only to loopback Agent API. There is currently no remote authentication boundary; external input remains untrusted.
- Agent policies decide command permissions. Host `SH` is not sandboxed. Agent policy validates shell commands before Executor; Executor owns durable process lifetime/history, while Agent API owns reasoning/admission, chat serialization, and cancellation.
- Agent shell execution uses the configured workspace; environment variable injection is denied unless explicitly authorized, and `MEZHS_*` is reserved.
- Queued root executions are persisted in SQLite; started operations reconnect to durable Executor records during Agent recovery rather than blindly replaying side effects.

See [architecture](../../docs/architecture.md) for service boundaries. Update these statements when actual behavior changes.
