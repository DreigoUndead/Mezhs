# Agent API Foundation TODO

This file records issues found during review of the `agent-api-foundation` branch and the final disposition of each item.

Status meanings:

- **RESOLVED** — implemented and covered by the current foundation.
- **BY DESIGN** — reviewed and intentionally retained because it matches the architecture.
- **REJECTED** — the original finding described a problem that the final architecture does not actually have.
- **DEFERRED** — intentionally not implemented until there is a real consumer or requirement.
- **OPEN** — still requires a separate architectural task.

## User-reported UI and runtime issues

### 1. Prompt textbox jumps while typing on line 2 or later

**Status: RESOLVED**

- **What changed:** Shared composer resizing now happens before browser paint, so multiline typing no longer causes the UI above the textarea to visibly jump.
- **How:** `Mezhs.Web.Lib` owns textarea autosizing through `useAutoResizeTextArea`; the resize effect was changed from `useEffect` to `useLayoutEffect` so height is corrected before the frame is painted.
- **Validation:** `tests/agent-web-shared-ui.ps1` and `tests/agent-architecture.ps1` assert the shared implementation and pre-paint resize ownership.

### 2. Chat UI does not render Markdown/code formatting correctly

**Status: RESOLVED**

- **What changed:** Agent chat messages now render headings, lists, inline code, fenced code blocks, links, and other supported Markdown instead of exposing raw Markdown markers.
- **How:** Markdown rendering was moved into the shared `Mezhs.Web.Lib` chat surface through `MarkdownContent`, including safe-link handling, so Agent Web and normal MEŽS chat use the same presentation owner.
- **Validation:** `tests/agent-web-shared-ui.ps1` and `tests/agent-architecture.ps1` verify shared Markdown ownership and safe-link handling.

### 3. Automatic continuation prompt is ambiguous and should come from policy/runtime configuration

**Status: RESOLVED**

- **What changed:** Runtime continuation/result text is configurable, while completion-marker guidance is generated from the selected policy instead of duplicated in global prose.
- **How:** `AgentPromptBuilder` appends `<DONE>` guidance only when that policy has `requireDone: true`; policies with `requireDone: false` receive no contradictory marker instruction. Policy model instructions remain the owner of successful-evidence requirements.
- **Validation:** `tests/policy-decoder.ps1` verifies continuation differs correctly for `requireDone: true` versus `false`.

### 4. Some shell commands are not formatted correctly in the UI

**Status: RESOLVED**

- **What changed:** Shell requests/results use consistent command/evidence cards and the Web UI no longer implements its own Agent command grammar.
- **How:** The canonical Agent `Parser` returns semantic commands plus visible non-protocol content. Agent API maps that into message DTOs (`commands`, `commandIndex`, `completionClaimed`, `displayContent`), and Agent Web only renders those semantics plus persisted execution evidence.
- **Validation:** `tests/policy-decoder.ps1` parses a real `<SH>...</SH>` + `<DONE>` reply and verifies display stripping/body/index; `tests/agent-web-shared-ui.ps1` rejects a Web-side protocol parser; API smoke verifies the live message shape.

### 5. After restarting a chat, previously executed shell commands show `NOT EXECUTED`

**Status: RESOLVED**

- **What changed:** Historical protocol commands remain attached to the execution that actually ran, including duplicate commands with identical text.
- **How:** Child executions persist `CommandName`, `TriggerMessageId`, and `CommandIndex`. Agent Web maps an assistant protocol command to execution evidence by semantic identity instead of matching raw command text.
- **Validation:** `tests/policy-decoder.ps1` creates duplicate identical shell commands and verifies they remain durably distinguishable by originating message and command index.

### 6. Shell execution can remain stuck for several minutes in the native chat UI

**Status: RESOLVED**

