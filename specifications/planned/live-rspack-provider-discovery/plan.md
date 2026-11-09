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

## Approach

`account-transport.js` remains the owner of authenticated transport and native Sentinel requirements.

1. Enumerate renderer globals through own property descriptors so accessors are not invoked. Consider only narrowly named Webpack/Rspack chunk arrays with an own non-native `push` data property and processed chunk-entry shape; ignore native arrays, getters, similarly named decoys, and unrelated custom push functions.
2. On the first provider request in a renderer document, use one unique inert runtime chunk per validated registry to obtain its existing loader. Accept only function loaders with a live module-cache object.
3. Cache only the validated loader references for that renderer document so probing has a bounded, one-time side effect. Do not cache a coordinator, requirements key, challenge, token, or headers.
4. On every turn, rescan the live loader caches for the semantic coordinator signature, deduplicate candidates by function identity, and accept exactly one coordinator. This lets later-loaded modules appear without repeating registry mutation.
5. Remove the provider cache, Resource Timing scan, bundle URL assumptions, known module-ID dependency, and dynamic runtime imports instead of retaining fallback paths.
6. Preserve fresh per-turn preparation, header allowlisting, login handling, and fail-closed behavior. Missing or ambiguous coordinators fail before preparation or conversation submission.
7. Replace the existing Resource Timing-based `inspectRspackExports` implementation with bounded live-loader inspection using the same registry safety contract. Add a loopback-only diagnostic option that clears Resource Timing and reports sanitized before/after counts for deterministic same-document verification; it must not expose URLs, exports, credentials, or token values.
8. Update `.agents/chatgpt-private-api-debugging.md` so discovery, diagnostics, failure classification, and regression guidance match the new mechanism.

The implementation must avoid invoking unverified exports or arbitrary page arrays. If the current live build does not expose a safely identifiable chunk registry, stop and reassess rather than adding navigation or timing fallbacks.

## Acceptance criteria

- [ ] A fresh renderer with no Resource Timing entries and a live Rspack registry discovers the native requirements coordinator.
- [ ] Discovery does not read Resource Timing or import CDN runtime URLs.
- [ ] Known and semantically renamed coordinator modules are supported through semantic inspection of the live module cache, without module-ID assumptions.
- [ ] Repeated turns perform only one bounded loader capture per renderer document while obtaining fresh requirements each time.
- [ ] Global accessors, native arrays, similarly named decoys, and unrelated custom push functions are neither invoked nor mutated.
- [ ] Missing and ambiguous coordinators fail closed before Sentinel preparation or conversation submission.
- [ ] Every turn obtains fresh requirements and only allowlisted Sentinel headers are forwarded.
- [ ] Existing authenticated ChatGPT tests and packaging checks remain green.
- [ ] A live same-session check clears Resource Timing through the bounded diagnostic, records zero matching timing entries, then demonstrates successful provider inspection and prompt submission without navigation or restarting Electron.

## Verification

- Focused Node tests for live-registry discovery, empty Resource Timing, renamed modules, ambiguity, missing runtime, repeated-turn loader reuse, fresh per-turn preparation, header filtering, accessors, native arrays, similarly named decoys, and non-runtime custom push functions.
- Focused Electron diagnostic tests for sanitized timing counts/clearing and live-registry export inspection. `inspectRspackExports` is updated rather than used as a Resource Timing oracle.
- `node --check` for every deployed ChatGPT browser module.
- `node --test tests/*.test.js`.
- `dotnet build Mezhs.sln -c Release` using isolated output paths if the running application locks normal build output.
- `tests/integration-resources.ps1`.
- Live verification procedure: start the exact branch build once; call the loopback timing diagnostic with its explicit clear option; verify sanitized after-counts are zero; call live-registry export inspection; then submit one harmless prompt through the normal MEŽS connection path without navigation or restart. Record only status and sanitized counts.

## Risks

- ChatGPT can change or hide its page-global Rspack registry or loader contract.
- Probing an unrelated page array could mutate page state; descriptor-only enumeration, narrow names, processed-entry shape, own non-native push validation, inert unique chunk IDs, and decoy tests are required.
- Capturing a loader mutates a real chunk registry; the loader cache must bound this to one successful capture per renderer document and repeated-turn tests must prove it does not accumulate.
- Semantic coordinator markers can drift or become ambiguous; ambiguity must fail closed rather than guessing.
- Synthetic tests cannot prove an undocumented upstream interface; the live check is required but still represents only the tested frontend build.

## Release proposal

PATCH: fix authenticated ChatGPT prompt submission after Resource Timing entries disappear. Do not edit `VERSION` or `CHANGELOG.md` before owner-approved release.
