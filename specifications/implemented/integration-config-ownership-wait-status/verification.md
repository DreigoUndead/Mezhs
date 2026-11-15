# Independent verification — integration-config-ownership-wait-status

Date: 2026-11-15
Task/level: Integration configuration ownership and Waiting status / High-risk
Candidate branch: integration-config-ownership-wait-status
**Exact independently tested implementation SHA:** bc55e64fb6b94212712be193bec12f467b2b687f
**Base main SHA:** 70be3d41a26cbf106e0db8ac0e86c29846ec1a0c
Independent code review: PASS, execution exec_d8b0234ac5d94c8d, same candidate SHA.
Independent test execution: exec_9433dbd983444fbaaf3f2f6e2ddb804b.

## Verdict

**PASS for the independently executed mocked/runtime and UI acceptance coverage**, with explicit limitations below. No confirmed implementation failure was observed. Sixteen checks passed as independent commands; the original resilience script was BLOCKED by its Windows PowerShell 5.1-incompatible parameter, then its assertions PASSed through a temporary compatibility wrapper. The negative-config startup harness observed five expected rejection messages, but one additional host-startup process-timeout path remains BLOCKED; its missing-Core-file loader behavior was independently tested and PASSed.

All checks used an isolated detached test worktree at %TEMP%/mezhs-integration-wait-test-9433dbd pinned to bc55e64. The owner's working checkout, live Electron sessions, browser authentication profiles and unrelated processes were not modified. Tests used mocked browser integrations, temporary data/config directories, dedicated ports, and a separate headless Chromium profile.

## Commands and independent observations

All rows use the exact candidate commit shown above. Exit codes are direct host command exit codes. PASS output is not a substitute for inspecting what each test covers.

| MEŽS execution | Command | Exit | Outcome / observed assertions |
| --- | --- | --- | --- |
| 7312 | dotnet build src/Mezhs.Agent.Api/Mezhs.Agent.Api.csproj -c Release --nologo -v minimal | 0 | PASS; 0 warnings, 0 errors |
| 7313 | powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/browser-admission-wait.ps1 | 0 | PASS; capacity 1/2/0, 5000 ms start-spacing measurement, genuine capacity/spacing wait signals, cancellation, release, provider activity isolation |
| 7315 | powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/agent-admission-projection.ps1 | 0 | PASS; real isolated Agent host and test-only BrowserIntegrationBase provider, shared ordinary/Agent capacity, exact Agent message association, Waiting→Running→Completed/Failed, cancellation, terminal cleanup, provider waiting separate from admission, independent YAML roots |
| 7318 | powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/browser-limits-config.ps1 | 0 | PASS; defaults, unlimited 0, bounded values, invalid values and provider limits |
| 7319 | powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/agent-architecture.ps1 | 0 | PASS; independent Core/Agent options and DI composition; architecture invariants |
| 7324 | dotnet build src/Mezhs.Agent.Web/Mezhs.Agent.Web.csproj -c Release --nologo -v minimal | 0 | PASS; TypeScript and Vite production build; npm audit advisories noted below |
| 7327 | powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/agent-api-smoke.ps1 | 0 | PASS; Core and Agent API routes/composition, separate listeners, loopback-only restrictions, model defaults/provider Default/inheritance/connection override, shared groups, deletion and historical Executor evidence |
| 7328 | node --test tests/agent-status-unread-browser.test.cjs | 0 | PASS; 1 Chromium test, 0 failures; orange Waiting, Running recovery, selected/header consistency, hover/focus labels, unread completion and reduced-motion |
| 7329 | powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/policy-decoder.ps1 | 0 | PASS; policy rules, durable queue, command evidence, timeout and Unicode |
| 7336 | powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/agent-protocol-result.ps1 | 0 | PASS; command syntax/rejection side effects, ordered shells, failed-later-shell evidence, protocol stripping |
| 7340 | powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/agent-provider-lifetime.ps1 | 0 | PASS; Agent/provider lifecycle and cancellation propagation |
| 7341 | powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/integration-cancellation-contract.ps1 | 0 | PASS; seven integration registrations and cancellation/capacity behavior |
| 7343 | tests/agent-runtime-resilience.ps1 invoked through a temporary PowerShell 5.1 Invoke-WebRequest compatibility function | 0 | PASS; six concurrent roots and six Executor shells, failure/cancellation isolation, queued cancellation, invalid-batch atomicity, restart/recovery, same-chat wake and deletion/shell-kill non-resurrection |
| 7346 | powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/agent-attachment-endtoend.ps1 | 0 | PASS; staged TXT/PNG uploads, metadata, SHA-256 content roundtrips and durable staging ledger |
| 7347 | python tests/agent-graceful-shutdown.py | 0 | PASS; graceful Agent shutdown with active shell; restart recovers running/queued work without duplicate shell |
| 7359 | isolated temporary dotnet test app referencing Mezhs.Api, calling MezhsConfigLoader.Load on nonexistent YAML | 0 | PASS; FileNotFoundException for missing Core config |

