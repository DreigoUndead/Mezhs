# ChatGPT Account private-API repair: October 2026 handoff

**Read this when a ChatGPT Account integration breaks.** This is an incident record and debugging playbook, not a stable protocol specification. Current as of 2026-10-13, review branch `agent-model-presets`, PR [#21](https://github.com/DreigoUndead/Mezhs/pull/21). Follow `.agents/skills/systematic-debugging/SKILL.md`, `software-change` and `repository-change-workflow` before editing. Do not merge a review branch without explicit post-review approval.

## Executive summary

MEŽS's authenticated ChatGPT Account integration (`chatgpt-web-account`; example connection `chatgpt-sub`) originally received HTTP **403** from `POST /backend-api/f/conversation`: “Unusual activity has been detected from your device.” A genuine request from the same logged-in ChatGPT frontend succeeded. Passive browser network tracing showed the real request had additional Sentinel header **names** beyond the conduit token.

The first attempt restored an **old** frontend security-provider signature lookup. That predicate no longer matched the currently loaded Rspack implementation. Aggregated export counts led to the actual native coordinator at `lXE.f`, which receives a requirements callback and prepares the headers. MEŽS now calls that **browser-owned** coordinator and supplies an authenticated `POST /backend-api/sentinel/chat-requirements/prepare` callback using `{ "p": requirementsKey }`. The native helper performs proof/Turnstile handling and constructs the headers; MEŽS forwards only its validated allowlisted Sentinel headers. It does **not** synthesize or cache security tokens or automate UI interactions for production sending.

A **separate intermittent** failure remained: `ChatGPT Rspack runtime is not loaded.` The code looked up the bundle in `performance.getEntriesByType("resource")` before *every* submission. Resource Timing is a mutable history, **not** a durable loaded-module registry. After `lXE.f` has been verified in a renderer document, MEŽS now keeps **only the function reference** on that document's `globalThis` under `Symbol.for("mezhs.chatgpt.nativeRequirementsProvider")`. Even if Resource Timing loses the entry, subsequent turns can call that function to obtain **fresh** per-turn requirements. A new document has no cached reference and still fails closed if discovery has not happened.

**Live result after these fixes:** the user’s 2026-10-13 Agent log has **5 completed Agent executions, 4 completed Shell executions, 0 Agent errors**. Tasks included multiple ChatGPT turns, file counts/names, size calculation, document reading and Agent follow-up. This is real integration evidence, not just mocks. It is *not* a promise that an undocumented upstream API will remain stable.

**Causality discipline:** The missing Sentinel header names and subsequent successful corrected flow strongly indicate a protocol mismatch, but the complete upstream reason for the original HTTP 403 was not independently proven. An absent Resource Timing URL similarly **does not prove** the JS module was unloaded. Keep observed facts separate from hypotheses.

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
