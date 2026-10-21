# ChatGPT browser integrations: diagnostics and known risks

This is a **debugging reference**, not a stable upstream protocol specification. Last investigated 2026-10-13/14; see [PR #21](https://github.com/DreigoUndead/Mezhs/pull/21) and Git history for incident detail. Before changing behavior, follow `systematic-debugging`, `software-change` and `repository-change-workflow` under `.agents/skills/`.

## Two distinct integrations

- **Authenticated Account** (`chatgpt-web-account`): `integrations/Mezhs.Integrations.ChatGpt/ChatGptIntegration.cs` and `browser/modules/account-*.js`. Uses the logged-in Electron renderer for native requirements preparation and semantic backend conversation requests. `electron/main.js` owns the browser transport; `BrowserDiagnosticsModule.cs` exposes bounded diagnostic operations.
- **Anonymous guest** (`chatgpt-web`): `browser/modules/guest.js`. Uses the guest composer and DOM transcript; its submission and failure modes are different. Never apply Account Rspack assumptions to guest operation.

Keep transport errors separate from Agent parser/Executor failures. An Agent follow-up can fail *after* its `SH` command succeeded; inspect durable Shell evidence before considering replay.

## Account: native Sentinel requirements

The observed submission sequence is:

1. Logged-in session, conversation/model initialization and `POST /backend-api/f/conversation/prepare` obtain a **conduit** token.
2. The renderer's own requirements coordinator prepares per-turn Sentinel headers. In October 2026 it was minified Rspack export `lXE.f`, located in `633146.<hash>.js`. MEŽS supplies its callback to `POST /backend-api/sentinel/chat-requirements/prepare` with `{ "p": requirementsKey }`.
3. The **native browser helper**, not MEŽS, performs proof/Turnstile work and generates headers. MEŽS validates and forwards the allowlisted headers to `POST /backend-api/f/conversation`.
4. Conversation state/reply polling is tied to the submitted **user message ID**, not just any visible assistant message.

The two `prepare` endpoints are **different protocols**. Do not fabricate, persist or replay Sentinel challenge/proof tokens, or replace semantic submission with UI clicking.

**Upstream instability:** `633146`, `lXE.f`, export shape/arity and model/conversation schemas are private implementation details. They can change on any frontend update. The current adapter in `account-transport.js` caches only a **verified coordinator function reference within the same renderer document** using `Symbol.for("mezhs.chatgpt.nativeRequirementsProvider")`. Each turn still obtains fresh requirements. Resource Timing entries can disappear without unloading a module; a new document has no verified cache and must fail closed. Do not add guessed IDs, fallback token synthesis or arbitrary navigation/sleep loops.

**Historical evidence, not proof of full causality:** MEŽS initially received HTTP 403 at `/backend-api/f/conversation` while a manual same-session browser request succeeded. Passive tracing found Sentinel **header-name differences**; a former all-marker function signature matched no exports, whereas bounded inspection identified `lXE.f`. After the native-coordinator repair, an October 13 live Agent run completed five Agent executions and four Shell executions, including multi-turn follow-up. This strongly supports a protocol mismatch, but the upstream cause of the original 403 was not independently proven. Later `Rspack runtime is not loaded` errors were consistent with loss of a Resource Timing entry, **not proof** of module unload.

## Guest: classify failures before any retry

Two historically observed symptoms are **not equivalent**:

- **Completed but unreadable response:** `data-message-complete` appeared with no readable text in a follow-up. Successful captures showed the completion marker could precede readable content by 44–2,625 ms; incremental `/unauth-mweb/conversation/updates` events can carry content even when a final `markdown` field is empty. The exact original failure cause remains unknown. The adapter allows a bounded grace period (`empty_guest_reply`); only a new-chat attempt may retry once. **Never blindly replay an existing conversation or command-result turn.**
- **Submission never accepted:** guest `/sentinel/chat-requirements/finalize` returned HTTP 200 with `force_login: true`, and no conversation update request or user turn followed. This is an explicit login-required result (`guest_login_required`), not an empty assistant response; the reason login was required is unknown. The adapter observes the browser's own finalization response without fabricating requests or token values. A no-turn timeout (`guest_submission_timeout`) does *not* establish a login requirement.

For new guest failures, locate the first missing transition: editor ready → submit event → requirements prepare/finalize → conversation request/updates → user turn → assistant completion → readable content. A clicked button or successful HTTP 200 is not proof a turn was accepted.

## Safe local diagnostics

The Agent API exposes privileged, **loopback-only** diagnostics; it does not independently authenticate local processes. Never expose it through LAN binding, port forwarding, a shared proxy or external ingress. Do not capture or commit cookies, bearer headers, Sentinel token values, challenge payloads, full request bodies or sensitive browser screenshots.

Endpoint pattern (use the configured port and connection ID):
`POST /v1/diagnostics/connections/{connectionId}/browser/{operation}`

- `networkTrace`: passively compare method, path, status, timing and **header names** from a genuine successful browser action versus a MEŽS failure. Observe rather than mutate.
- `inspectRspackExports`: inspect a **small** set of identified module exports for bounded function metadata/snippets without invoking them. Historical `lXE` keys are comparison evidence, not future identifiers.
- `snapshot`, `inspectPoint`, `click`, `type`, `key`, `screenshot`, `show`: diagnostic-only browser interactions; never use them as a replacement for Account semantic sending.

Example, with the relevant account window active:

```powershell
Invoke-RestMethod -Method Post `
  -Uri "http://127.0.0.1:5060/v1/diagnostics/connections/chatgpt-sub/browser/networkTrace" `
  -ContentType "application/json" -Body '{"waitMs":30000}' |
  ConvertTo-Json -Depth 12
```

Compare a **same-build, same-session** working request with the failing one. Keep failures distinct:

| Symptom | What is established / next check |
| --- | --- |
| No verified native provider / missing Resource Timing entry | Check document lifecycle, loaded bundle and coordinator cache; do **not** infer unload or blindly resend |
| Native export `lXE.f` unavailable | Frontend internals may have changed; inspect the current runtime interface |
| Requirements prepare HTTP 403 | Pre-submission authentication/requirements failed; inspect browser behavior, do not invent proof |
| Conversation POST HTTP 403 | Backend rejected request; compare sequence and **header names**, not guessed server motives |
| Conversation polling HTTP 429 | Read was rate-limited; back off, never resubmit an uncertain accepted turn |
| Guest login required | Stop without cycling guest sessions; upstream trigger unknown |
| Guest completed-but-empty | Check delayed rendering and stream event sequence; existing-chat replay is unsafe |
| Agent error after `SH` | Consult durable Executor ID/status before replay |
| Escaped `<DONE>` | Literal presentation, not completion; only standalone raw command markers execute |

## Regressions and verification

- `tests/chatgpt-rspack-signature.test.js`: genuine coordinator script in a synthetic Rspack cache, prepare callback, header filtering, lost Resource Timing and fresh-document fail-closed.
- `tests/chatgpt-account-api.test.js`: semantic request, response ancestry and model metadata, polling/rate limits, citation handling and replay safety.
- `tests/chatgpt-guest-composer.test.js`: delayed or empty guest text, login requirement, bounded timeouts and no replay in existing chats.
- `tests/electron-rspack-inspection.test.js`: export diagnostics must not invoke private functions.
- `tests/agent-protocol-result.ps1`: inert escaped examples, raw `SH`/`DONE`, durable shell results and continuation.

Use the permanent `.github/workflows/mezhs-core.yml` validation and relevant focused tests. Mock tests and compilation alone do not prove an undocumented upstream interface still works: where safe and authorized, also verify a **multi-turn** Account Agent flow (harmless `SH` → durable result → model follow-up → raw `DONE`). Record observed evidence and remaining uncertainty; leave PRs unmerged until explicit post-review approval.

**Guiding principle:** diagnose the first wrong stage, reuse the real browser-owned protocol mechanism, keep security data redacted, and never assume a successful shell or completed mock implies the next model turn succeeded.
