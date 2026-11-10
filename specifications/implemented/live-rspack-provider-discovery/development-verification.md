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

## Windows developer continuation - 2026-11-09

Branch: live-rspack-provider-discovery. Starting commit: f96c36e253365d9d09f24649c07d52e861505bae. High-risk Developer stage.

### Local checks
- PASS: node --check on all nine deployed ChatGPT browser modules.
- PASS: focused Rspack and Electron diagnostics Node tests (19 passed, 0 failed).
- PASS: node --test tests/*.test.js (116 passed, 0 failed, 0 skipped).
- PASS: dotnet build Mezhs.sln -c Release --nologo -v quiet (0 errors, 1 unrelated WhatsApp CS0114 warning).
- PASS: tests/integration-resources.ps1 (all nine ChatGPT embedded modules byte-for-byte, Grok and Gemini checked).
- PASS: node .agents/codegraph/export.cjs (246 files, 640 edges; large generated graph refresh).

### Mandatory live verification - BLOCKED, not PASS
- Running Agent API on 127.0.0.1:5060 uses Debug executable and owns the authenticated ChatGPT Electron process. Exact-branch Release executable built but was not started.
- Isolated Release host config used unused loopback port 5097 and separate storage. Copying the Chromium profile failed on locked Network/Cookies and Cookies-journal (Windows error 32; Robocopy exit 9). Read-only SQLite access failed as well.
- Partial isolated profile and config were deleted. Original authenticated profile preserved. No session credentials were committed.
- No live timing clear; zero matching Resource Timing entries not observed. No live Rspack export inspection or Account turns occurred. Two fresh preparations and same-document loader reuse remain unverified.

### Mandatory complexity escalation - owner decision pending
Concurrent profile use or private session extraction adds disproportionate complexity and could disrupt the running agent. Do not add profile-lock workarounds or relax browser security guards.
Recommended: owner-approved controlled switch to the exact task-branch Release Agent host after this execution, followed by explicit timing clear, safe registry inspection and two harmless Account turns in the same renderer document. Alternative: authenticate a separate isolated Release host.
Developer stage INCOMPLETE. Not ready for independent code review. CI and synthetic tests cannot replace live verification.