- **What changed:** Shell timeout/cancellation owns bounded process-tree termination without silently claiming success when termination cannot be confirmed. Partial output is persisted with failed timeout evidence.
- **How:** `Shell` kills the process tree and waits for bounded shutdown. If termination cannot be confirmed, it records explicit failed shell evidence and raises `ShellTerminationException`, which fails the root execution instead of reporting a normal timeout/cancellation completion.
- **Validation:** `tests/policy-decoder.ps1` exercises a real timed-out shell and verifies pre-timeout output remains durable; architecture checks require explicit termination-uncertainty handling.

## Review findings

### High priority

### 7. Execution queue is unbounded

**Status: RESOLVED**

- **What changed:** SQLite is now the authoritative bounded queue for root Agent executions. In-memory state no longer owns queued execution IDs.
- **How:**
  - `AgentStore.TryCreateRootExecution` performs admission and insertion atomically against the configured outstanding-work capacity.
  - Excess work is rejected before an execution row is created.
  - `AgentWorker` keeps only a small bounded wake signal; consumers call `AgentStore.TryClaimNextQueuedExecution` to atomically claim durable work.
  - Same-chat eligibility is enforced in the SQLite claim query before an execution becomes `Running`.
  - Queued rows survive service restart; only work that had actually started is recovered as interrupted.
- **Validation:** `tests/policy-decoder.ps1` verifies durable same-chat queue serialization; `tests/agent-runtime-resilience.ps1` verifies bounded admission and queued-work restart survival.

### 8. Agent exposure must have one explicit trust boundary

**Status: RESOLVED for the current local-only architecture**

- **What changed:** The trust boundary is explicitly the local machine/API exposure boundary rather than a decorative bearer token between local cooperating processes.
- **How:**
  - Agent API validates that its configured listener is loopback-only.
  - Agent Web validates both its listener and Agent API base URL as loopback-only.
  - Agent API does not enable permissive CORS.
  - The redundant Agent Web -> Agent API bearer layer was removed.
- **Important boundary note:** Loopback protects network exposure; it does **not** make future WhatsApp/Jira/reminder content trusted. Policies fed by external events still need appropriately narrow capabilities.
- **Validation:** `tests/agent-api-smoke.ps1` verifies both services fail closed on non-loopback configuration and that no bearer ceremony is required locally.

### 9. CORS currently allows any origin, header, and method

**Status: RESOLVED**

- **What changed:** Agent API no longer enables permissive CORS.
- **How:** The CORS registration/middleware was removed; browser access is expected through the loopback Agent Web proxy.
- **Validation:** `tests/agent-api-smoke.ps1` sends an `Origin` header and verifies Agent API does not emit `Access-Control-Allow-Origin`.

### 10. Shell capability boundary is too broad

**Status: BY DESIGN, with an explicit security constraint**

- **What changed:** No fake command-level sandbox was introduced.
- **How:** `SH` is treated as an intentional host-shell capability. Policy decides whether that capability exists; once allowed, the shell text remains opaque and runs with the privileges of the account hosting MEŽS Agent.
- **Reasoning:** Parsing/filtering arbitrary shell syntax would create an unreliable pseudo-sandbox. If a future external source must not have host authority, expose narrow structured tools or use a real OS/container/account isolation boundary instead.
- **Related:** See #18.

### 11. No rate limiting or execution admission control

**Status: RESOLVED for execution capacity**

- **What changed:** Agent execution admission is bounded by durable outstanding-work capacity.
- **How:** The maximum outstanding root executions is derived from configured active-worker capacity plus queue capacity. `TryCreateRootExecution` performs the capacity check and insert in the same SQLite transaction. The HTTP endpoint returns `429` when capacity is full and no rejected execution row is created.
- **Validation:** `tests/agent-runtime-resilience.ps1` fills active + queued capacity, verifies the next request receives `429`, and verifies no extra execution row was persisted.

### 12. Agent-specific integration tests are missing

**Status: RESOLVED for the foundation scope**

