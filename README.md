# MEŽS

MEŽS exposes multiple AI integrations through one asynchronous HTTP API and one React frontend. Integrations may use a browser session, a native/local runtime, or a direct service API; the MEŽS core does not assume which transport an integration needs.

Current built-in integrations are ChatGPT Web, ChatGPT Account, Gemini Web, Grok, and deterministic Mock integrations used by the test suite.

## Agent runtime

`Mezhs.Agent.Api` can execute policy-approved commands on the host operating system. That is an intentional capability boundary, not a sandbox.

Before starting Agent API or Agent Web, set `MEZHS_AGENT_API_KEY` to the same non-empty secret in both processes. Agent API accepts authenticated requests only and its configured listener must be loopback. Browser access goes through the same-origin `Mezhs.Agent.Web` proxy, which authenticates to Agent API server-side.

Agent shell commands always run from the configured `workspace`. Caller-provided environment variables are denied unless the selected policy explicitly allows their names; `MEZHS_*` variables are reserved for runtime execution context.

## Architecture

Agent responsibilities are intentionally split by owner: API boundary authentication/admission, durable execution/evidence persistence, policy compilation/evaluation, bounded worker scheduling, shell process lifecycle, and shared chat rendering. The dashboard consumes persisted command identity rather than reconstructing execution state from command text.

## Known Issues / TODO

### Process isolation

- MEŽS services still need a dedicated supervisor/launcher that owns detached process/session startup.
- The controlling shell must not be coupled to the lifetime of the service it starts.
- Development tooling should provide independent start/stop/restart handling instead of relying on killing shared process trees.

This is intentionally not implemented as a shell-script detachment workaround; it needs one explicit process-supervision owner.
