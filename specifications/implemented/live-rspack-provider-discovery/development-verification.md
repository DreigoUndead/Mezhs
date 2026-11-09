# Live Rspack provider discovery — developer verification

Level: **High-risk** (private runtime introspection and Sentinel security boundary).
Branch: live-rspack-provider-discovery; PR #49 (draft).
Approved plan: 2ae6007be9b462b1f4a02c5ad41ed5c9f84ff296.

## Checks performed in connected GitHub-only environment

- **PASS — syntax parsing (V8)**: six focused JS files parsed with new Function. This is not equivalent to the requested node --check run.
- **PASS — direct synthetic execution**: shared discovery and native preparation with a synthetic chunk registry. Two executions in the same renderer state captured the loader once and prepared fresh keys (key-1 and key-2), with two callback requests.
- **PASS — direct synthetic export inspection**: requested a function and secret-valued nonfunction export; only function metadata and nonfunction type were returned.
- **PASS — direct synthetic timing diagnostic**: cleared an in-memory Resource Timing fixture and observed before/after counts (2 to 0), with no URLs or values exposed.
- **PASS — GitHub diff inspection**: scoped to Account discovery, diagnostics, tests, packaging, guide, and plan movement; no version/changelog/main edits.
- **NOT RUN — node --check for every deployed module**: no local checked-out repository.
- **NOT RUN — node --test tests/*.test.js**: no local checkout; GitHub Actions has not started for draft PR #49 as of this entry.
- **NOT RUN — dotnet build Mezhs.sln -c Release**: local .NET SDK and working repository are unavailable.
- **NOT RUN — tests/integration-resources.ps1**: no Windows PowerShell or built assemblies.
- **NOT RUN — node .agents/codegraph/export.cjs**: no local working repository/Codegraph.
- **NOT RUN — live same-session Account check**: no access to Windows/Electron authenticated renderer.

## Remaining verification and risk

The independent code reviewer must inspect the implementation without relying on the synthetic checks. The independent tester must run all required checks and the live same-session sequence using the branch build. The current ChatGPT frontend's descriptor-safe, non-native chunk-push shape and coordinator export data descriptors have **not** been verified in this environment; these are assumptions in the approved plan. A mismatch must fail closed and trigger the mandatory owner escalation before relaxing the guards or adding fallbacks.

This document records developer evidence, not an independent testing PASS.
