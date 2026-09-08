# Agent API Foundation TODO

This file records the current remaining work and final disposition of issues found during review of the `agent-api-foundation` branch.

## Active TODO

Two active follow-up tasks remain from this review:

- [ ] **#14 — Process/service isolation:** design and implement one dedicated MEŽS supervisor/launcher that owns starting, stopping, restarting, and detached service lifetime. Do not solve this with `start /b`, ad-hoc PowerShell detachment, or similar shell tricks.
- [ ] **Common SQLite/LogSql storage foundation:** extract/reuse the generic SQLite ownership from `Mezhs.Log.Sql` for main MEŽS API and Agent persistence. Keep generic database path/connection/transaction/migration mechanics separate from `Mezhs.Log.Shared` log-root and notes-file semantics. Migrate the existing main/API/Agent SQLite writers only after that common ownership boundary is clean.

Not active implementation work:

- **#10 / #18:** explicit host-shell capability/security design decisions. If stronger isolation is required later, use narrow structured tools and/or a real OS/container/account boundary rather than a fake shell sandbox.
- **#28:** intentionally deferred until a concrete metrics consumer exists.

## Final validation

Fresh validation on `windows-latest` passed on **2026-09-08** after the final fixes:

- `dotnet build Mezhs.sln -c Release` — **PASS**
- `tests/agent-architecture.ps1` — **PASS**
- `tests/agent-web-shared-ui.ps1` — **PASS**
- `tests/policy-decoder.ps1` — **PASS**
- `tests/agent-api-smoke.ps1` — **PASS**
- `tests/agent-runtime-resilience.ps1` — **PASS**

The temporary validation workflow was removed after the successful run.

## Review disposition

Status meanings:

- **RESOLVED** — implemented and covered by the current foundation.
- **BY DESIGN** — reviewed and intentionally retained because it matches the architecture.
- **REJECTED** — the original finding described a problem that the final architecture does not actually have, or a proposed mechanism was redundant.
- **DEFERRED** — intentionally not implemented until there is a real consumer or requirement.
- **OPEN** — still requires a separate architectural task.

