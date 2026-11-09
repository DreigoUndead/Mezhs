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

## Failure mechanism and invariant

The current code treats `performance.getEntriesByType("resource")` as the entrance to the loaded module system. Resource Timing entries can be cleared while the Rspack runtime and its module cache remain alive, so a valid authenticated renderer can incorrectly report that the native provider was not observed.

Invariant: provider discovery reads the current renderer document's live module registry directly. Its correctness must not depend on historical resource entries, bundle URLs, a previous successful turn, or cached security results.

## Approach

`account-transport.js` remains the owner of authenticated transport and native Sentinel requirements.

1. Discover candidate live Rspack chunk registries from the renderer global object using strict shape checks.
2. Use a unique inert runtime chunk to obtain each registry's existing loader without fetching or importing bundle URLs.
3. Inspect the live loader cache for the existing known export and the current semantic coordinator signature.
4. Deduplicate candidates by function identity and accept exactly one coordinator.
5. Remove the provider cache, Resource Timing scan, and dynamic runtime imports instead of retaining fallback paths.
6. Preserve fresh per-turn preparation, header allowlisting, login handling, and fail-closed behavior.

The implementation must avoid invoking unverified exports or arbitrary page arrays. If the current live build does not expose a safely identifiable chunk registry, stop and reassess rather than adding navigation or timing fallbacks.

## Acceptance criteria

- [ ] A fresh renderer with no Resource Timing entries and a live Rspack registry discovers the native requirements coordinator.
- [ ] Discovery does not read Resource Timing or import CDN runtime URLs.
- [ ] Known and semantically renamed coordinator modules remain supported through the live module cache.
- [ ] Missing and ambiguous coordinators fail closed before Sentinel preparation or conversation submission.
- [ ] Every turn obtains fresh requirements and only allowlisted Sentinel headers are forwarded.
- [ ] Existing authenticated ChatGPT tests and packaging checks remain green.
- [ ] A live same-session check demonstrates discovery after Resource Timing loss without restarting Electron.

## Verification

- Focused Node tests for live-registry discovery, empty Resource Timing, renamed modules, ambiguity, missing runtime, fresh per-turn preparation, and header filtering.
- `node --check` for every deployed ChatGPT browser module.
- `node --test tests/*.test.js`.
- `dotnet build Mezhs.sln -c Release` using isolated output paths if the running application locks normal build output.
- `tests/integration-resources.ps1`.
- Live diagnostic/provider verification through the normal loopback MEŽS connection path, recording only status and sanitized evidence.

## Risks

- ChatGPT can change or hide its page-global Rspack registry or loader contract.
- Probing an unrelated page array could mutate page state; strict registry naming/shape validation and inert unique chunk IDs are required.
- Semantic coordinator markers can drift or become ambiguous; ambiguity must fail closed rather than guessing.
- Synthetic tests cannot prove an undocumented upstream interface; the live check is required but still represents only the tested frontend build.

## Release proposal

PATCH: fix authenticated ChatGPT prompt submission after Resource Timing entries disappear. Do not edit `VERSION` or `CHANGELOG.md` before owner-approved release.
