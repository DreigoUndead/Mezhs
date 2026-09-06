# Agent API Foundation TODO

This file collects issues found during review of the `agent-api-foundation` branch.

## User-reported UI and runtime issues

1. Prompt textbox jumps while typing on line 2 or later.
   - Resolved in the shared composer by resizing before paint.

2. Chat UI does not render Markdown/code formatting correctly.
   - Resolved in the shared chat renderer.

3. Automatic continuation prompt is ambiguous and should come from policy configuration.
   - Resolved: runtime messages are configurable and explicitly explain `<DONE>`.

4. Some shell commands are not formatted correctly in the UI.
   - Resolved through persisted command evidence and consistent command/result cards.

5. After restarting a chat, previously executed shell commands show `NOT EXECUTED`.
   - Resolved by persisting command name, originating assistant message id, and command index.

6. Shell execution can remain stuck for several minutes in the native chat UI.
   - Shell timeout/cancellation owns process-tree termination and bounded shutdown waits; lifecycle tests cover this path.

## Review findings

### High priority

7. Execution queue is unbounded.
   - Resolved after sanity review: SQLite is the authoritative bounded root-execution queue.
   - Admission is atomic against durable outstanding work; the worker keeps only a wake signal and atomically claims eligible queued rows.
   - Same-chat serialization is checked before a queued execution becomes `Running`, and queued work survives Agent restart.

8. Agent exposure must have one explicit trust boundary.
   - Current architecture deliberately uses the local machine as the API boundary: Agent API and Agent Web remain loopback-only and CORS-closed.
   - Do not add a bearer layer between cooperating local processes. If a remote Agent entry point is introduced later, authenticate and authorize at that external boundary.

9. CORS currently allows any origin, header, and method.
   - Resolved: Agent API does not enable cross-origin browser access.

10. Shell capability boundary is too broad.
   - Sanity conclusion: direct host-shell execution is intentional when a policy explicitly allows `SH`; MEŽS must not pretend command filtering is a sandbox.
   - Loopback does not make future external event content trusted. Before WhatsApp/Jira/reminder policies expose powerful capabilities to untrusted input, prefer narrow structured tools or real OS privilege/isolation boundaries.

11. No rate limiting or execution admission control.
   - Resolved: durable admission caps outstanding root executions and returns HTTP 429 without creating a rejected execution row.

12. Agent-specific integration tests are missing.
   - Resolved for the foundation: lifecycle, restart recovery, cancellation, policy denial, durable admission/serialization, shell timeout, and persistence are covered.

13. Debug-log endpoint can expose sensitive operational data.
   - Keep it inside the same loopback-only Agent boundary.
   - If Agent access ever becomes remote or multi-user, this endpoint must be covered by that external authentication/authorization model.

### Medium priority

14. MEZS services and the controlling shell need process isolation.
   - Still open. Services need one dedicated supervisor/launcher that owns detached process/session startup.

15. Worker task tracking is more complex than necessary.
   - Resolved: fixed worker consumers own execution concurrency; no dynamic task set remains.

16. AgentStore serializes all writes through one global application lock.
   - Resolved: the application lock is gone. SQLite WAL + busy timeout + independent connections own concurrency; shared-cache mode is not used.

17. Shell execution has no explicit working-directory/workspace isolation.
   - Resolved: shell execution uses one configured workspace.

18. Policy cannot enforce runtime restrictions after a process starts.
   - Sanity conclusion matches #10: `SH` is not a security sandbox. Add actual OS-level controls only where a concrete untrusted capability boundary requires them.

19. Policy evidence is rebuilt from mutable live execution history.
   - Resolved: policy evaluation receives immutable execution-evidence snapshots.

20. Policy actions are opaque `Kind + Request string` values.
   - Resolved: actions carry the command definition plus body.

21. Completion is model-claimed before system verification.
   - Resolved and intentionally retained: `<DONE>` is a claim; policy may require persisted successful command evidence before accepting it.

22. Execution records lack requester identity.
   - Rejected after sanity review. `Source`, `SourceReference`, parent execution, and correlation id already own durable causal provenance.
   - An unauthenticated `Requester` header duplicated those semantics without creating trusted identity, so the header/column/DTO/environment field were removed.

23. Caller-provided environment variables can influence shell execution.
   - Resolved: policies explicitly allow environment names and `MEZHS_*` is runtime-reserved.

24. Cancellation is cooperative and does not model acknowledgement/stopping states.
   - Resolved with durable `CancelRequested` before terminal `Cancelled` acknowledgement for running work.

25. Configuration supports only version 1 with no migration path.
   - Version dispatch now exists before version-specific decoding so later versions have an explicit seam.

26. Storage path resolution can escape an intended storage root.
   - Review found no intended Agent storage root invariant to enforce; workspace/storage are explicit configuration paths.

27. API persistence records are exposed directly instead of dedicated API DTOs.
   - Resolved with dedicated execution views/mapping.

28. Operational metrics are missing.
   - Deferred after sanity review. The generic metrics layer had no consumer and mixed durable derived values with an ephemeral policy-denial counter.
   - Add concrete, durable aggregates when the dashboard or another real operational consumer needs them instead of maintaining speculative telemetry now.

29. Repository/file modification workflow needs safer verification.
   - Covered by the repository `.agents` change workflow and final-diff validation.

### Low priority

30. Temporary Windows command files may remain if cleanup fails.
   - Resolved by streaming shell text over stdin; temporary `.cmd` files are no longer created.
