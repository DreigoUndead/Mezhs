# Live Rspack provider discovery
ID: live-rspack-provider-discovery

## Classification

High-risk repair. The change is localized to the authenticated ChatGPT transport, but it touches the per-turn Sentinel security boundary and private frontend runtime introspection. Independent plan review, code review, and testing are required.

## Goal and scope

Replace Resource Timing-based discovery of ChatGPT's native requirements coordinator with discovery from the renderer document's live Rspack module registry.

The authenticated ChatGPT Account connection must continue to:

- use semantic backend conversation submission rather than DOM automation;
- let ChatGPT's native coordinator own proof and Turnstile work;
- request fresh Sentinel requirements for every turn;
- validate and forward only the existing allowlisted headers;
- fail before conversation submission when no unique verified coordinator exists.

Out of scope:

- reload, retry, delay, or eager-cache workarounds;
- synthesizing or persisting Sentinel tokens;
- replacing Account submission with UI clicks;
- migrating the connection to the documented OpenAI API;
- unrelated ChatGPT model, upload, conversation, or Electron lifecycle changes.

The existing Rspack inspection diagnostic and private-API debugging guide are in scope only where their Resource Timing assumptions must be corrected or where deterministic verification requires bounded timing-state diagnostics.

## Failure mechanism and invariant

The current code treats `performance.getEntriesByType("resource")` as the entrance to the loaded module system. Resource Timing entries can be cleared while the Rspack runtime and its module cache remain alive, so a valid authenticated renderer can incorrectly report that the native provider was not observed.

Invariant: provider discovery reads the current renderer document's live module registry directly. Its correctness must not depend on historical resource entries, bundle URLs, a previous successful turn, or cached security results.

## Feasibility evidence

On 2026-11-09, a temporary sanitized planning diagnostic against the current authenticated ChatGPT renderer observed 57 matching runtime assets, one loader, and 10,185 cached modules. The existing four-marker semantic predicate found exactly one coordinator, the historical `lXEa`/`lXE` lookup found exactly one coordinator, and both identified the same function. The temporary diagnostic exposed only counts/identity equality and was removed immediately after the observation.

This establishes semantic-only feasibility for the tested frontend build. If the predicate is no longer unique during implementation or live verification, stop and reassess rather than restoring module IDs or adding fallback chains.

## Approach

`account-transport.js` remains the owner of authenticated transport and native Sentinel requirements.

1. Enumerate renderer globals through own property descriptors so accessors are not invoked. Consider only narrowly named Webpack/Rspack chunk arrays with an own non-native `push` data property and processed chunk-entry shape; ignore native arrays, getters, similarly named decoys, and unrelated custom push functions. Apply the same no-getter rule when validating the loader, module-cache records, and candidate exports; do not trust a property merely because the outer object passed validation.
2. On first discovery in a renderer document (whether initiated by the diagnostic or provider), use one collision-resistant, inert runtime chunk per validated registry to obtain its existing loader; do not register actual module factories or invoke arbitrary exports. Accept only function loaders with a live module-cache object, and do not probe registries that fail the safety checks.
3. Cache only the validated loader references for that renderer document under the shared versioned key `Symbol.for("mezhs.chatgpt.rspackLoaders.v1")`. The value is a versioned record containing a frozen loader-reference array; every consumer validates the record and each loader's live cache before use. An invalid or tampered record must fail closed, not become a reason to execute unvalidated cached data. Do not cache a coordinator, requirements key, challenge, token, or headers.
4. On every turn, rescan the live loader caches for the existing four-marker semantic coordinator signature, deduplicate candidates by function identity across all validated loaders, and accept exactly one coordinator. Read candidate exports through data-property descriptors without executing getters or unverified functions. Bound the number of registries/modules inspected and metadata returned; choose limits that accommodate the observed 10,185-module live cache, and fail closed if bounds are exceeded. This lets later-loaded modules appear without repeating registry mutation.
5. Remove the provider cache, Resource Timing scan, bundle URL assumptions, known module-ID dependency, and dynamic runtime imports instead of retaining fallback paths.
6. Preserve fresh per-turn preparation, header allowlisting, login handling, and fail-closed behavior. Missing or ambiguous coordinators fail before preparation or conversation submission.
7. Replace the existing Resource Timing-based `inspectRspackExports` implementation with bounded live-loader inspection using the same discovery/safety mechanism and shared cache key/schema as production; avoid two independently drifting implementations of registry validation. Preserve its validated `moduleId`/`exportKeys` inputs and bounded function-metadata response where practical, without returning raw export values or full function bodies. Diagnostic-first inspection and later provider turns must reuse the same loader capture. Add an explicitly opted-in, loopback-only timing diagnostic that clears Resource Timing and reports sanitized before/after counts only; never expose URLs, export values, credentials, or token values in those timing results.
8. Update `.agents/chatgpt-private-api-debugging.md` so discovery, diagnostics, failure classification, and regression guidance match the new mechanism.

