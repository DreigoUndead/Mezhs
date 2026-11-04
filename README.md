# MEŽS

MEŽS exposes ChatGPT, Gemini, Grok, and other AI integrations through one asynchronous HTTP API and a shared React frontend. Integrations may use browser sessions, local runtimes or direct APIs. Agent functionality adds policy-controlled orchestration and durable shell execution without being required by the generic chat application.

**Current release candidate:** `0.1.1` (2026-11-04; not released until Monkey approves and it merges into `main`). Latest governed change: standardized development workflow; see [changelog](CHANGELOG.md). Earlier app development is recorded in Git history.

## Documentation

- [Architecture and security boundaries](docs/architecture.md)
- [Development instructions](AGENTS.md)
- [Current behavior specifications](specifications/current/)
- [Changelog](CHANGELOG.md)

**Security:** `Mezhs.Agent.Api` can run policy-approved commands on the host OS; it is **not a sandbox**. Current Agent endpoints are intended for loopback only. Untrusted event content must never be given unrestricted shell authority.

## Components

- `Mezhs.Api`: generic core HTTP composition.
- `Mezhs.Agent.Api`: same core composition with Agent orchestration and policy.
- `Mezhs.Executor`: independent durable shell/process owner.
- `Mezhs.Web.Lib`: shared React chat UI for normal Web and Agent Web.

Setup and integration details remain in the component sources and manifests.