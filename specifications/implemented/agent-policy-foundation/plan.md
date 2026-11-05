# Agent policy foundation — task 1 of sandboxed agent jobs

**ID:** agent-policy-foundation  
**Level:** High-risk — agent permissions, persisted policy enforcement and execution recovery.  
**Status:** Implemented on development branch; independent code review and testing pending.
**Parent:** [Sandboxed agent jobs V1 architecture](../../planned/sandboxed-agent-jobs/plan.md). This child is a bounded extraction of owner-approved requirements, not a replacement or amendment to the parent.

## Goal and scope

Make existing Agent API policy and completion behavior ready for subsequent sandbox integration, **without introducing Docker, Jobs API or an Executor refactor**. All changes belong in existing Agent API policy, command, service, persistence and focused tests. Retain behavior for ordinary trusted host agents except for the explicitly approved protocol and snapshot corrections below.

### Included

1. **Project file policy:** Extend the existing execution creation policyId resolution to accept a local path to a project .mezhs/agent.yaml, in addition to existing registered policy IDs. Reuse the current PolicyDefinition, PolicyDecoder, validation and PolicyContext path as much as possible. Define and test a minimal standalone project policy file representation compatible with those existing settings; do not interpret this as a complete Agent server configuration. Load it once when creating the execution, validate connection/model/allowed environment as for registered policies and record the resulting effective policy snapshot. An invalid/unreadable policy file must fail the request clearly, not silently resolve a different policy.

2. **Immutable effective policy per execution:** Existing ExecutionRecord.PolicySnapshot is persisted, but AgentWorker currently obtains live policy from PolicyRegistry using execution.PolicyId. Compile policy settings/instructions/actions/completion validators from the **stored snapshot for the execution**, for ordinary processing, queued work and restart/recovery. Do not re-read the file or registry after creation. New executions can use new file/registered-policy settings. A corrupt or missing persisted snapshot is an explicit execution failure, never a fallback to a current mutable policy. Preserve chat validation/admission, existing policy ID compatibility and safe command behavior.

3. **Protocol ABORT:** Add the approved standalone uppercase <ABORT> marker as an immediate unsuccessful terminal outcome that bypasses completion evidence requirements. It must not run pending <SH> or <ATTACH> commands from the same assistant reply, irrespective of marker order. Preserve normal DONE semantics, visible-text parsing, escaped markup behavior and ordinary cancellation. No new resumable pause lifecycle.

4. **Successful command evidence:** Extend completion.requiredSuccessfulCommands beyond registered command names to support an **exact SH body** or an SH body **prefix ending in a single asterisk**, as specified in the parent. Retain backward compatibility with existing name-only checks. Require evidence of a completed/successful command from the correct execution/correlation, with matching actual persisted SH text; a failed command, unrelated command, or merely echoing a required string cannot fulfill completion. Keep any matching syntax minimal, explicit and covered by tests; do not add a new JOB_COMMENT command or a comment verification service. Failed HTTP commands remain failed shell commands; the later Jobs API provides the job-scoped endpoint.

### Explicitly excluded

- New SandboxId request/model/persistence, HostExecutor/SandboxExecutor, Docker command routing or environment isolation — **Task 4**.
- Sandbox-only ATTACH rejection — **Task 4**, when the sandbox identity exists. Keep existing local ATTACH working here; replacement/removal is deferred future work.
- Docker containers/NGINX — Tasks 2–3; repository clone/jobs.yaml/redaction — Task 5; queue/Jobs API — Task 6; provider comments — Tasks 7–8; UI — Task 9.
- Multi-agent orchestration plan, new agent statuses beyond the approved ABORT failure behavior, new API endpoints for policy uploads, general configuration hot reload, alternative policy execution engines.

## Existing code touchpoints

- src/Mezhs.Agent.Api/Services/AgentService.cs — resolves only registered policies and stores a snapshot at execution creation.
- src/Mezhs.Agent.Api/Services/PolicyRegistry.cs — policy lookup; extend narrowly rather than registering a global policy for each job.
- src/Mezhs.Agent.Api/Services/AgentWorker.cs — currently calls live PolicyRegistry.Get(execution.PolicyId).
- src/Mezhs.Agent.Api/Policy/PolicyDecoder.cs and PolicyModels.cs — compile settings, instructions, allowed actions and completion checks.
- src/Mezhs.Agent.Api/Policy/PolicyEvaluationService.cs — uses durable shell evidence and correlation; avoid duplicate stores.
- src/Mezhs.Agent.Api/Commands/Parser.cs, Registry.cs and Interpreter.cs — preserve the existing restricted uppercase-command parser, escaped text and execution ordering.
- src/Mezhs.Agent.Api/Persistence/AgentStore.cs, Models/AgentModels.cs and current Agent API tests — persisted policy snapshots and recovery.
- Consult specifications/current/agent-runtime.md and existing tests before editing.

## Acceptance criteria

- [ ] Normal registered policy execution remains backward compatible; existing Agent API and relevant test suites pass.
- [ ] An execution can use valid project .mezhs/agent.yaml from a host-local path and rejects invalid or missing policies; its effective settings are stored once.
- [ ] Changing a registered policy or project file after execution creation cannot change any queued, running or recovered execution's prompts, authorization, command timeout or completion rules.
- [ ] Existing durable policy snapshots are interpreted consistently; corruption does not silently use live policy.
- [ ] ABORT causes unsuccessful termination even with unmet completion requirements; no SH/ATTACH from that same reply executes; DONE remains subject to its original checks.
- [ ] Name-only requiredSuccessfulCommands still work; exact and prefix SH requirements accept only successful persisted matching commands from the execution and reject unrelated, failed and nonmatching commands.
- [ ] Existing local ATTACH and host shell workflows are unchanged; no container or Jobs API components introduced.

## Verification and delivery

Run focused unit/integration tests covering: policy file loading and bad YAML; snapshot use after a policy change and recovery; ABORT with pending commands before/after the marker; successful command name/exact/prefix matches, failures and false positives; existing DONE and escaped command handling; trusted host ATTACH. Run the relevant dotnet build/test entry points and record commands/results. Do not claim actual restart or provider end-to-end coverage unless executed.

Follow AGENTS.md and .agents/workflow.md. Because this task is high-risk, obtain the independent plan-review gate where required by the workflow, then implement on branch agent-policy-foundation from the latest approved main. Do not merge implementation without the owner's explicit approval. Before independent code verification move the completed child plan to implemented/ **on the development branch only**. Obtain independent code review and independent testing; report exact SHA, tests, remaining risk and PR/branch. Do not change the version or changelog before the approved merge.
