# ChatGPT Account and anonymous guest integration: October 2026 handoff

**Read this when either ChatGPT Account or anonymous guest integration breaks.** This is an incident record and debugging playbook, not a stable protocol specification. Current as of 2026-10-14, review branch `agent-model-presets`, PR [#21](https://github.com/DreigoUndead/Mezhs/pull/21). Follow `.agents/skills/systematic-debugging/SKILL.md`, `software-change` and `repository-change-workflow` before editing. Do not merge a review branch without explicit post-review approval.

## Executive summary

MEŽS's authenticated ChatGPT Account integration (`chatgpt-web-account`; example connection `chatgpt-sub`) originally received HTTP **403** from `POST /backend-api/f/conversation`: “Unusual activity has been detected from your device.” A genuine request from the same logged-in ChatGPT frontend succeeded. Passive browser network tracing showed the real request had additional Sentinel header **names** beyond the conduit token.

The first attempt restored an **old** frontend security-provider signature lookup. That predicate no longer matched the currently loaded Rspack implementation. Aggregated export counts led to the actual native coordinator at `lXE.f`, which receives a requirements callback and prepares the headers. MEŽS now calls that **browser-owned** coordinator and supplies an authenticated `POST /backend-api/sentinel/chat-requirements/prepare` callback using `{ "p": requirementsKey }`. The native helper performs proof/Turnstile handling and constructs the headers; MEŽS forwards only its validated allowlisted Sentinel headers. It does **not** synthesize or cache security tokens or automate UI interactions for production sending.

A **separate intermittent** failure remained: `ChatGPT Rspack runtime is not loaded.` The code looked up the bundle in `performance.getEntriesByType("resource")` before *every* submission. Resource Timing is a mutable history, **not** a durable loaded-module registry. After `lXE.f` has been verified in a renderer document, MEŽS now keeps **only the function reference** on that document's `globalThis` under `Symbol.for("mezhs.chatgpt.nativeRequirementsProvider")`. Even if Resource Timing loses the entry, subsequent turns can call that function to obtain **fresh** per-turn requirements. A new document has no cached reference and still fails closed if discovery has not happened.

**Live result after these fixes:** the user’s 2026-10-13 Agent log has **5 completed Agent executions, 4 completed Shell executions, 0 Agent errors**. Tasks included multiple ChatGPT turns, file counts/names, size calculation, document reading and Agent follow-up. This is real integration evidence, not just mocks. It is *not* a promise that an undocumented upstream API will remain stable.

**Causality discipline:** The missing Sentinel header names and subsequent successful corrected flow strongly indicate a protocol mismatch, but the complete upstream reason for the original HTTP 403 was not independently proven. An absent Resource Timing URL similarly **does not prove** the JS module was unloaded. Keep observed facts separate from hypotheses.

## Anonymous guest incident: completed blank replies versus login required (2026-10-13/14)

This is a **different path** from the authenticated Account transport above. Anonymous ChatGPT uses `operations.sendPrompt` in `integrations/Mezhs.Integrations.ChatGpt/browser/chatgpt.ts` with the guest web composer and DOM transcript; authenticated Account `newChat`/`send` uses semantic requests, native requirements and conversation IDs. Do not transfer guest DOM assumptions to Account sending, or apply the Account Rspack repair to guest failures without evidence.

### Incident A: a completed assistant with no readable text

- The original MEŽS Agent incident happened on a **follow-up** after an earlier successful reply. Its Octane transcript showed a new assistant element with `data-message-complete` but no readable reply for more than five seconds. This is a **response/display-stage** symptom: a conversation had already begun. The precise failing transport stream was **not** captured, so the underlying cause remains unknown.
- In separate successful guest captures, `data-message-complete` appeared about **44–2,625 ms before** the reply text became readable. Some response streams ended before DOM text appeared. Incremental `/unauth-mweb/conversation/updates` NDJSON events may contain useful earlier `content` patches even when a final `complete` event has an empty `markdown` field. Therefore **neither the completion attribute nor an individual empty final markdown event proves a permanently blank response**.
- Replaying the same reconstructed prompt often succeeded. That rules out a simple deterministic prompt rejection, but does **not** identify whether the original failure was rendering, a missing content event, or another upstream transient.
- Existing mitigation (commits `31db329`, `ee20bca`): after **10 empty completed-message polls** at 500 ms intervals, return `empty_guest_reply`. Only a `newChat: true` attempt may retry once in a fresh chat; an existing conversation (`newChat: false`) must **not** replay a turn, especially a follow-up after executed Agent commands. This is bounded recovery, **not proof that the underlying issue is fixed**.

### Incident B: the guest conversation never starts

A separate live reproduction used an isolated Electron guest session with Chromium-native text input and a **trusted** mouse click. The send button was enabled; the form was valid and its `submit` event fired. The frontend then prevented the native HTML form submission. The following **browser-owned** fetch responses were observed:

| Guest request | Observed result |
| --- | --- |
| `/unauth-mweb/sentinel/chat-requirements/prepare` | HTTP 200 |
| `/unauth-mweb/conversation/prepare` | HTTP 200 |
| `/unauth-mweb/sentinel/chat-requirements/finalize` | HTTP 200, JSON boolean `force_login: true` |
| `/unauth-mweb/conversation/updates` | **Not initiated**; no user or assistant turn appeared |

A 200 from preparation/finalization means the request completed, **not** that guest conversation creation was permitted. The explicit `force_login: true` establishes that the frontend was being instructed to require login. It does **not** establish *why* (rate limit, anti-abuse policy, guest quota and other eligibility restrictions remain unverified hypotheses). No evidence connects this new **no-submit** failure causally to the original **completed-but-empty** reply. Changing from synthetic `send.click()` to trusted CDP input/click did not restore submission.

**Implementation (commit `0f561ac`):** The guest adapter temporarily observes the frontend's *existing* `fetch` calls during one `sendPrompt` attempt. It inspects only a cloned finalization response for `force_login === true`, without constructing challenge requests, capturing tokens or replacing response content; the original `fetch` is restored in `finally`. An explicit login requirement returns **non-retryable** `guest_login_required` (also during send-button polling). If the click produces no new user/assistant transcript element, it returns `guest_submission_timeout` after approximately **30 seconds** rather than hanging. A timeout **does not imply** `force_login`; no special retry is added for it. The existing `empty_guest_reply` handling is unchanged. The guest endpoint/field is an **undocumented, unstable** frontend contract: if the field is absent, retain a generic failure/timeout instead of assuming authorization or inventing a reason.

### Safe diagnostic procedure for the next guest incident

1. **Classify by the first missing stage:** editor/button ready -> form submit -> requirements prepare/finalize -> guest conversation request/stream -> user/assistant transcript -> assistant completion -> readable content. Check the new user turn as well as the assistant marker. *No user turn* is not an *empty completed assistant*.
2. Capture bounded timestamps and safe facts: endpoint **paths**, response **status**, booleans such as `force_login`, whether a request/event occurred, transcript element counts, completion state and text **lengths**. When needed, inspect sequence of incremental stream event **types** rather than assuming the final event is the whole answer. Avoid storing full prompt text.
3. Install observational hooks **before** composer input/navigation where possible: in the captured flow, prepare/finalize requests could begin **before** the send-button click. Distinguish a trusted click and intercepted form submit from actual accepted conversation creation. If using a test `fetch` wrapper, restore it and avoid persisting it in the page.
4. Never dump or check in cookies, challenge/proof payloads, tokens or full sensitive network bodies. Do not attempt to bypass `force_login` with repeated new guest sessions. The user's original failure is still unresolved without a **failing response-stream capture**.
5. Keep this separate from authenticated Account troubleshooting, whose request path and security coordination are different. Reuse the **observation method**, not ChatGPT-specific challenge mechanics.

**Verification for commit `0f561ac`:** `tests/chatgpt-guest-composer.test.js` covers rendered/completed Octane responses, one bounded new-chat blank retry, existing-conversation no-replay, explicit login requirement before or after the click, stalled submission, and successful non-login finalization. On 2026-10-14 all **61/61 Node tests** passed and the ChatGPT integration .NET project built with **0 warnings / 0 errors** using an isolated output directory. The in-place full solution build was obstructed by locks from already-running MEŽS and Visual Studio processes; this does **not** demonstrate a compilation failure. The login-required logic has deterministic tests, but **was not verified by a new successful guest conversation** because the live anonymous session required login. The local sanitized diagnostic report was written to `%TEMP%/mezhs-guest-capture-summary.json` (temporary, **not** committed); raw test probes also lived in `%TEMP%`, not this repository.

## Owners / moving parts

| Owner | Path | Purpose |
| --- | --- | --- |
| ChatGPT account JS adapter | `integrations/Mezhs.Integrations.ChatGpt/browser/chatgpt.ts` | Auth, model/preset, conduit and native requirements, semantic submission, conversation polling, citations |
| Account .NET wrapper | `integrations/Mezhs.Integrations.ChatGpt/ChatGptIntegration.cs` | Connection/session, transport requests and results |
| Electron window and CDP | `electron/main.js` | Logged-in renderer, browser session, passive request trace, diagnostic inspection |
| Diagnostics allowlist | `src/Mezhs.Integration.Browser/BrowserDiagnosticsModule.cs` | Exposes bounded browser diagnostic operations |
| Agent command grammar | `src/Mezhs.Agent.Api/Commands/Parser.cs` | `SH` and `DONE`, including exact escaped DONE |
| Agent/Executor | `src/Mezhs.Agent.Api`, `src/Mezhs.Executor` | Policy, durable command execution, command-result turns |
| Regression tests | `tests/chatgpt-rspack-signature.test.js`, `tests/chatgpt-account-api.test.js`, `tests/browser-provider-operations.test.js`, `tests/electron-rspack-inspection.test.js`, `tests/agent-protocol-result.ps1` | Contract, fail-closed, partial stream, replay safety, diagnostics and real shell smoke |

The conceptual request path is: logged-in Electron session → account identity/model/conversation setup → `/backend-api/f/conversation/prepare` (conduit token) → renderer-native `lXE.f` → `/backend-api/sentinel/chat-requirements/prepare` (security requirements callback) → browser-owned challenge/header work → semantic `/backend-api/f/conversation` submission → state polling tied to the **specific user message ID** → Agent `SH` and `DONE` handling if applicable. The *two different prepare endpoints have different jobs*.

## How the investigation found the contract

1. **Proved a success/failure difference.** A manual request in the real browser succeeded while MEŽS got HTTP 403. CDP's `Network.requestWillBeSent` / `ExtraInfo` showed the working request's header names `OpenAI-Sentinel-Chat-Requirements-Token`, `OpenAI-Sentinel-Proof-Token`, `OpenAI-Sentinel-Turnstile-Token` and `x-conduit-token`. Header **values** were neither captured nor fabricated.
2. **Tested the old hypothesis.** The historical scanner required `chatReq`, `turnstileToken`, `proofToken`, `getEnforcementTokenSync` and `getEnforcementToken` to appear in one function's source. Initial live counts were `bundles=1, imported=1, loaders=1, cachedModules=8935, inspectedExports=40054, matchingProviders=0`. Therefore the loader/import/cache worked, but its search predicate did not.
3. **Measured individual markers.** Another live run enumerated `8986` modules, `40202` exports and `15651` unique functions. Marker counts: `chatReq=2`, `turnstileToken=2`, `proofToken=3`, `getEnforcementTokenSync=0`, `getEnforcementToken=0`. Exactly two functions had three markers. Do **not** simply weaken the predicate to three markers: it would select ambiguously.
4. **Identified exact candidate exports via bounded inspection.** Both were in module `lXE`. `b` was a 198-character, arity-3 header builder; `f` was a 3,505-character, arity-1 asynchronous coordinator. Inspecting companion exports established: `c` processes proof-of-work, `d` generates a requirements key, `e` validates the response; `a` was an unidentified object.
5. **Recovered the callback contract.** `lXE.f` began conceptually with `let t=g(), n=p(await e(t))`. It expected `e(requirementsKey)` to return the server's requirements object, then handled challenges and returned `{ chatRequirements, headers, proofToken, requirementsKey, turnstileToken, ... }`. Rather than reimplementing browser proof/Turnstile logic, MEŽS supplies the authenticated prepare callback and accepts the browser helper's headers.
6. **Verified live end-to-end.** The user log then showed accepted prompts, served model/effort, tool use, completed responses and command-result follow-ups.
7. **Isolated intermittent discovery failure.** A subsequent log recorded `ChatGPT Rspack runtime is not loaded` both before a first turn and after a successful shell action when submitting command results. The fragile repeated Resource Timing lookup was repaired by **same-document caching of the verified function reference**, with a new-document fail-closed regression. This fixes a demonstrated weakness; the precise reason entries went missing in the user's renderer is not independently proven.
8. **Polished independent Agent/UI issues.** Exact standalone `<escape><DONE></escape>` is now recognized as completion, but escaped `SH` and shell bodies remain inert/opaque. For citations, the adapter uses message `metadata.content_references`, exact `matched_text` and `alt` Markdown when available; otherwise shows `[citation unavailable]` rather than inventing URLs. These are *not* authentication fixes.

### Historical Rspack exports, not stable names

| Module export (October 2026) | Function | Observed meaning |
| --- | --- | --- |
| `lXE.b` | `c(e,t,n)` | Build Sentinel requirements, proof and Turnstile header map |
| `lXE.c` | `m(e)` | Process proof-of-work requirement |
| `lXE.d` | `g()` | Generate requirements key |
| `lXE.e` | `p(e)` | Validate requirements response object |
| `lXE.f` | `async d(e)` | Request requirements via callback, process challenge(s), return generated headers |
| `lXE.a` | object | Not established |

Hardcoded `633146.<hash>.js` and `lXE.f` are minified upstream implementation identifiers, **not versioned public contracts**. Treat this record as a historical comparison point, not a recipe to guess future identifiers.

## Live diagnostic tools

**Documented local development endpoint:** `http://127.0.0.1:5060`; use the actual configured port/connection ID. Diagnostic endpoint pattern:

`POST /v1/diagnostics/connections/{connectionId}/browser/{operation}`

### Passive browser request trace

`networkTrace` uses Electron CDP to observe **manual** activity while the browser is visible/focused. It reports backend path, HTTP method, response status and **header names**, not request bodies or credentials. Initiate the relevant browser action manually while tracing:

```powershell
Invoke-RestMethod -Method Post `
  -Uri "http://127.0.0.1:5060/v1/diagnostics/connections/chatgpt-sub/browser/networkTrace" `
  -ContentType "application/json" -Body '{"waitMs":30000}' |
  ConvertTo-Json -Depth 12
```

Compare this trace with the failing MEŽS operation: same authenticated account, endpoint, request lifecycle and response. A trace alone is not proof that the whole Agent job completed.

### Targeted Rspack export inspection

`inspectRspackExports` accepts a module ID and up to four export keys. It reports function name, arity, source length and *bounded* source snippets/marker contexts. **It does not call these functions**.

```powershell
Invoke-RestMethod -Method Post `
  -Uri "http://127.0.0.1:5060/v1/diagnostics/connections/chatgpt-sub/browser/inspectRspackExports" `
  -ContentType "application/json" `
  -Body '{"moduleId":"lXE","exportKeys":["b","f"]}' |
  ConvertTo-Json -Depth 12
```

This example uses **historical** module identities. The inspection diagnostic itself depends on the runtime's Resource Timing URL and can fail when that URL disappears. Failure does **not** prove the module was unloaded. Avoid scanning everything or dumping secret runtime data; review code excerpts before sharing.

### Other browser diagnostics

**Trust boundary:** The Agent API is configured to reject non-loopback listeners; diagnostics share that localhost endpoint and authenticated browser session but do **not** have independent per-user authorization. Treat the local Agent HTTP listener as a privileged interface. Do not publish it via port forwarding, LAN binding, shared proxy, or external ingress. Loopback reachability is not a substitute for authentication against untrusted local processes.

`BrowserDiagnosticsModule.cs` allowlists: `snapshot`, `inspectPoint`, `click`, `type`, `key`, `screenshot`, `networkTrace`, `inspectRspackExports` and `show`. UI interaction methods are **for diagnostics only**, not ChatGPT production sending. Screenshot and page snapshots can contain personal data. General CDP mechanics are reusable for other browser integrations; ChatGPT Sentinel and Rspack implementation details are not.

## Tests and reproducibility

Use a clean Windows PowerShell checkout; the last command starts a **local test Agent host** and test shell execution:

```powershell
node --check integrations/Mezhs.Integrations.ChatGpt/browser/chatgpt.ts
node --test (Get-ChildItem tests -Filter *.test.js | ForEach-Object FullName)
dotnet build Mezhs.sln -c Release --nologo
pwsh -NoProfile -File tests/agent-protocol-result.ps1
```

- `tests/chatgpt-rspack-signature.test.js`: executes actual renderer-side coordinator script against a synthetic Rspack cache; validates callback/key, header filtering, HTTP failure without submission, lost Resource Timing in one persistent document, and a new-document fail-closed case.
- `tests/chatgpt-account-api.test.js`: submission and response ancestry; no blind resend after uncertain failures; partial stream and HTTP 429 polling; served-model metadata; citation references.
- `tests/chatgpt-guest-composer.test.js`: anonymous Octane composer, delayed content, completed blank replies, new-chat-only retry, non-replay for existing conversations, explicit login requirement and no-submission timeout. **Guest UI tests do not validate authenticated Account semantic sending.**
- `tests/electron-rspack-inspection.test.js`: bounded export inspection must **not invoke** inspected functions.
- `tests/browser-provider-operations.test.js`: browser transport and provider boundaries.
- `tests/agent-protocol-result.ps1`: parser safety, escaped `DONE`/`SH` behavior, actual test-host `SH` run, durable Executor evidence, same-turn completion and command-result continuation.

**Last checked Windows CI (2026-10-12):** 48/48 Node tests, JavaScript syntax, .NET Release build and Agent/Executor shell smoke all passed. There was one pre-existing unrelated WhatsApp compiler warning. The **subsequent live 2026-10-13 log** recorded 5/5 completed Agent jobs and 4/4 completed Shell jobs with no Agent error.

When tools are unavailable locally, `.agents/README.md` documents the repository's temporary GitHub Actions validation pattern: add the workflow **to the review branch**, write a sanitized PASS/FAIL report, inspect it, then delete **both** workflow and output before final review.

## Failure triage

| Symptom | What it establishes | Investigate next |
| --- | --- | --- |
| Older `ChatGPT Rspack runtime is not loaded` | No matching URL in Resource Timing on that request; **not proof of unload** | Compare new vs warmed renderer document, navigation and resource history |
| `native requirements provider was not observed in this document (resourceEntries=0, cachedProvider=false)` | No verified cached provider and no matching timing entry | Cold-start readiness and page lifecycle; do not blind-resend |
| `native requirements provider lXE.f is unavailable` | Runtime observed/imported but expected export incompatible | Check actual frontend module interface; minified IDs drift |
| `requirements prepare failed with HTTP 403` | Requirements callback failed before model submission | Auth/account state, browser requirements behavior; do not fake challenge tokens |
| `/backend-api/f/conversation` HTTP 403 | Conversation request rejected; **server-side reason unknown** | Compare known-good browser with MEŽS: method/path, sequencing, auth, conduit, **header names** |
| HTTP 429 during conversation polling | State read was rate-limited; turn may be accepted | Back off reads; never auto-resubmit an uncertain/accepted turn |
| Guest `guest_login_required` / finalization `force_login: true` | Explicit guest login requirement; no accepted turn was observed in the captured case | Stop without replay or guest-session cycling; upstream trigger remains unknown |
| Guest `guest_submission_timeout` | Click did not produce a detectable new transcript turn within ~30 s; **not** necessarily a login requirement | Check preparation/finalization and whether a conversation request was actually made; do not blindly resend |
| Guest `empty_guest_reply` | New assistant was marked complete but stayed unreadable through a bounded grace period | Distinguish DOM rendering from event stream; only the existing `newChat` path retries once, never an existing turn |
| Agent `Failed` after a completed `SH` | Shell side effect succeeded but later model communication failed | Inspect Executor evidence **separately** from the Agent message timeline; never rerun side effects blindly |
| Escaped `DONE` / raw citation markers | Parser or text presentation issue, not an HTTP transport problem | Exact escaped-marker grammar and `content_references` metadata |

## Next agent's checklist

1. Read this file plus `systematic-debugging` and `repository-change-workflow`. Confirm which branch and frontend version are actually running.
2. Find the **first incorrect stage**: session → model/init → conduit → native requirements → conversation submit → poll/reply → Agent parser → shell → command-result follow-up. Correlate Agent execution ID, message ID, conversation ID and Executor ID. A successful shell action is independent of the following model turn.
3. Compare one successful and one failing request from the **same build/session**. State observed facts, hypotheses, and what measurement would disprove each hypothesis.
4. Use passive `networkTrace` for protocol differences; capture paths, methods, statuses, header **names** and safe timings. Never capture/share cookie, bearer, Sentinel token, challenge payload or full request body values.
5. For frontend compatibility, inspect **a few** candidate exports and their caller/callback relationship. Avoid blanket 9,000-module scans, weak three-marker selection, DOM send automation, and guessed function invocation.
6. Add a deterministic regression for the precise failure mode (e.g., Resource Timing empty while same document survives). Preserve fresh per-turn requirements, do not cache proof data, and fail closed where the provider is unknown.
7. Validate JS, Node, .NET, Agent smoke and one **live multi-turn Agent run** (harmless `SH` → durable shell result → command-result model submission → final `DONE`). Testing only the first prompt misses the regression we had.
8. Review changes in **one PR**, leave it unmerged until the user explicitly approves *the completed reviewed changes*.

## Known remaining risks and architectural notes

- `633146.<hash>.js` and `lXE.f` are minified private frontend identities; either can change with a frontend deployment. Model catalogs, conduit flow, session state, citation metadata and conversation shape are similarly unversioned. Avoid promises that the integration is future-proof.
- The same-document function cache **only** helps after the provider was found once in that document. A true cold-start/new-document failure still needs lifecycle evidence; do not paper over with arbitrary sleeps, reload loops or retries.
- Long-term improvement: find a **single unambiguous native interface contract** if the current frontend exposes one, with a fail-closed test; do not grow a list of guessed module IDs or replicate security work.
- The Agent/Executor boundary is valuable: host execution evidence is durable, and follow-up AI communication can fail independently. Recover safely without duplicating commands. Keep cause/correlation IDs.
- **Reuse the investigation method**, generic CDP diagnostics, tests and Agent auditing across Gemini/Grok/other integrations. **Do not reuse ChatGPT-specific Sentinel tokens, module names or endpoints** as generic abstractions.
- Keep diagnostics private and redacted. The authenticated browser owns requirements processing; never fabricate credentials or challenge results, extract token values for logging or commit test-session data.

**Core lesson:** Compare the working browser with the failing semantic request, isolate the first wrong state, identify the actual browser-owned interface, test both endpoint **and document lifetime**, and confirm success in a real multi-turn Agent execution. A compile or mock alone did not establish compatibility.

## October 15, 2026: Account attachment transfer diagnosis

Observed on the active authenticated ChatGPT Account browser (without submitting a chat message):

- Native Windows file selection added temporary text attachments to the unsent composer. The browser made a successful POST to /backend-api/files/upload_reservations/{file_id}/claim_and_finish (HTTP 200), followed by file_upload_action_suggestions (HTTP 200). This establishes native completion of a reservation claim, not the full initial byte-transfer contract.
- A fresh file with distinct bytes behaved the same way. The current networkTrace implementation observes the main renderer and deliberately filters out external storage requests, so it did not capture upload creation or the storage PUT. Do not infer that these omitted requests never occurred.
- The MEZS API adapter instead reached POST /backend-api/files, then got HTTP 400 from its PUT to the returned upload_url, before process_upload_stream or semantic message submission. Both a text document and a PNG failed at that same stage. The specific cause remains unknown.
- An isolated Electron session.fetch local HTTP test sent a Node Buffer with exact binary bytes, a correct content length and requested Azure headers, returning HTTP 201. Thus a general Electron/Buffer inability to transmit bytes is not the explanation; this does not verify a real Azure signed URL or the ChatGPT upload contract.
- The native file chooser was initially positioned outside the visible desktop. Moving it onscreen and typing the full fixture path through keyboard events, then Enter, worked. Programmatic SetWindowText and direct Open-button messages did not complete the selection. This is a diagnostic UI detail, not an integration fix.
- A safe diagnostic was added to chatgpt.ts: on a failed storage response it appends only a strictly validated x-ms-error-code header to the HTTP status, never a response body, signed URL, or header value not matching the allowlist. Account regression tests cover valid and unsafe codes. Full Node suite: 74 tests passed; integration .NET build: zero warnings/errors.

Still needed before enabling capabilities:

1. Run the updated adapter after an authorized natural browser reload/recycle; the currently running sidecar has the older module cached. Record only the HTTP status and validated storage error code. Do not restart the active agent or copy locked authentication stores merely to force this test.
2. Compare a complete successful native upload (including possible worker and external storage traffic) with the failed adapter sequence. In particular, establish whether upload reservations, signed destination headers and completion/processing steps differ. Current page-only networkTrace cannot show that full lifecycle.
3. Fix the evidenced first incorrect step; add regressions for new and existing conversations and documents/images. Perform a live successful transfer, processing and semantic response before changing FileInput/ImageInput from false. Never silently fall back to DOM send or retry a submitted turn.

---


## October 16, 2026: MissingRequiredHeader follow-up

- A live newChat upload of a small test document against the authenticated chatgpt-sub sidecar reached storage and failed before semantic submission with HTTP 400 and validated x-ms-error-code MissingRequiredHeader. This narrows the server-reported class of failure, but the specific missing header was not exposed.
- In the adapter, x-ms-blob-type: BlockBlob and x-ms-version: 2020-04-08 were supplied only when upload_url.hostname ended with .blob.core.windows.net. A signed Azure-compatible storage destination on another hostname could therefore omit the required blob-type header. This is a concrete adapter bug; whether it was the exact cause of the live error still requires verification.
- Added a regression that uses a custom HTTPS storage hostname for one upload while retaining the canonical Azure hostname for another. Before the fix, the regression failed because x-ms-blob-type was undefined. After making the blob headers unconditional for this direct PUT path, all 29 ChatGPT Account tests passed.
- The active sidecar loaded its browser module before the source change. The module is embedded into the .NET integration assembly, written to the connection modules folder on first use, and cached within the Electron process. Live retesting therefore needs a future controlled host/sidecar lifecycle after updating the embedded assembly; replacing the on-disk file alone does not reload it. The ongoing MEZS agent process was not interrupted.
- Keep FileInput and ImageInput disabled until a real document and image upload each completes transfer, processing and exactly one semantic turn. No live success has yet been established.

---
