# MEŽS

MEŽS exposes multiple AI integrations through one asynchronous HTTP API and one React frontend. Integrations may use a browser session, a native/local runtime, or a direct service API; the MEŽS core does not assume which transport an integration needs.

Current built-in integrations are ChatGPT Web, ChatGPT Account, and Gemini Web. A deterministic Mock integration is used by the test suite.

## Architecture

```text
(unchanged architecture documentation remains above)
```

## Known Issues / TODO

### Process isolation

- MEŽS services must be started in a detached process/session.
- The controlling shell must not be terminated together with the service it starts.
- Development tooling should provide independent start/stop/restart handling instead of relying on killing shared process trees.