The implementation must avoid invoking unverified exports or arbitrary page arrays. If the current live build does not expose a safely identifiable chunk registry, stop and reassess rather than adding navigation or timing fallbacks.

## 2026-11-09 live failure: diagnostic-first amendment

Status: owner approved the diagnostic-first revision after two live Account execution failures. The already-committed Account implementation is not accepted or release-ready. This amendment authorizes only a diagnostic-first development stage after independent plan review; it does not authorize a change to Account transport behavior or a Stage B compatibility fix.

### Observed failure and unresolved hypothesis

- Executions exec_c3373045fa7643d1abcefaf89f6b9cb6 and exec_12e4f313a6944eab92494782acd2279d failed with: ChatGPT live Rspack chunk registry was not observed. Both failed before successful loader capture or Sentinel preparation.
- The current registry filter requires a narrowly named page global, an Array with own non-native push data function, a bounded nonempty length, and at least one processed chunk entry with the expected shape. The error proves no candidate passed all checks, not which check failed.
- A bound/native-looking runtime push is a plausible cause, not an established root cause. Tests currently use a synthetic non-native push and deliberately reject bound functions. The later process inspection showed a Debug main host, which does not establish which build was active at failure time.

### Stage A - read-only diagnostic only

1. Add an explicit opt-in ChatGPT-only operation named rspackRegistryShape to the existing loopback browser diagnostics path. Require args.inspect === true. Do not call it automatically from Account transport or from unrelated providers. Use the existing authorized-session and diagnostics routing; do not create an additional server endpoint or expose it beyond loopback.
2. Run a bounded renderer-side observation function without calling any chunk push, injecting chunks, evaluating factories, capturing loaders, importing assets, invoking exports, preparing requirements, navigating, or changing existing cache/global state. Do not inspect Resource Timing for discovery.
3. Enumerate own global names and access matching own property descriptors, not property getters. For each plausible array, inspect own data descriptors for push, length and a bounded set of stored chunk entries; inspect only shape, never contents. Function.prototype.toString may classify a function as native-looking, but must never return the source text or call that function. Guard introspection exceptions as a sanitized category. These operations must not deliberately invoke page callbacks or accessor getters; however, descriptor/introspection operations on a Proxy can run its traps, so a literal guarantee that no page code executes is not possible. Treat thrown/revoked-Proxy cases as incomplete and untrusted evidence, never as a successful match.
4. Return only a fixed-schema aggregate of bounded integer counters and booleans: scannedGlobals, matchingNames, dataArrayCandidates, accessorCandidates, ownPushFunctions, inheritedOrAbsentPush, nativeLookingPush, nonNativePush, emptyArrays, processedEntryShapeCandidates, noProcessedEntryShape, passedNameAndArray, passedNameArrayAndOwnPush, passedNameArrayOwnPushNonNative, passedAllPredicates, descriptorErrors, limitExceeded, and incomplete. Cumulative passed-stage counters must apply to the same candidate at each step, not independent category totals; keep the cumulative sequence monotonic. Aggregates classify matching candidates, not which candidate is the actual runtime. In particular, matchingNames=0 proves only that no enumerated global matched the current name predicate. Do not return registry names, module IDs, chunk IDs, function source, URLs, raw objects, request data, cookies, credentials, tokens, headers or screenshots.
5. Bound enumeration to 5000 globals, 32 matching candidates, 2048 entries per candidate and 4096 entries total. On a bound breach or unsafe/failed introspection, return the same fixed schema with incomplete=true and the applicable bounded limitExceeded or descriptorErrors indication, not a raw exception/message/stack or partially successful diagnosis. Do not treat truncated observations as conclusive evidence or bypass existing runtime safety checks. No persistent logging of confidential browser content.
6. Implement targeted unit and diagnostic API tests for empty globals, non-runtime arrays, missing or inherited push, native-looking/bound push classification, valid synthetic runtime, accessor properties, Proxy/revoked-Proxy introspection failure, bounds, cumulative counter semantics, incomplete-result handling, redaction and the opt-in ChatGPT/loopback authorization checks (including rejection for non-ChatGPT sessions). Assert that the diagnostic makes no chunk-push calls, writes, loader captures or Sentinel preparations. Keep existing Account behavior unchanged and all existing tests passing.
7. On an owner-controlled exact-branch build with an authenticated renderer, first establish that the targeted window belongs to the running branch build (record a nonsecret process/window-to-artifact identity, not merely the checkout branch, an adjacent Debug/Release binary, or a different host). Then run rspackRegistryShape once and record only its aggregate counters plus branch commit and verified build identity. Do not perform an Account turn for this observation. If runtime/build identity or diagnostic access is blocked, record the blocker; do not infer a registry shape from synthetic results.

