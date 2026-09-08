# MEŽS

MEŽS exposes multiple AI integrations through one asynchronous HTTP API and one React frontend. Integrations may use a browser session, a native/local runtime, or a direct service API; the MEŽS core does not assume which transport an integration needs.

Current built-in integrations are ChatGPT Web, ChatGPT Account, Gemini Web, Grok, and deterministic Mock integrations used by the test suite.

## Agent runtime

`Mezhs.Agent.Api` can execute policy-approved commands on the host operating system. That is an intentional capability boundary, not a sandbox.

The current Agent API trust boundary is the local machine. Both `Mezhs.Agent.Api` and `Mezhs.Agent.Web` must bind only to loopback addresses, and Agent Web may proxy only to a loopback Agent API. There is deliberately no bearer-token layer between these cooperating local processes; if MEŽS later gains a remotely reachable Agent entry point, authentication belongs at that external boundary.

Loopback does not make external event content trusted. A future WhatsApp/Jira/reminder listener may feed untrusted text into a local Agent. Policies for those sources must therefore expose only the capabilities they are intended to have; unrestricted `SH` still means the privileges of the account running MEŽS Agent. Prefer narrow structured tools or real OS isolation where an external source must not have host-shell authority.

Agent shell commands always run from the configured `workspace`. Caller-provided environment variables are denied unless the selected policy explicitly allows their names; `MEZHS_*` variables are reserved for runtime execution context.

## Architecture

Agent responsibilities are split by semantic owner. `Mezhs.Agent.Api` owns policy, durable root reasoning/admission state, same-chat serialization and root cancellation. `Mezhs.Executor` owns host shell/process lifetime and durable shell execution history. Agent policy validates the concrete `SH` body before Executor receives it; Executor itself does not interpret Agent policy.

Executor makes each host execution independently observable through one durable SQLite row, one runtime owner process and one owned shell/process. It supports detached execution, heartbeat, timeout, kill, restart lineage, self-restart handoff and lazy reconciliation of stale active owners. Agent API and Agent Web project shell state from Executor instead of maintaining a second shell lifecycle copy.

`Mezhs.Sqlite` owns shared SQLite mechanics such as path handling, WAL/busy-timeout connections and schema helpers. AgentStore keeps Agent-specific durable state while ExecutorStore keeps execution-specific durable state.

SQLite is the source of truth for queued root Agent executions and admission capacity. Workers keep only a small in-memory wake signal, atomically claim eligible work, and enforce same-chat serialization before an execution becomes `Running`. Queued work survives Agent API restart. Work that had already started is requeued for reasoning recovery; previously launched `SH` blocks reconnect to their existing Executor rows using durable command identity rather than executing the host action again.