| # | Finding | Status | Final disposition |
|---|---|---|---|
| 1 | Prompt textbox jumps while typing on line 2 or later | **RESOLVED** | Shared `useAutoResizeTextArea` uses pre-paint layout resizing. |
| 2 | Chat UI does not render Markdown/code formatting correctly | **RESOLVED** | Shared `MarkdownContent` owns supported Markdown/code rendering and safe links. |
| 3 | Automatic continuation prompt is ambiguous and should come from policy/runtime configuration | **RESOLVED** | Runtime prose is configurable; `<DONE>` guidance is generated from the selected policy, so `requireDone: false` cannot receive contradictory completion instructions. |
| 4 | Some shell commands are not formatted correctly in the UI | **RESOLVED** | Canonical Agent `Parser` owns protocol parsing. Agent API exposes semantic command/display metadata; Agent Web no longer implements a second Agent command grammar. |
| 5 | After restarting a chat, previously executed shell commands show `NOT EXECUTED` | **RESOLVED** | Durable `TriggerMessageId` + `CommandIndex` + command identity link protocol commands to the execution that actually ran, including duplicate command text. |
| 6 | Shell execution can remain stuck for several minutes in the native chat UI | **RESOLVED** | Timeout/cancellation owns bounded process-tree termination; unconfirmed termination is explicit failure rather than silently reported success, and partial timeout output remains durable evidence. |
| 7 | Execution queue is unbounded | **RESOLVED** | SQLite is the bounded durable queue/admission owner; workers keep only a small wake signal and atomically claim eligible queued work. |
| 8 | Agent exposure must have one explicit trust boundary | **RESOLVED for current local-only architecture** | Agent API/Web enforce loopback-only exposure; no decorative local bearer layer. External-event input is still not implicitly trusted. |
| 9 | CORS currently allows any origin, header, and method | **RESOLVED** | Agent API does not enable permissive CORS; browser access goes through loopback Agent Web. |
| 10 | Shell capability boundary is too broad | **BY DESIGN** | `SH` is intentionally an opaque host-shell capability. Policy grants/denies the capability; it does not pretend to sandbox arbitrary shell syntax. |
| 11 | No rate limiting or execution admission control | **RESOLVED for execution capacity** | Durable admission is bounded by worker + queue capacity; overflow returns HTTP 429 without creating a rejected execution row. |
| 12 | Agent-specific integration tests are missing | **RESOLVED for foundation scope** | Architecture, UI, policy/shell, API smoke, and runtime-resilience suites now cover the foundation. |
| 13 | Debug-log endpoint can expose sensitive operational data | **RESOLVED for current boundary** | Debug log remains inside the loopback-only Agent boundary. If Agent becomes remote/multi-user, debug-log authorization belongs to that external boundary. |
| 14 | MEŽS services and the controlling shell need process isolation | **OPEN** | Separate architectural task: introduce one proper supervisor/launcher lifecycle owner. |
| 15 | Worker task tracking is more complex than necessary | **RESOLVED** | Fixed worker consumers replaced dynamic task tracking; durable SQLite claim-time serialization replaced transient per-chat gates. |
| 16 | AgentStore serializes all writes through one global application lock | **RESOLVED** | SQLite owns concurrency through WAL, busy timeout, transactions, and independent connections; no application-wide write lock/shared-cache mode. |
| 17 | Shell execution has no explicit working-directory/workspace isolation | **RESOLVED** | Every shell command starts from the validated configured Agent workspace. This is deterministic CWD ownership, not filesystem sandboxing. |
| 18 | Policy cannot enforce runtime restrictions after a process starts | **BY DESIGN / DEFERRED** | No fake post-start restrictions. Stronger isolation requires structured capabilities or a real OS/container/account boundary. |
| 19 | Policy evidence is rebuilt from mutable live execution history | **RESOLVED** | Policy evaluation consumes immutable `ExecutionEvidence` snapshots. |
| 20 | Policy actions are opaque `Kind + Request string` values | **RESOLVED** | `PolicyAction` carries resolved command identity plus body, so policy evaluates registered capabilities directly. |
| 21 | Completion is model-claimed before system verification | **RESOLVED** | `<DONE>` is only a claim; persisted successful-command evidence is checked before completion is accepted when policy requires it. |
| 22 | Execution records lack requester identity | **REJECTED** | Unauthenticated `Requester` machinery was removed; durable causal provenance remains `Source`, `SourceReference`, execution/chat/message identity, parent and correlation data. |
| 23 | Caller-provided environment variables can influence shell execution | **RESOLVED** | Caller environment mutation is policy-allowlisted; runtime-owned `MEZHS_*` names are reserved. |
| 24 | Cancellation is cooperative and does not model acknowledgement/stopping states | **RESOLVED** | Root execution is the single cancellation owner; durable `CancelRequested` acknowledgement is reconciled after token registration to close the claim/register race. Direct shell-child cancellation is rejected. |
| 25 | Configuration supports only version 1 with no migration path | **RESOLVED for configuration decoding** | Version dispatch happens before version-specific decoding, leaving an explicit future decoder/migration seam. |
| 26 | Storage path resolution can escape an intended storage root | **REJECTED as an Agent invariant** | Agent intentionally accepts explicit workspace/storage paths; there is no Agent storage-root contract to enforce. Log-root semantics remain specific to `Mezhs.Log.Shared`. |
| 27 | API persistence records are exposed directly instead of dedicated API DTOs | **RESOLVED** | `AgentApiMapper` maps persistence records to dedicated public Agent API views; persistence-only details remain internal. |
| 28 | Operational metrics are missing | **DEFERRED** | Speculative metrics API/counters were removed. Add concrete durable aggregates when a real consumer requires them. |
| 29 | Repository/file modification workflow needs safer verification | **RESOLVED as repository workflow** | `.agents` requires one review branch, strongest practical validation, adversarial/final diff review, temporary CI only as fallback, cleanup afterward, and explicit approval before merge. |
| 30 | Temporary Windows command files may remain if cleanup fails | **RESOLVED** | Temporary command files were removed from the design; shell bodies stream through stdin on Windows and Unix-like systems. |

## Key architecture conclusions from the review

- Agent protocol parsing has one owner: the canonical Agent parser/API semantic representation, not separate server and Web grammars.
- Completion semantics are policy-owned; runtime message templates do not independently redefine `<DONE>` requirements.
- Root Agent execution owns cancellation; shell-child cancellation is internal to that lifecycle.
- Shell terminal state is not claimed cleanly when process termination cannot be confirmed.
- SQLite owns durable queue/admission/state concurrency instead of parallel in-memory sources of truth.
- Do not fold the supervisor task or common SQLite/LogSql extraction into this foundation change; they are separate reviewable architecture tasks.

All numbered review findings other than **#14** are now resolved, rejected, intentionally retained by design, or explicitly deferred.