### Stage B - explicit evidence and approval gate

- Identify the earliest failing predicate **for observed matching candidates** where the complete Stage A counters establish one. Aggregate counts alone cannot prove which candidate is the real Rspack runtime, and no matching names cannot prove a runtime is absent. If evidence is incomplete, has multiple plausible candidates, or cannot link the rejected candidate to the live runtime, report the root cause as unresolved and request further owner-approved read-only evidence rather than guessing.
- Propose the smallest safely identifiable compatibility strategy using the observed shape. Maintain descriptor-only reads, no unverified arbitrary push invocation, fresh per-turn requirements, semantic coordinator uniqueness and fail-closed submission. Do not silently relax the non-native push rule or add fallback imports, retries or cached security results.
- If the live registry cannot be safely distinguished from unrelated page arrays, preserve fail-closed Account behavior and escalate the architectural trade-off to the owner; never substitute a guessed runtime. Any changed mutation/identity guarantee needs a further explicit owner decision and amended independent plan review before implementation.
- After a separately approved Stage B implementation, repeat the original focused/full checks, independent code review, independent testing and exact-build same-document live two-turn verification. The current live failure is not a test PASS.

### Additional Stage A acceptance criteria

- [ ] Read-only diagnostic reports bounded, sanitized, monotonic cumulative counts for each observable registry-validation stage, without exposing candidate identities or claiming an unverified runtime identity.
- [ ] No Account transport behavior changes; the diagnostic performs no intentional page writes, chunk-push calls, or direct page-function/getter invocation. Potential Proxy-trap side effects are explicitly treated as an introspection limitation, not as proof of absolute inertness.
- [ ] Opt-in authorization, ChatGPT-only scope, aggregate response schema, exception/Proxy and input bounds are tested; incomplete scans cannot be treated as positive evidence, and live observations include verified renderer-to-running-build identity or are explicitly blocked.
- [ ] No Stage B compatibility change begins until live evidence, owner approval and independent review establish its safety.

