# Browser provider safe-read resilience
ID: browser-provider-read-resilience
Level: **Standard** — one provider-owned catalogue-read repair; no shared transport, authentication, concurrency, or prompt lifecycle changes.
Branch: `browser-provider-read-resilience`
Base: `main` @ `70be3d41a26cbf106e0db8ac0e86c29846ec1a0c`
Reference only: unmerged ChatGPT Account PR #58; do not edit it or depend on it.

## Evidence and boundary inspection
- Grok `browser/grok.ts`: `discoverModeCatalog()` makes `POST https://grok.com/rest/modes` with only `{locale}`, credentials and `cache: no-store`. It consumes `response.json()` with **no transport recovery**. Transport rejection or interrupted response body escapes from `pageOperations.models` or `selectModel`. Existing contract tests exercise success/status paths but not transient interruption. This is a source-evidenced recovery gap, **not a confirmed real Grok outage**.
- `GrokAccountIntegration.cs`: `newChat` invokes page `selectModel` before page `sendPrompt`; model catalog failure is pre-submission. Prompt completion is UI-driven; it is never safe to rerun the whole operation after an ambiguous outcome.
- ChatGPT Guest `guest.js` and `entry.js`: one UI send click, then DOM polling; no provider safe-read HTTP transport to retry. Gemini `gemini.ts`: one UI send click then DOM polling; no provider safe-read HTTP transport. No network replay added to these providers; avoid expanding DOM automation.
- ChatGPT Account PR #58 independently retries known transient GET/HEAD reads and response bodies. Its existing branch stays untouched/unmerged. This change does not copy its account transport or alter C# browser bases.
- No evidence yet isolates Chromium, network or remote-server origin of a transport error.

## Goal and scope
Recover a transient Chromium network interruption of Grok's read-only model catalogue, before the prompt is submitted, without automatically resubmitting any turn. Keep all changes within Grok's existing provider module plus focused tests; do not introduce shared retry primitives or alter Electron browser transport, session/login, policy, configuration, ChatGPT Account/Guest, Gemini, or model-selector DOM mechanics.

## Semantics and implementation
- **POST is not sufficient to infer mutation**: `/rest/modes` is a catalogue query, carries only locale, and its response is a mode list/default ID. There is no application-side state transition or message body. Retrying the *identical* catalog query after a transport exception is semantically repeatable for MEŽS, subject to the limitation that server-side analytics/rate limiting cannot be proven absent. Only this exact endpoint is allowlisted by the owning function. Never generalize POST retries.
- Use a small, bounded provider-local retry around **fetch and response body reading**, accepting only known transient Chromium codes (from `error.code` or a recognized code in the message). Delays 150/400/900 ms, at most four requests total. No retry for an HTTP response (including 401, 403, 404, 429 or 500), malformed successful JSON, bad catalog data, or unknown errors.
- Preserve original failure after exhausting attempts. No raw error text or payload logging. Log only fixed endpoint category (`/rest/modes`), method POST, code and retry count/delay; no cookies, tokens, locale body, URLs with user identifiers or full exception messages.
- No caller-level replay. A failed catalogue lookup aborts *before* the single `sendPrompt` call. A success continues with exactly one native UI click. Authorization cookie checks remain unchanged.
- If test/source examination demonstrates that the catalogue POST mutates remote user state or that broader shared coordination is required, **stop and request the owner decision**; reclassify High-risk and obtain independent plan review before architecture changes.

## Engineering sanity questions (.agents/coding.md)
1. **Ownership/state:** Grok page module owns `/rest/modes` semantics; no shared mutable retry state or caller coordination.
2. **Duplicate mechanisms:** PR #58 owns separate ChatGPT Account GET/HEAD behavior, not a common browser contract; reuse conceptual error classification but do not depend on unmerged code.
3. **DOM convenience:** keep provider catalogue API; no new selectors, clicks or DOM fallback.
4. **Async:** only actual fetch, body read and bounded backoff require async; parsing/normalization stay synchronous.
5. **Breakage/security:** check cookies/credentials, 401/403/404, other HTTP errors, JSON parse errors, transient exhaustion, log redaction, and a one-click-per-turn invariant.
6. **Maintenance/coupling/state:** one small provider-local helper; no new config, global state, registry, base class or dependency.
7. **Necessary fallbacks:** only bounded transient network errors; no blanket errors or HTTP response retries.
8. **Repeated decisions:** one owned catalogue operation makes the replay decision internally; no global transport rule.
9. **Shared base/framework:** not justified: GET/HEAD ChatGPT Account and semantic-POST Grok have distinct ownership; common abstraction would increase coupling.
10. **Real repetition:** only this Grok read and a separate unmerged provider case exist; no demonstrated shared API/lifetime requirement.
11. **Simple/hard to misuse:** retry cannot be invoked for prompt sends; no extra click or turn restart path.

## Acceptance and developer verification
- [ ] `/rest/modes` recovers thrown `ERR_HTTP2_PROTOCOL_ERROR` on fetch and transient body-consumption interruption, producing normalized catalog.
- [ ] Network-error exhaustion is bounded, original error propagates, and logs contain no user/provider secrets.
- [ ] Authorization HTTP 401/403, rate-limit 429, server HTTP 500, malformed JSON and unexpected errors perform only one request.
- [ ] Grok model-selection path resumes after a transient read and sends at most one prompt; catalog failure does not invoke send.
- [ ] Existing Grok auth/model/native-send and ChatGPT Guest/Gemini operation contracts remain intact.
- [ ] No changes to `VERSION`, `CHANGELOG.md`, PR #58, or `main`.
- Added `tests/grok-model-catalog-resilience.test.js` (six failure-path tests) and strengthened one-click assertion in `tests/grok-browser-module.test.js`.
- Developer checks to run on the exact candidate SHA: `node --test tests/grok-model-catalog-resilience.test.js tests/grok-browser-module.test.js tests/browser-provider-operations.test.js tests/chatgpt-guest-composer.test.js`. Local checkout not available in this GitHub-only execution; use PR CI to obtain actual run evidence. Do not claim PASS without a run.
- Source review: `discoverModeCatalog` alone contains retry loop; `sendPrompt` remains a single click and `newChat` still invokes selectModel before send. No additional module dependencies were introduced, so no codegraph edge refresh was necessary.

## Workflow
Plan committed on the task branch first. Keep this Standard scope; archive final plan at `specifications/implemented/browser-provider-read-resilience/plan.md` before independent code review. Review **before final testing**. Independent reviewer must report PASS/FAIL/BLOCKED; separate tester useful because of interrupted response-body semantics. Do not claim browser-live reproduction from mocks. Suggested release: PATCH, only after separate owner approval; leave VERSION/CHANGELOG unchanged.