### Negative-case and harness evidence

- Exec 7335: the unmodified tests/agent-runtime-resilience.ps1 exited 1 before completing because Windows PowerShell 5.1 has no Invoke-WebRequest -SkipHttpErrorCheck. This is an observed test-harness/tooling incompatibility, not evidence of production failure. Exec 7343 ran the unchanged assertions with a temporary in-process compatibility function, handling expected non-2xx status codes; all full-suite assertions PASSed. The original script itself is not directly runnable on this host without PowerShell 7 or a portability edit.
- Exec 7353: negative host-startup harness observed the correct rejection messages for Core-owned Agent YAML top-level sections (connections, transport, storage, extensions) and unknown policy connection IDs. The script printed blank process exit-code fields even for those five cases, so this evidence confirms rejection messages **only**, not clean process termination. The missing-Core-config host test was BLOCKED: no exit within 12 s. A later process check found no lingering test-owned dotnet process. Direct Core loader rejection was verified separately by exec 7359. The known Windows/.NET unhandled-startup-exception process behavior is a possible explanation, **not an established cause** of this timeout.
- Exec 7324 printed npm audit advisories: three transitive dependency issues (one moderate, two high) and a Vite chunk-size warning. Build itself succeeded; no causality to this task is established.
- Exec 7351 removed only the isolated smoke-test artifact tests/data-agent-test (one orphaned SQLite shm file). Exec 7355 confirmed no matching test .NET process, and ports 5198, 5199, 5200, 5201, 5397 were free. None of the user's live processes/profiles was targeted for termination.

## Coverage limits and scope

- Live authenticated Electron account sessions, guest provider network flows, live ChatGPT/Grok/Gemini operation and actual remote-provider failure timing were **not** exercised, to protect existing sessions/profiles. Controlled mock integrations and Chromium UI fixture validate the specified admission/status behavior, not all real-provider conditions.
- No cross-process admission coordination was tested or required: the approved architecture explicitly limits shared permits to one owning integration instance within one Agent API host.
- No distributed scheduling, new persistent Waiting state, or provider activity-driven Agent status was added. Root status remained Running while effective status was Waiting in the covered real-host probe.
- The full solution build and all unrelated front-end suites were not independently rerun here; scoped Agent API/Web builds and targeted integrations/regressions were run.
- Existing CI outcome was not independently checked. Developer and reviewer checks are distinct evidence from the independent results above.

## Handoff and approval boundary

Code review PASS and independent targeted acceptance testing PASS are recorded for **implementation SHA bc55e64fb6b94212712be193bec12f467b2b687f**. This verification file is an **evidence-only follow-up commit**; it is not a re-tested or code-reviewed implementation change. Before release, recheck the current task branch head, main/base, PR status and whether only this verification documentation was added. Material implementation changes require new review/testing and owner approval.

**Owner action:** review the delivered configuration split and effective admission-only Waiting behavior, the above evidence and limitations, then explicitly decide whether the task may merge to main. No merge, version, changelog or release action was authorized/performed by the tester.
