# Agent API Foundation TODO

This file collects issues found during review of the `agent-api-foundation` branch.

## User-reported UI and runtime issues

1. Prompt textbox jumps while typing on line 2 or later.
   - When the caret is on the second or later line, the UI above the textbox visibly jumps on each typed character.

2. Chat UI does not render Markdown/code formatting correctly.
   - Raw markers such as triple backticks, `##`, and other Markdown syntax appear directly in chat.
   - Code blocks and headings should be rendered instead of shown as raw markup.

3. Automatic continuation prompt is ambiguous and should come from policy configuration.
   - Current automatic text: `Continue the assigned agent task according to the applicable policy.`
   - It should explicitly tell the model how to stop, for example that it must return `<DONE>` when the task is complete.
   - The full continuation message should be configurable in policy/runtime configuration, not hardcoded.

4. Some shell commands are not formatted correctly in the UI.
   - Command blocks/results should use one consistent shell/code presentation.

5. After restarting a chat, previously executed shell commands show `NOT EXECUTED`.
   - Execution state must be persisted/restored correctly so historical commands keep their actual executed status.

6. Shell execution can remain stuck for several minutes in the native chat UI.
   - Observed while a simple generated shell block was still shown as executing after about four minutes.
   - Investigate command lifecycle, timeout propagation, process completion detection, and UI refresh/state synchronization.

## Review findings

### High priority

7. Execution queue is unbounded.
   - Use a bounded queue and backpressure/admission control.

8. Agent API has no authentication boundary.
   - Anyone who can reach the listener can potentially start, inspect, pause, or cancel executions.

9. CORS currently allows any origin, header, and method.
   - Restrict in non-development configurations.

10. Shell capability boundary is too broad.
   - Policy currently gates access, but allowed shell execution still reaches the host shell directly.
   - Introduce stronger execution/sandbox boundaries before remote exposure.

11. No rate limiting or execution admission control.

12. Agent-specific integration tests are missing.
   - Cover execution lifecycle, restart recovery, cancellation, policy denial, concurrency, shell timeout, and persistence.

13. Debug-log endpoint can expose sensitive operational data.
   - Protect it with authentication/authorization and consider disabling it outside development.

### Medium priority

14. MEZS services and the controlling shell need process isolation.
   - Services must run in a detached process/session so stopping/restarting them does not kill the command interface controlling them.

15. Worker task tracking is more complex than necessary.
   - Active task exceptions can be observed late and lifecycle handling can be simplified.

16. AgentStore serializes all writes through one global application lock.
   - Acceptable for now, but it will become a concurrency bottleneck.

17. Shell execution has no explicit working-directory/workspace isolation.

18. Policy cannot enforce runtime restrictions after a process starts.
   - Future limits may need resource, filesystem, network, and process controls.

19. Policy evidence is rebuilt from mutable live execution history.
   - Consider immutable evidence snapshots for deterministic/auditable policy decisions.

20. Policy actions are opaque `Kind + Request string` values.
   - Structured action metadata would make policy enforcement safer and easier.

21. Completion is model-claimed before system verification.
   - Treat model completion as a request/claim and verify required evidence before accepting it.

22. Execution records lack requester identity.
   - Record who/what requested an execution in addition to generic source/sourceReference.

23. Caller-provided environment variables can influence shell execution.
   - Define ownership and restrict/namespace user-supplied environment variables.

24. Cancellation is cooperative and does not model acknowledgement/stopping states.
   - Consider states such as CancelRequested/Stopping/Stopped where needed.

25. Configuration supports only version 1 with no migration path.

26. Storage path resolution can escape an intended storage root.

27. API persistence records are exposed directly instead of dedicated API DTOs.

28. Operational metrics are missing.
   - Queue length, active executions, failures, duration, shell failures, and policy denials should be observable.

29. Repository/file modification workflow needs safer verification.
   - Before committing automated file edits, verify the resulting diff contains only intended changes.

### Low priority

30. Temporary Windows command files may remain if cleanup fails.
   - Cleanup currently ignores IO/permission failures.