- **What changed:** The Agent foundation now has architecture, policy/shell, API, UI, and runtime-resilience coverage.
- **How / coverage:**
  - `tests/agent-architecture.ps1` — ownership/architecture invariants.
  - `tests/agent-web-shared-ui.ps1` — shared composer/Markdown/command evidence behavior.
  - `tests/policy-decoder.ps1` — policy compilation/evaluation, completion evidence, durable command identity, SQLite queue serialization, real shell fidelity/timeout/Unicode.
  - `tests/agent-api-smoke.ps1` — loopback boundary, CORS, API DTOs, environment policy, Web proxy.
  - `tests/agent-runtime-resilience.ps1` — cancellation acknowledgement, bounded durable admission, forced restart recovery, queued-work survival.
- **Validation:** Full suite passed on `windows-latest` with `dotnet build Mezhs.sln -c Release` at 0 warnings / 0 errors.

### 13. Debug-log endpoint can expose sensitive operational data

**Status: RESOLVED for the current boundary; conditional future requirement remains**

- **What changed:** Debug logs remain available, but only inside the same loopback-only Agent boundary.
- **How:** The endpoint is exposed by Agent API and proxied by loopback-only Agent Web. There is no remote Agent surface in the current architecture.
- **Future condition:** If Agent access becomes remote or multi-user, the debug-log endpoint must be covered by that external authentication/authorization model rather than inventing a separate local token scheme.
- **Validation:** `tests/agent-api-smoke.ps1` verifies the log is downloadable locally and proxied by Agent Web.

### Medium priority

### 14. MEŽS services and the controlling shell need process isolation

**Status: OPEN**

- **What remains:** Service lifecycle is still coupled enough to the process/session that launched it that a dedicated process owner is needed.
- **Required architecture:** Introduce one supervisor/launcher responsible for starting, stopping, restarting, and owning detached service processes/sessions.
- **Explicit non-solution:** Do not solve this with `start /b`, ad-hoc PowerShell detachment, or similar shell tricks. This needs one proper lifecycle owner.
- **Scope:** Separate task from the Agent foundation changes in this list.

### 15. Worker task tracking is more complex than necessary

**Status: RESOLVED**

- **What changed:** Dynamic active-task tracking was removed.
- **How:** `AgentWorker` starts a fixed number of owned consumer tasks based on `MaxConcurrentExecutions`. There is no `HashSet<Task>` and no late cleanup/observation loop for dynamically-created worker tasks.
- **Additional simplification:** Per-chat in-memory semaphores were also removed when durable claim-time serialization was introduced in #7.
- **Validation:** `tests/agent-architecture.ps1` asserts fixed consumers and absence of transient task/chat-gate ownership.

### 16. AgentStore serializes all writes through one global application lock

**Status: RESOLVED**

- **What changed:** The application-wide write lock was removed.
- **How:** SQLite owns concurrency using WAL, `busy_timeout`, transactions, and independent connections. SQLite shared-cache mode is not used.
- **Validation:** `tests/agent-architecture.ps1` asserts no `_writeLock` and no `SqliteCacheMode.Shared`; queue/resilience tests exercise concurrent state transitions.
- **Future storage note:** The separate `log-foundation` branch contains reusable `LogSql` SQLite infrastructure. Reusing that engine for main/API/Agent persistence is a separate task; the current Agent work intentionally does not mix that branch into this foundation change.

### 17. Shell execution has no explicit working-directory/workspace isolation

**Status: RESOLVED**

- **What changed:** Every shell command starts from one explicit configured Agent workspace.
- **How:** `ProcessStartInfo.WorkingDirectory` is set from `AgentOptions.Workspace`; configuration resolves/validates that workspace before execution.
- **Validation:** `tests/agent-architecture.ps1` checks workspace ownership and `tests/policy-decoder.ps1` verifies configured workspace resolution.
- **Clarification:** This provides deterministic working-directory ownership, not filesystem sandboxing.

### 18. Policy cannot enforce runtime restrictions after a process starts

**Status: BY DESIGN / DEFERRED until a concrete isolation requirement exists**

- **What changed:** No fake post-start shell restrictions were added.
- **How:** Current policy controls whether a capability such as `SH` can be invoked, allowed environment names, limits, and timeout. It does not claim to constrain arbitrary filesystem/network/process behavior once unrestricted host shell is granted.
- **Future direction:** If a policy must safely process untrusted external input without host authority, use narrow structured commands and/or real OS/container/account restrictions.
- **Related:** See #10.