## Acceptance criteria

The original feature acceptance criteria and verification below are deferred until a separately owner-approved Stage B compatibility implementation. They are **not** conditions for completing the diagnostic-only Stage A and must not be reported as PASS based only on its counters.

- [ ] A fresh renderer with no Resource Timing entries and a live Rspack registry discovers the native requirements coordinator.
- [ ] Discovery does not read Resource Timing or import CDN runtime URLs.
- [ ] Known and semantically renamed coordinator modules are supported through semantic inspection of the live module cache, without module-ID assumptions.
- [ ] Repeated turns perform at most one successful loader capture per validated registry in the same renderer document while obtaining fresh requirements each time.
- [ ] Diagnostic inspection followed by two provider turns shares one loader capture per registry while both turns perform fresh requirements preparation.
- [ ] Global accessors, module-cache/export getters, native arrays, similarly named decoys, and unrelated custom push functions are neither invoked nor mutated; invalid shared cache records fail closed.
- [ ] Missing and ambiguous coordinators fail closed before Sentinel preparation or conversation submission.
- [ ] Every turn obtains fresh requirements and only allowlisted Sentinel headers are forwarded.
- [ ] Cache traversal has explicit, tested bounds that handle the observed live cache size without accidentally accepting a partial/ambiguous scan.
- [ ] The existing validated `inspectRspackExports` request and safe response fields remain usable after switching discovery to the live registry.
- [ ] Existing authenticated ChatGPT tests and packaging checks remain green.
- [ ] A live same-session check clears Resource Timing through the bounded diagnostic, records zero matching timing entries, then demonstrates successful provider inspection and two sequential harmless Account turns without navigation or restarting Electron.

## Verification

- Focused Node tests for live-registry discovery, empty Resource Timing, renamed modules, ambiguity, missing runtime, repeated-turn loader reuse, fresh per-turn preparation, header filtering, global/module/export accessors, invalid or tampered shared cache records, scan bounds, native arrays, similarly named decoys, and non-runtime custom push functions.
- Focused Electron diagnostic tests for sanitized timing counts/clearing, existing inspection input/response compatibility, and safe live-registry export inspection. `inspectRspackExports` is updated rather than used as a Resource Timing oracle.
- A combined same-document test runs diagnostic inspection first and then two provider turns, asserting one registry capture and two fresh requirements preparations.
- `node --check` for every deployed ChatGPT browser module.
- `node --test tests/*.test.js`.
- `dotnet build Mezhs.sln -c Release` using isolated output paths if the running application locks normal build output.
- `tests/integration-resources.ps1`.
- Live verification procedure: start the exact branch build once; call the loopback timing diagnostic with its explicit clear option; verify sanitized after-counts are zero; call live-registry export inspection; then submit two successive harmless Account prompts in the same conversation through the normal MEŽS connection path without navigation or restart. Record only status and sanitized counts; do not record prompts, headers, or responses.

## Risks

- ChatGPT can change or hide its page-global Rspack registry or loader contract.
- Probing an unrelated page array could mutate page state; descriptor-only enumeration, narrow names, processed-entry shape, own non-native push validation, inert unique chunk IDs, and decoy tests are required.
- Capturing a loader mutates a real chunk registry; the loader cache must bound this to one successful capture per renderer document and repeated-turn tests must prove it does not accumulate.
- Semantic coordinator markers can drift or become ambiguous; ambiguity or an incomplete bounded scan must fail closed rather than guessing.
- A shared page-global cache is cross-consumer state, not an authority: reject malformed or stale cache records, and keep the discovery implementation consistent across transport and diagnostics.
- Synthetic tests cannot prove an undocumented upstream interface; the live check is required but still represents only the tested frontend build.

## Release proposal

PATCH: fix authenticated ChatGPT prompt submission after Resource Timing entries disappear. Do not edit `VERSION` or `CHANGELOG.md` before owner-approved release.
