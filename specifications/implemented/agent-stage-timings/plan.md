# Agent stage timing and status clarity
ID: agent-stage-timings
Level: Small (owner-requested low-risk, additive diagnostics only). Same-chat review/checks; owner approval still required before merge.

## Goal
Make the Agent's long "Waiting for model" / "Processing command result" periods explainable, without changing ChatGPT submission, execution, polling, persistence schemas or concurrency behavior.

## Approach
1. Report meaningful existing activity transitions at the authenticated ChatGPT connection/session, authentication, model selection, conversation preflight, request preparation, submission, streaming and completed-message lookup boundaries. Preserve existing serialized session and per-turn native requirements. Never emit secrets or prompt contents.
2. Keep the existing activity history as the timing source of truth. Annotate the Agent's downloadable debug log with elapsed stage duration (measured between consecutive activity events, or terminal time); clarify that these are observed wall-clock intervals rather than proof of where time was spent.
3. Replace ambiguous Agent UI fallback labels, relying on actual provider activity whenever available; preserve command execution status priority.
4. Add focused non-network regression checks for statuses, timing formatting and unchanged request behavior. Refresh Codegraph only if useful for this small additive change; run focused tests/build/CI where possible.

## Out of scope
Streaming answer text, changing session serialization, retry behavior, backend API schemas, Electron polling or browser security protocols. Actual root-cause attribution requires a live warm/cold and concurrent run after instrumentation.

## Review/acceptance
- Visible Agent states distinguish waiting for a shared connection/browser, preparing the request, submitting, waiting for model activity, generating, collecting final reply, and executing commands.
- Downloaded debug log shows duration for each recorded activity state and shell execution, without credentials.
- Existing ChatGPT Account tests, Agent UI checks and build remain green.
- Do not merge, change VERSION/CHANGELOG or claim performance improvements until measured and owner-approved.
