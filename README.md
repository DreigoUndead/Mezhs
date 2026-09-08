# MEŽS

MEŽS exposes multiple AI integrations through one asynchronous HTTP API and one React frontend. Integrations may use a browser session, a native/local runtime, or a direct service API; the MEŽS core does not assume which transport an integration needs.

Current built-in integrations are ChatGPT Web, ChatGPT Account, Gemini Web, Grok, and deterministic Mock integrations used by the test suite.

## Agent runtime

`Mezhs.Agent.Api` can execute policy-approved commands on the host operating system. That is an intentional capability boundary, not a sandbox.

The current Agent API trust boundary is the local machine. Both `Mezhs.Agent.Api` and `Mezhs.Agent.Web` must bind only to loopback addresses, and Agent Web may proxy only to a loopback Agent API. There is deliberately no bearer-token layer between these cooperating local processes; if MEŽS later gains a remotely reachable Agent entry point, authentication belongs at that external boundary.

Loopback does not make external event content trusted. A future WhatsApp/Jira/reminder listener may feed untrusted text into a local Agent. Policies for those sources must therefore expose only the capabilities they are intended to have; unrestricted `SH` still means the privileges of the account running MEŽS Agent. Prefer narrow structured tools or real OS isolation where an external source must not have host-shell authority.

Agent shell commands always run from the configured `workspace`. Caller-provided environment variables are denied unless the selected policy explicitly allows their names; `MEZHS_*` variables are reserved for runtime execution context.

## Architecture

Agent responsibilities are intentionally split by owner: local-only API exposure, durable execution/evidence persistence, policy compilation/evaluation, durable worker scheduling, shell process lifecycle, and shared chat rendering. The dashboard consumes persisted command identity rather than reconstructing execution state from command text.

SQLite is the source of truth for queued root executions and admission capacity. Workers keep only a tiny in-memory wake signal, atomically claim eligible queued work from SQLite, and enforce same-chat serialization before an execution becomes `Running`. Queued work therefore survives an Agent service restart; only work that had actually started is recovered as `Interrupted`.

## Known Issues / TODO

### Process isolation

- MEŽS services still need a dedicated supervisor/launcher that owns detached process/session startup.
- The controlling shell must not be coupled to the lifetime of the service it starts.
- Development tooling should provide independent start/stop/restart handling instead of relying on killing shared process trees.

This is intentionally not implemented as a shell-script detachment workaround; it needs one explicit process-supervision owner.
