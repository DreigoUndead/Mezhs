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