### 19. Policy evidence is rebuilt from mutable live execution history

**Status: RESOLVED**

- **What changed:** Policy decisions consume immutable evidence snapshots rather than mutable persistence entities.
- **How:** `PolicyEvaluationService` converts execution records into `ExecutionEvidence` values and builds policy evaluation contexts from those snapshots before invoking policy logic.
- **Validation:** `tests/agent-architecture.ps1` asserts `ExecutionEvidence` ownership; `tests/policy-decoder.ps1` exercises completion decisions against immutable evidence values.

### 20. Policy actions are opaque `Kind + Request string` values

**Status: RESOLVED**

- **What changed:** Policy action evaluation uses structured command identity instead of opaque string kind/request pairs.
- **How:** `PolicyAction` carries the resolved `CommandDefinition` plus command body. Policy validation therefore reasons about the registered command capability directly.
- **Validation:** `tests/policy-decoder.ps1` verifies allowed/denied structured `SH` actions.

### 21. Completion is model-claimed before system verification

**Status: RESOLVED and intentionally retained as system-verified completion**

- **What changed:** `<DONE>` is treated as a completion **claim**, not unquestioned completion.
- **How:** Policy completion settings can require one or more successful command types. `PolicyDecoder`/policy evaluation checks persisted `Completed` command evidence before accepting the claim. Missing or failed required evidence rejects completion.
- **Validation:** `tests/policy-decoder.ps1` covers missing `<DONE>`, accepted `<DONE>`, missing evidence, failed evidence, and successful required evidence.

### 22. Execution records lack requester identity

**Status: REJECTED; redundant requester mechanism removed**

- **What changed:** The earlier `X-MEZHS-Requester` header, `Requester` persistence/DTO/model field, dashboard display, debug-log field, and `MEZHS_REQUESTER` shell environment value were removed.
- **How:** Durable causal provenance remains represented by `Source`, `SourceReference`, `ParentExecutionId`, `CorrelationId`, chat identity, and command/message identity. Those values describe where the work actually came from. An unauthenticated caller-supplied requester string did not add trusted identity.
- **Migration:** Existing SQLite databases drop the legacy `Executions.Requester` column during initialization.
- **Validation:** `tests/agent-architecture.ps1` verifies requester semantics are gone while explicitly requiring legacy-column cleanup; API smoke verifies normal execution through both direct API and Agent Web without requester machinery.

### 23. Caller-provided environment variables can influence shell execution

**Status: RESOLVED**

- **What changed:** Caller environment mutation is opt-in per policy.
- **How:** AgentService rejects environment names not present in the selected policy allowlist. Runtime-owned `MEZHS_*` names are reserved and cannot be supplied by callers. Approved values are persisted with execution context and passed to child shell processes.
- **Validation:** `tests/agent-api-smoke.ps1` verifies an approved test variable reaches the shell and a `PATH` override is rejected.

### 24. Cancellation is cooperative and does not model acknowledgement/stopping states

**Status: RESOLVED**

- **What changed:** Running work has a durable cancellation-request acknowledgement, one root cancellation owner, and reconciliation for the claim-to-token-registration race.
- **How:** Only root Agent executions may be cancelled through the API. `RequestCancel` transitions a running root to `CancelRequested`; after the worker registers its cancellation token it re-reads durable state and immediately cancels if the request landed in the narrow claim/register window. Child shell cancellation stays internal to root cancellation.
- **Validation:** `tests/agent-runtime-resilience.ps1` rejects direct shell-child cancellation and verifies the normal `CancelRequested` -> `Cancelled` lifecycle; architecture checks require durable race reconciliation.

### 25. Configuration supports only version 1 with no migration path

**Status: RESOLVED for configuration decoding**

- **What changed:** Configuration version dispatch exists before version-specific decoding.
- **How:** The loader reads/validates the version and routes to the appropriate decoder rather than assuming every future file must match v1 implicitly.
- **Result:** A future v2 can be introduced through an explicit decoder/migration seam instead of silently changing v1 interpretation.

