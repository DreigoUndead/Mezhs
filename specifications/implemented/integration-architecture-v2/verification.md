# Integration Architecture V2 — developer verification (not independent testing)

**Task branch:** `integration-architecture-v2`; draft PR #57; baseline `main` `70be3d41a26cbf106e0db8ac0e86c29846ec1a0c`. High-risk implementation with owner approval required for any merge.

## Baseline migration evidence

- Windows GitHub Actions at `07523e77023b1a91074e7e6f9ce8d644031d27f6`: **PASS** (run 43656161412). Core/Agent/API, provider JS, history contract, packaging, browser login/cancellation/limits and real Chromium/UI regression checks passed at that earlier SHA.
- An earlier authenticated **read-only** live ChatGPT Account models GET probe returned a catalog. This is not a live prompt-send or streaming-mutation proof.
- An independent code review of earlier commit `05b7a120df92ff8b2db20b751a8cdfe387e11567` returned **FAIL**, with shared typed HTTP execution missing and mock unsupported-model validation inconsistent. That review does not constitute approval of the subsequent rework.

## Rework evidence and scope

- Composed `IntegrationHttpClient`: typed GET/POST/PATCH, JSON, bounded C# SSE/NDJSON interpretation, ordinary `HttpClient` transport, and Electron-bound transport via existing session. `BrowserHttpExecutor` does not own a second browser/session.
- Authenticated Electron provider `typedHttp` now accepts bounded same-origin backend GET/POST/PATCH operations using provider-native token, cookie and integrity handling; uncertain mutations are dispatched only once. Provider-specific Sentinel/conduit handling remains in the established ChatGPT prompt-send path.
- ChatGPT Account model discovery and picker interpretation now use typed GET/C# mapping. The mock integration rejects unsupported selected model IDs.
- Developer-run on isolated checkout at `33cae4ed124b6e287e839c765fb216095c2f0c44`: six simulated Node typed HTTP tests **PASS**; `Mezhs.Integration.Contracts.Probe` **PASS** covering normal HttpClient and fake Electron transports, GET/POST/PATCH, SSE/NDJSON framing, cancellation/no-replay and typed ChatGPT model catalog.
- Full Windows CI run 43709251020 at that SHA reached browser regressions but **failed**: model discovery supplied a non-null placeholder GET body and account test fixture expected legacy `getModels`. Both were corrected in follow-up commit `01191630bea809254e163d841babcbd2c7edfec4`; its CI result must be inspected separately.

## Remaining verification

- New independent code review and independent high-risk testing of the **final** changed commit. Developer checks and green CI, when available, cannot substitute for independent testing.
- No proven live ChatGPT Account mutation/streaming transport test, simultaneous differing-model account prompts, or completed live guest-provider validation. Existing ChatGPT send path is intentionally retained rather than migrated. Do not infer live-provider behavior from simulated tests.
- No local Electron browser launch during typed HTTP rework; no merge, VERSION or CHANGELOG update.


## Code review 6 cleanup verification (2026-11-21)

- Integration catalog probe passed: typed HTTP, native model/effort mapping, disabled presets, duplicate displayed model labels and legacy model fallback.
- `node --test tests/browser-provider-operations.test.js tests/chatgpt-browser-package.test.js tests/chatgpt-account-api.test.js`: 59/59 PASS.
- `node --test tests/typed-http-feasibility.test.js tests/browser-provider-operations.test.js tests/chatgpt-browser-package.test.js`: 16/16 PASS.
- `node --test tests/integration-model-mismatch.test.cjs`: 2/2 PASS (React SSR `useLayoutEffect` warnings only).
- `npm run build --prefix src/Mezhs.Agent.Web`: PASS, TypeScript and Vite.
- `dotnet build Mezhs.sln -c Release --nologo -v:q`: PASS, one warning from `Mezhs.WhatsApp` and zero errors.
- `node --check` on changed browser modules and `git diff --check`: PASS.
- No final-SHA live provider or independent tester result is claimed.


### Chromium comparison at code review 6

`node --test tests/rich-model-ui-browser.test.cjs` **FAILED** on the assertion `Captured nested rich layout rendered as literal or indented code in Chromium`. The same test was rerun after temporarily reversing all three UI changes in this review (`MezhsChatApp.tsx`, `providers/contracts.ts`, `rich-model-ui-browser.test.cjs`); it **FAILED identically** at the same assertion. The review UI changes were then restored and `git diff --check` passed. The comparison indicates that the observed failure predates this review cleanup, but it remains an unresolved branch UI-test failure; do not report the full suite as PASS.