### 26. Storage path resolution can escape an intended storage root

**Status: REJECTED as an Agent invariant**

- **Reasoning:** Agent configuration intentionally accepts explicit workspace/storage paths; there is no defined Agent storage-root contract that those paths are supposed to remain beneath.
- **What changed:** No artificial path-root restriction was added to Agent storage.
- **Related future work:** `Mezhs.Log.Shared` does have a deliberate log-root invariant for log files. If common SQLite infrastructure is later extracted/reused from `LogSql`, generic database path ownership should be separated from log-file path ownership instead of applying the log-root rule to every MEŽS database.

### 27. API persistence records are exposed directly instead of dedicated API DTOs

**Status: RESOLVED**

- **What changed:** HTTP endpoints expose dedicated Agent API views rather than persistence records.
- **How:** `AgentApiMapper` maps durable records into `AgentExecutionView`/other API DTOs. Persistence-only details such as execution environment remain internal.
- **Validation:** `tests/agent-architecture.ps1` asserts DTO mapping ownership; API smoke exercises the public shape.

### 28. Operational metrics are missing

**Status: DEFERRED; speculative implementation removed**

- **What changed:** The earlier generic `/v1/metrics` endpoint, `AgentMetrics` counter service, client DTO/method, and persisted aggregate helper were removed.
- **Reasoning:** Nothing consumed them, and the design mixed values derivable from durable execution history with an ephemeral policy-denial counter. Maintaining that API before a real operational use case would create speculative surface area.
- **Future direction:** When the dashboard or another consumer needs metrics, define the concrete required aggregates and derive/persist them consistently from the durable execution model.
- **Validation:** `tests/agent-api-smoke.ps1` verifies `/v1/metrics` is absent; `tests/agent-architecture.ps1` asserts the unused metrics infrastructure is gone.

### 29. Repository/file modification workflow needs safer verification

**Status: RESOLVED as repository workflow**

- **What changed:** Agent-driven repository changes are governed by `.agents` workflow instructions rather than relying on ad-hoc editing habits.
- **How:** Changes require review-branch work, owner/scope inspection, strongest practical validation, adversarial/final-diff review, and PR update. When local tooling is unavailable, temporary CI on the review branch is the fallback and must be removed afterward.
- **Validation used for this foundation:** Final task-specific compares were inspected for unrelated changes; temporary Windows validation workflow was deleted after the successful run.

### Low priority

### 30. Temporary Windows command files may remain if cleanup fails

**Status: RESOLVED by removing the temporary-file design**

- **What changed:** Shell execution no longer creates temporary `.cmd` files.
- **How:** Windows starts `cmd.exe /D /Q` and streams the exact shell body through redirected standard input; Unix-like systems stream to `/bin/sh` the same way. stdout/stderr remain redirected and captured.
- **Result:** There is no command temp file to leak, delete, race over, or clean up after failure.
- **Validation:** `tests/agent-architecture.ps1` rejects temp-command-file logic; `tests/policy-decoder.ps1` verifies multiline shell bodies, exit codes, UTF-8/Latvian text, and timeout behavior.

## Current remaining work from this review

Two active follow-up tasks remain after this review:

- **#14 — Process/service isolation:** design and implement the dedicated MEŽS supervisor/launcher.
- **Common SQLite/LogSql storage foundation:** extract/reuse the generic SQLite ownership from `Mezhs.Log.Sql` for main MEŽS API and Agent persistence. Keep generic database path/connection/transaction/migration mechanics separate from `Mezhs.Log.Shared` log-root and notes-file semantics; application databases must not pretend to be log files. Migrate the existing main/API/Agent SQLite writers only after that common ownership boundary is clean.

Items **#10** and **#18** are explicit capability/security design decisions rather than unfinished code. **#28** is intentionally deferred until a concrete metrics consumer exists. All other numbered findings are resolved, rejected as invalid/redundant, or closed for the current foundation scope.