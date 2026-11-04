# MEŽS sandboxed agent jobs — architecture handoff

**ID:** `sandboxed-agent-jobs`  
**Level:** High-risk (architecture, isolation/security, container lifecycle and concurrency).  
**Status:** Independently reviewed on 2026-11-05 against `main` at `412047e426d0dc50e2bffac212661c10be3f1db4`; **PASS WITH OPEN QUESTIONS**. No application code implemented. The architecture/ownership is viable, but the explicit pre-implementation security and lifecycle gates in section 11 remain mandatory.
**Repository:** `DreigoUndead/Mezhs`.
**Plan location:** `specifications/planned/sandboxed-agent-jobs/plan.md` on `main`. Implementation branch and PR are not yet assigned.
**Related plan:** `docs/plans/multi-agent-orchestration.md` on `plan/multi-agent-orchestration`. That separate plan is not implemented and will be reconciled later; do **not** implement multi-agent orchestration as part of this task.
**Intent:** Provide safe, disposable execution environments for code-review/development jobs without removing the ability to run trusted local MEŽS agents directly on the host.

This document is the **current decision record**. It supersedes earlier discussion sketches that put container CRUD and scheduling inside Agent API, allowed the repository to choose host execution, or made a Git-specific service inherit Agent API.

## Goal and scope

Introduce sandboxed development and code-review jobs while preserving optional trusted host execution for local MEŽS. This plan covers ownership, sandbox assignment, project configuration, job scheduling, Git workspace and reporting, UI integration, and isolation validation. It does **not** implement multi-agent orchestration or prescribe every unresolved API/schema detail.

## Approach

Jobs API manages jobs, Git operations, workspaces, container queue/CRUD, and cleanup. Agent API manages agent executions and an immutable sandbox assignment. ContainerTool and SandboxExecutor remain focused CLIs. Trusted host configuration, not repository policy, decides whether host execution is allowed. Detailed contracts and superseded alternatives follow below.

## Acceptance criteria

- [ ] Jobs API is the single authority for job/container CRUD and scheduling; Agent API remains the authority for agent execution state.
- [ ] The same Agent API can route ordinary SH commands to host Executor or to an assigned sandbox, subject to host-controlled admission.
- [ ] Restricted deployment fails closed when sandbox assignment is absent or invalid; project YAML cannot grant host execution.
- [ ] A queued job runs only when allocated capacity is available; Linux and Windows execution paths are supported as configured.
- [ ] Git credentials and protected original checkout remain outside the writable sandbox workspace; agent-requested Git actions go through Jobs API.
- [ ] Agent pause/kill/terminal states, cleanup ownership and container deletion protection follow the agreed state contract.
- [ ] End-to-end network/permission controls demonstrably prevent unauthorized host and API access.

## Verification

High-risk process: **independent plan review before implementation**, then independent code review and independent testing. Verify target-platform container launch and mounts; SH output/exit status; timeout and process-tree kill; queue concurrency, failures and deletion races; host-execution denial; HTTP method/path/TCP restrictions and direct-egress bypass attempts; Git redaction/restore safety; cleanup and terminal reporting. See section 12 for staged delivery and acceptance scenarios.

## 1. Mental model: three lifecycles, three owners

A **job** is the external work item and its overall lifecycle (GitHub/GitLab/Jira trigger or user request, checkout, scheduling, reporting, final cleanup).
An **agent execution** is one AI agent's task, conversation, commands, status and prompts.
A **sandbox** is an isolated, managed execution environment assigned to an agent execution.

Do not equate job, execution and sandbox identifiers. They have related but independently owned lifecycles.

| Component | Authoritative responsibility | Explicit non-responsibility |
| --- | --- | --- |
| **`Mezhs.Jobs.Api`** (proposed name; replaces the old "GitTool" concept) | Job lifecycle, Git integration and credentials, original/working directories, resource scheduling, SQLite job/container state, container CRUD API, progress/reporting to caller, post-processing and cleanup | AI reasoning, conversation persistence, shell command interpretation |
| **`Mezhs.Agent.Api`** | Agent creation, task/command state, prompts, policy evaluation, execution statuses, immutable per-execution sandbox assignment, routing to the selected executor | Git cloning and Git credentials, container CRUD, container capacity queue, job/workspace cleanup |
| **`Mezhs.ContainerTool` CLI** (name provisional) | Simple create/start/stop/inspect/destroy operations against configured Docker engine(s), including its own partial-create rollback | Agent/job policy, FIFO queue, capacity accounting, HTTP service, job persistence |
| **`Mezhs.SandboxExecutor` CLI** | Run SH inside an already assigned container using Docker's exec facility; return output/status and support interrupt/timeout semantics | Creating containers, picking a different target, job lifecycle |
| **`Mezhs.Executor`** (existing) | Trusted host-shell command execution and its existing durable execution functions | Docker/job orchestration |
| **MEŽS UI** | Agent UI plus Jobs/Containers views using owning API endpoints | Independent scheduling/container state store |

`Mezhs.Jobs.Api` is an **active API service**, not a passive Git helper. Git functionality is one workflow capability. Later integrations (GitLab, Jira, etc.) can be added to Jobs API without subclassing Agent API or building new API inheritance chains.

Both APIs may run at the same time, on different endpoints, and communicate over the normal typed HTTP client/API contract. Prefer composition and reusable libraries to API-to-API inheritance.

### Sources of truth

- Jobs API SQLite: jobs, pending queue, capacity reservations, sandbox/container management records, workspace associations, job completion/reporting.
- Agent API's own persistence: agents, conversations, execution state and their immutable `SandboxId`.
- Docker/ContainerTool: actual runtime container and engine state; it is **not** the job queue database.
- Git checkout: repository/branch state, only controlled by Jobs API.

No duplicate container CRUD or scheduling implementation in Agent API. MEŽS Agent UI can display Jobs API views by calling/proxying its endpoints.

**Cross-service invariant:** Jobs API alone grants, reserves, revokes and deletes managed sandbox allocations. Agent API alone records and runs agent executions, but may accept a sandbox binding only from an authenticated, authorized caller against an allocation that Jobs API has reserved for that job. The two SQLite databases are *not* one transaction. Jobs API keeps its allocation in a non-deletable `Starting/Attached` state until it has reconciled success/failure of agent creation; response loss must not mean the sandbox is assumed unused. A deterministic job/caller correlation key and a read-back by execution/job identity are sufficient; do not introduce a distributed transaction coordinator. Sandbox ID itself is never authority to attach, delete, or route to a different sandbox.

## 2. End-to-end workflow

1. A webhook, GitHub/GitLab integration, Jira integration or UI caller submits a **job** to Jobs API.
2. Jobs API records the job, authenticates and interprets the request, clones the requested revision into a job-owned host directory and reads the project's configuration files.
3. Jobs API prepares a **sanitized writable workspace** (no mounted `.git` directory, Git credentials or protected original source). It retains the original checkout separately, outside container reach.
4. Jobs API adds the container request to its **single FIFO capacity queue**. All managed containers use the same capacity rule irrespective of origin; there is no `IsManual` special case.
5. When a slot is free, Jobs API calls ContainerTool CLI with the project sandbox configuration path and the host workspace directory. ContainerTool returns an opaque `SandboxId` or sufficient container metadata for that ID to be resolved.
6. Jobs API reserves the allocation against concurrent delete/start, then calls the regular Agent API creation endpoint over an **authenticated service boundary** with a stable job correlation key, authorized effective policy/configuration and **`SandboxId`**. Agent API must verify authorization and allocation validity, then persist the immutable assignment when it accepts the execution; Jobs API records the returned execution ID. If either side crashes or the response is lost, reconcile the known job/execution before releasing the reservation. Jobs API never needs an Agent API subclass. Agent API routes SH commands to SandboxExecutor through the existing durable execution mechanism.
7. The agent operates normally: it generates regular `<SH>` commands, never `docker exec`. SandboxExecutor calls Docker externally, using the assigned container, appropriate working directory and platform shell. The agent sees the available tools/files but does not choose whether execution is sandboxed.
8. During execution, the sandbox can reach only a narrow, host-approved Jobs API surface to report progress, comment on its own branch or request allowed Git operations. The Jobs API authenticates the calling sandbox/job identity and authorizes the requested action against **that specific job and repository**; HTTP method/path filtering alone is not authorization. A trusted gateway may inject the authenticated identity after stripping any caller-supplied identity headers, or a short-lived job-scoped credential may be used. Neither grants access to generic container CRUD, other jobs or Git credentials. Jobs API handles Git operations.
9. Jobs API **observes Agent API execution status**; Agent API remains authoritative for it. Once the agent is terminal **and no Agent-owned SH invocation is still active or eligible to start**, Jobs API performs any required final processing, requests sandbox/proxy/network destruction, and only then deletes its job-owned workspace. Independent background services inside the container do **not** have to exit before Docker stop/destroy: that operation terminates them; success requires confirmation that the container and its mounts are gone. Destroy must be confirmed before releasing capacity and starting the next pending job. If agent status, Agent-owned SH termination or Docker destruction cannot be confirmed, mark cleanup blocked and retain the allocation rather than deleting a potentially active mount. Report the final job outcome to the original caller.

Jobs API is the **cleanup owner** for managed jobs (this expressly replaces the earlier proposed Agent API cleanup ownership). Agent API does **not** independently destroy the same sandbox on DONE/ABORT: there must be one cleanup owner, not two races. For containers not associated with a running execution, Jobs API's CRUD path can delete them under the same usage-state rules.

If startup fails after a partial container create, the owner initiates cleanup (`try/catch` or equivalent), and ContainerTool's destroy/rollback command must tolerate partially created resources. Reconcile uncertain Agent API creation before cleanup. No elaborate recovery subsystem was agreed for V1.

**Important sequencing:** no filesystem cleanup before Git post-processing that still requires workspace files, and no workspace deletion before the container mount is released. Avoid requiring a second layer of file export from Docker; the sandbox receives a writable mount of the Jobs API's prepared workspace.

## 3. Executor assignment contract

The **repository must not select host vs. sandbox execution**. A repo-supplied agent policy could otherwise ask to run on the host. The execution target is a **trusted host/caller concern**.

Extend the normal Agent API creation request (the future canonical `POST /v1/agents` from the orchestration plan, or its equivalent when this lands first) with a nullable `sandboxId`. Do not introduce a special `/v1/sandboxed-agents` path.

Illustrative request, **not a frozen DTO schema**:

~~~json
{
  "name": "Review PR #42",
  "task": "Review and submit findings",
  "policyId": "project-review",
  "sandboxId": "job-482-container"
}
~~~

- `sandboxId != null`: use SandboxExecutor for **all** SH commands of that execution.
- `sandboxId == null`: use the existing host Executor **only when trusted host configuration permits host execution**.
- Work/restricted deployment: host execution disabled; missing or unusable sandbox ID is rejected, **never** silently redirected to host execution.
- Personal/local deployment: host execution and sandbox execution may both be allowed.
- Persist the sandbox assignment **as a first-class Agent API execution field** at creation, not merely in shell environment variables. It is immutable throughout the execution, including restart/recovery. Bind a sandbox-owned chat/session to the same job/sandbox until its job is terminal: later prompts or new execution requests on that chat must never silently switch to the host or another sandbox. A different job gets a different chat/session. Avoid a duplicate free-floating chat ownership mechanism where one persisted binding and validation suffice.
- No user-controlled policy, prompt, shell text or ordinary environment variable may override the target. On restricted installations, reject direct untrusted creation of an arbitrary `sandboxId`; admit only trusted Jobs API allocations (and explicitly authorized local/manual management when enabled). Validate the job-to-sandbox claim under Jobs API's start/delete serialization. Sandbox IDs are identifiers, **not authentication secrets**; do not trust possession of one.
- Sandbox IDs must resolve to the correct Docker **engine/context and container**, not merely assume all engine-local IDs share one namespace. The exact resolution representation is an implementation detail.
- Manual/host agents can continue to operate without Jobs API. A manually initiated sandbox agent needs an already assigned sandbox from the managed container API/tool path.

### Existing environment mechanism

MEŽS already sets execution context environment variables in `Mezhs.Agent.Api/Commands/Shell.cs`, with definitions in `Mezhs.Console/MezhsExecutionContext.cs` and persistence/launch in `Mezhs.Executor`. Reuse this pattern for **trusted routing context**:

~~~text
MEZHS_SANDBOX_ID=<persisted SandboxId>
~~~

Agent API constructs the value from the execution record, not from submitted shell text or repository YAML. SandboxExecutor may receive it as **host-side metadata**; it must not select its routing authority from an editable environment variable. The persisted trusted assignment selects the engine/container. Neither the ID nor Docker socket should be needed inside the container.

Avoid forwarding a complete Agent API/host environment into the container. Pass the intended sandbox command environment only. The existing host executor snapshots an environment; this is not by itself a contract to forward all those variables through `docker exec`.

### CLI / shell semantics

`<SH>` remains the same agent protocol regardless of target. SandboxExecutor translates it internally to `docker exec` (or platform equivalent). Docker does not require an SSH/HTTP command agent inside the container.

New SH commands get new shell processes in the **same** container and workspace. Persistent files and independently running services remain; transient shell-local variables and `cd` do not automatically carry over between SH blocks.

**Reuse the existing `ExecutorService`/`ExecutorStore` durable execution row and command identity** for both host and sandbox SH. Add only the necessary persisted trusted target/engine metadata and a process-launch branch/adapter in the existing runner, invoking SandboxExecutor as needed. Keep the original SH text in the durable row (not a synthesized shell-escaped `docker exec` string), so restart deduplication, command evidence, history and shell Kill/Restart still have one owner. A separate SandboxExecutor SQLite execution tracker is **not** planned. Do not assume killing a local `docker exec` client reliably kills its process inside the container: container-aware **process-tree interruption**, uncertain/partial starts, exit-code propagation and no replay after ambiguous restart need explicit tests. Do not introduce a custom shell daemon unless a verified limitation requires one.

The current repository's `ExecutorService` is sealed and used directly by `Shell`, `AgentWorker`, `AgentRecoveryState`, `PolicyEvaluationService` and API execution endpoints. Prefer a small extension at the execution/runner boundary over replacing those consumers wholesale. `ExecutorPlatform.Current` currently selects the **host** OS; sandbox shell selection must instead use the **container** OS. `AgentAttachmentStaging` currently reads files under host `AgentOptions.Workspace`; for sandbox jobs, `<ATTACH>` must import only validated host-visible files from the job's mounted workspace, not arbitrary host paths. The prompt's description of `<SH>` must also name the sandbox shell rather than falsely advertising host execution.

## 4. Configuration boundaries

Two **project-owned** files are planned:

| Location | Purpose | Trust |
| --- | --- | --- |
| `.mezhs/agent.yaml` | Agent instructions, model defaults, command policy, required-successful-command rules and project task behavior | Read from project/job, **not authorized** to request host execution |
| `.mezhs/sandbox.yaml` | Desired OS/image, resources, workspace/container mount shape, allowed outbound HTTP endpoint and TCP destinations | Project request, subject to enforced host infrastructure restrictions |

And a **host-owned** configuration (e.g. `containertool.yaml` and host/Agent API settings) controls actual engines/contexts, proxy implementation, capacity, deployment permissions and whether host execution is enabled. Do not give a project file authority over host privilege or the Docker socket.

**Enforced upper bounds:** A project `sandbox.yaml` is a *request*, never permission to choose arbitrary bind sources, privileged/host networking, Docker socket, host PID/IPC namespace, container capabilities, security-profile relaxation, credentials, or unrestricted images/engines. Host configuration approves the image/engine/platform, fixed workspace mount destination, execution user and resource/network maxima. Reject unsupported requests; do not silently accept unsafe defaults. Network destinations requested by the project must be a subset of host-approved destinations, and the default for everything else is deny.

Illustrative project sandbox request:

~~~yaml
container:
  os: windows                 # linux | windows
  image: company/dotnet-build:windows
resources:
  memory: 4g
  cpus: 2
network:
  http:
    - method: POST
      destination: http://jobs.internal/CommentBranch
    - method: POST
      destination: http://jobs.internal/CreateMergeRequest
  tcp:
    - db.internal:1433
~~~

This is an **example**, not approved syntax. The final schema and policy-file delivery contract are still open.

**Required contract before Agent API integration:** `AgentConfigLoader` currently loads a host-level `agent.yaml` (including connections/server), not a per-job `.mezhs/agent.yaml`. Treat project policy as a bounded input, not a replacement host config or registered global policy. Jobs API passes the job's project policy request through a trusted channel; Agent API validates it against host-approved policy/connection/model/command limits, compiles an **effective per-execution policy** and persists enough effective policy data to evaluate permissions and completion consistently after restart. Existing `PolicySnapshot` is persisted, but `AgentWorker` presently evaluates the *live* `PolicyRegistry` by ID; simply populating the column does **not** achieve this. The separate unimplemented multi-agent plan proposes live YAML instead of snapshots. For sandboxed external jobs, preserving the accepted effective security policy over an execution is the safety invariant; reconcile that branch's policy semantics explicitly before any shared runtime migration. Do not silently use live untrusted repo YAML, a new global policy ID, or a second policy evaluator.

Existing `MEZHS_*` runtime environment names are reserved by the Agent API for its internal context.

## 5. Scheduling and container management

**Jobs API**, not ContainerTool or Agent API, owns the SQLite queue and concurrency limit.

- One shared capacity rule for every managed job container; no manual-vs-automated distinction. Proxy sidecars are implementation resources of the same sandbox, **not additional job slots**.
- The counted resource is an **allocated sandbox/container**, including one retained by a paused execution; do **not** count SH invocations as separate container slots.
- An incoming request becomes Pending; Jobs API queues it FIFO. Starting reserves a slot atomically so competing requests cannot overbook capacity.
- Once a container is successfully allocated, it is visible as an existing/created resource. Container creation error releases the reservation and does not block the entire queue.
- A destroyed container releases capacity and triggers admission of the next pending request. Do not free the slot before destruction finishes.
- On a **Jobs API restart**, pending requests are marked Abandoned rather than rescheduled. For V1, previously active jobs enter a **reconciliation/cleanup-required** state and cannot admit fresh agent starts. Rebuild occupied capacity from persisted allocations **and actual Docker containers**; for each interrupted job, request terminal Agent cancellation where reachable, confirm Agent-owned SH completion/interruption, finish safe Git readbacks if needed, then stop/remove the sandbox (which also kills any remaining container-internal background processes). If Agent API or Docker status is unavailable/uncertain, retain allocation and mark it for operator resolution; **do not free the slot or delete the mounted workspace on a guessed terminal state**. This is bounded startup reconciliation, not job continuation or a new retry framework. Agent API's own interrupted reasoning may recover, so it must be prevented from relaunching sandbox work once Jobs API has begun revocation.
- Queue processing is initiated by job submission and container slot release under Jobs API's orchestration. Use a SQLite-transactional reservation, with one active Jobs API scheduler per shared store/engine pool in V1; do not assume multiple API replicas can independently schedule safely. No blocking `while (...) Thread.Sleep(...)` in a simple CLI, and no additional standalone ContainerTool scheduler/service.

ContainerTool remains a straightforward CLI operating on a specified target/engine with no knowledge of Jobs API's queue.

### UI / API

The Jobs/Containers area should expose at least **Existing** and **Pending** tabs, current status and a means to cancel pending requests, inspect existing containers and request deletion. It can appear as new tabs in the Agent UI, backed by Jobs API rather than duplicated Agent API CRUD.

Deleting an existing container must be rejected with a conflict response while an agent execution is **active**, an agent start/assignment is reserved, **or an Agent-owned SH is active or its termination unconfirmed**. Jobs API serializes the start/resume/delete admission transitions through the allocation state; an Agent API status poll alone is not an atomic deletion lock. The UI may present a Yes/No confirmation; on approval it invokes the agent's terminal Kill/cancellation, waits for both Agent and Agent-owned SH termination, then calls Delete. Docker stop/delete is responsible for remaining container background processes. Do not create a secret force-delete bypass in the public API.

**Derived predicate, not extra authoritative persisted boolean:**

~~~csharp
bool IsActive => Status is Running or Waiting or Paused;
~~~

The predicate describes **agent reasoning**, not the full eligibility to delete a container: a terminal agent with a live Agent-owned SH, an outstanding assignment/start or uncertain SH termination still blocks deletion (but Docker destroy may terminate the container's detached background processes). Exact enum names/status mapping need agreement with the existing agent runtime.

Check execution usage and the deletion transition with coordination so that a resume/start cannot race with destruction.

## 6. Agent lifecycle and completion

For this sandbox-job scope, one **root job** is bound to one sandbox and one root task execution. Additional prompts may amend the in-progress task. Do not introduce a way to repurpose the same finished sandbox to a new unrelated job. Multi-agent descendant/child behavior will be handled by the separate orchestration plan; the currently agreed future direction is for descendants of the same job to share the assigned sandbox, but **no additional multi-agent lifecycle rules are specified here**.

Working vocabulary (provisional pending reconciliation with the separate plan):

| Status | Active for container deletion | Intended meaning |
| --- | --- | --- |
| Pending/Queued | Job scheduling state, not a running agent | Await container capacity |
| Running | Yes | Processing work |
| Waiting | Yes | Blocked on awaited work |
| Paused | Yes | Resumable; keeps sandbox |
| Stopped | No | Terminal, not resumable |
| Completed | No | Terminal successful execution |
| Failed/Errored | No | Terminal unsuccessful execution |
| Cancelled | No | Terminal cancellation |

**Verified runtime mismatch:** current `AgentExecutionStatus` has `Queued`, `Running`, `CancelRequested`, `Completed`, `Failed`, `Cancelled`; `AgentService.SetPaused` sets a **chat flag and cancels running work**, not a resumable paused execution. Current `/v1/executions/{id}/kill` is **shell-only**; root execution cancellation is a different route. This plan's Pause/Resume/terminal Kill therefore need actual lifecycle work, not a status rename. The separate unimplemented multi-agent plan uses **Stopped** for *resumable*, opposite to this table. Preserve this plan's behavior choice for now (`Paused` resumable, `Stopped` terminal) and **request owner reconciliation before implementing shared Agent lifecycle/status changes**; do not introduce two incompatible lifecycle APIs. The choice of labels must not change the stop/resume/kill invariants.

Desired behaviors:
- **Pause (new behavior, not existing `SetPaused`):** interrupt and **confirm termination of** the current SH process tree while retaining sandbox/workspace, then allow additional prompts while paused. No new commands run until Resume. Account for the current runtime's cancellation/recovery behavior and preserve ordered prompts; neither cancelled root work nor a stale assistant reply may accidentally execute in the paused state.
- **Resume:** continue under the same sandbox with updated prompts/context, following the eventual canonical Agent runtime semantics.
- **Kill:** terminally cancel the root agent and interrupt all its running SH, confirm those SH commands are no longer executing, and prevent recovery/Resume from reviving it. Jobs API then destroys the container, including any independent background processes; no need to separately track each container-internal process. This is not current shell-only `/kill` and not an alias for Pause.
- **`<DONE>`:** successful agent completion after policy requirements.
- **`<ABORT>`:** a new standalone termination marker; ends the agent as Failed immediately, regardless of required-successful-command policy. When returned together with pending executable blocks, do not run them.

Existing `requiredSuccessfulCommands` supports **command names only**. A minimal extension for exact SH request text (or trailing `*` prefix) may improve completion prompts, but a successful `curl` command/exit code is **not proof** of authorized Git side effects: it can hit the wrong endpoint, receive misleading output or be retried. Jobs API's own job-scoped action result/post-processing remains authoritative for Git write/MR completion. Do not add a second generic outcomes framework: read Jobs API's existing action state before marking the **job** successfully reported, and make such operations idempotent for a job/action key. Runtime instructions must list missing required shell commands plainly on rejected DONE, with the ABORT escape route when completion is impossible. Ensure HTTP shell failures produce nonzero exits (e.g. `curl --fail-with-body`).

Jobs API polls/reads the Agent API's persisted execution status; callbacks are not required. Jobs API separately owns reporting back to the original Git/Jira/UI caller. Agent API is not expected to understand which trigger initiated the task. **Agent DONE means the agent completed; it does not by itself mean Git publication/reporting succeeded.** Jobs API owns the final job state after its required post-processing.

## 7. Filesystem and Git trust boundary

Jobs API owns both the source checkout and the cleanup of the complete job directory. Example:

~~~text
jobs/job-482/
  repository/      # Original clone incl. .git and protected source; NOT mounted
  workspace/       # Sanitized writable copy; mounted read/write in sandbox
~~~

Git credentials, git push/commit/MR creation and source-system authorization remain in Jobs API, not in the sandbox or Agent API. The agent reports progress and requests permitted Git actions via Jobs API; it does not use Git credentials.

The originally proposed `//<SecurityCritical> ... //</SecurityCritical>` redaction mechanism may strip protected sections from the sandbox copy. Exact transformation/restoration and merging algorithm is **unresolved** and security-sensitive: do not commit redacted placeholders over original protected code or allow agent output to overwrite preserved protected segments. **Do not run a job requiring protected-source redaction until the transformation/restore/merge gate is designed and tested**; ordinary repositories without protected markers can be piloted first. The full original checkout stays outside the mounted workspace for safe reconstruction and diff application.

Before mounting, produce an **independent copy** with no `.git`, credentials, protected original paths, symlinks/hardlinks escaping the allowed workspace or other host file references that could expose the original checkout. Never trust a repository-controlled path or mount source as a host filesystem path. On Git post-processing, apply only reviewed workspace changes to the original checkout; never grant the sandbox Git credentials or direct original checkout access.

Mount only paths needed for the job. Ensure configured Windows/Linux Docker engines can actually mount the job workspace on the host where Jobs API prepared it; this constraint must be validated, especially for distinct engine hosts.

Cleanup ordering: finish required Git operations/readbacks; stop/destroy container and proxy, detach mount; then delete the Jobs API-owned job directory. Failure and cancellation must release resources without inventing a complicated retry framework.

## 8. Network boundary and deployment security

Sandboxed code is **untrusted execution**, even when its prompt or project YAML appears benign.

The previously agreed intent is a per-sandbox network isolation layer with **explicit allowed HTTP methods/paths (and, when needed, query constraints)** for limited Jobs API operations, plus separately authorized TCP destinations for database access. A simple hostname/port allowlist is insufficient for Git/Jobs API endpoints. **Every allowed Jobs API operation also needs job-scoped authentication/authorization**; method/path filters are defense in depth, not the sole gate. Use existing NGINX/Envoy (or equivalent maintained component), **not a custom application firewall**. Earlier discussion preferred one proxy companion per sandbox to avoid central mutable rule ownership; if a different supported Windows enforcement layout is needed, preserve the per-sandbox *policy* and isolation invariant rather than requiring a Linux sidecar on Windows.

**Implementation validation still required:**
- Linux and Windows workloads must be supported **concurrently**, potentially by different Docker engines/contexts. Do not assume one Docker Desktop engine automatically runs both container types simultaneously.
- A Linux proxy image is not automatically usable as a Windows sidecar. Specify a compatible per-platform enforcement approach and demonstrate method/path rules and TCP allowlisting.
- A workload container must not bypass the proxy via direct egress, host gateway, Docker daemon/socket, other container network, metadata endpoint, redirects, DNS rebinding, etc. Docker `--internal` alone must not be assumed sufficient without testing. Strict HTTP method/path enforcement needs canonical URL/redirect handling; TLS CONNECT tunnelling is not equivalent.
- HTTP inspection requires controlled forwarding/TLS termination. An arbitrary HTTPS CONNECT tunnel cannot satisfy endpoint-level rules.
- The Docker/container control plane, host directories and Agent API must be reachable only by authorized processes/roles. The work host may additionally enforce no-host-execution by OS permissions.
- No credentials or unrelated host environment variables should leak into container processes through snapshot propagation.

The organization security team owns deployment-level network and permission governance. Do not add speculative preflight cross-validation of every required SH command against the proxy YAML; runtime enforcement and focused validation tests are the agreed approach. **Security feasibility gate:** before expanding into Agent routing or calling Windows/Linux support complete, prove a working egress-enforced example on *each* required container OS/engine, including HTTP path/method restriction, TCP authorization and direct-egress denial. If a target Docker engine cannot enforce that boundary, that target remains unavailable for restricted jobs rather than silently relaxing isolation.

## 9. Deployment modes

**Personal/local MEŽS:** `Mezhs.Agent.Api` may run by itself, using the existing host Executor. If Jobs API is installed, it may also start sandboxed agent jobs. The local user may permit both targets.

**Restricted/work MEŽS:** Host-controlled configuration disables host-shell execution. Agents are created only with authorized, immutable sandbox assignments, and failures to resolve the sandbox **must fail closed**. OS-level permissions should backstop the application rule. Git/Jira jobs go through Jobs API. Work external entry points require appropriate authentication/authorization; the current loopback-only Agent API is not automatically safe to publish.

Illustrative **host-controlled** settings, not finalized names:

~~~yaml
execution:
  allowHost: false
  allowSandbox: true
~~~

These may be set differently on personal and restricted deployments using the **same** Agent API binary. No need to inherit/override Agent API to ensure sandbox-only behavior. Deploy separately configured instances when distinct trust boundaries require distinct privileges; personal/Jobs usage on one trusted machine need not require duplicate Agent API hosts.

## 10. Relationship to existing code

This plan is not a claim that ContainerTool, Jobs API or SandboxExecutor already exist on `main`.

Verified current MEŽS touchpoints (inspect again when implementing):
- `src/Mezhs.Agent.Api/Program.cs` composes existing MEŽS API services and registers `ExecutorService`. Current Agent API binds **loopback only** through `AgentConfigLoader`; `AgentApiClient` also rejects non-loopback targets, and `Mezhs.Agent.Web/Program.cs` proxies `/v1/*` exclusively to Agent API. V1 may co-locate Jobs API and Agent API over authenticated local-only service calls; deploying them on different hosts requires an explicitly secured service-to-service transport/client and UI routing, not simply changing a URL or publishing the existing Agent API.
- `src/Mezhs.Agent.Api/Commands/Shell.cs` directly calls `ExecutorService.Execute` / `WaitAsync` and constructs execution environment.
- `src/Mezhs.Executor/ExecutorEnvironment.cs` snapshots environment and `ExecutorRunner.cs` restores it into child shell processes.
- `src/Mezhs.Console/MezhsExecutionContext.cs` defines current `MEZHS_*` execution variables.
- `src/Mezhs.Agent.Api/Services/AgentService.cs` currently selects host-registered policies, rejects caller-supplied `MEZHS_*` names and creates a **new root execution** for each `Start` request, including reused chats. It does not implement the planned anytime-prompt semantics.
- Other Agent API services reference `ExecutorService` directly: worker/recovery, policy completion, logs, endpoints. `ExecutorStore` already has durable SH identity `(ChatId, TriggerMessageId, CommandIndex)` and restart history; retain it when adding the sandbox target. `AgentAttachmentStaging` currently resolves ATTACH paths on the Agent API host.
- `README.md` warns that existing Agent API host SH is intentionally not sandboxed and intended for loopback trusted use.
- The separate multi-agent plan proposes canonical `POST /v1/agents` and expanded state management, but those are **future** contracts. Avoid prematurely changing that plan's behavior as a side effect of this work.

## 11. Deliberately unresolved before implementation

1. **Before Agent API routing:** Exact trusted Jobs API → Agent API credential/identity mechanism, allocation claim/read-back contract, job-scoped sandbox → Jobs API identity, and effective per-execution policy delivery/validation/persistence. The ownership and fail-closed invariants are fixed in sections 1–4; endpoint DTOs and transport choice are implementation decisions.
2. **Before public CRUD/UI:** Keep distinct container CRUD and agent start operations; use the same Jobs API start/delete reservation for manually created managed containers. Exact DTOs and endpoint names may be decided at implementation, but an accepted yet unresolved agent create must remain a non-deletable reservation.
3. **Before Executor integration:** Engine-qualified opaque sandbox resolution, durable target fields in the existing Executor history, container process-tree interrupt/kill and stale-command recovery semantics. Do not build a second shell history store.
4. **Before claiming restricted target-platform support (stage 2 gate):** Demonstrate the platform-compatible proxy/network firewall arrangement, HTTP method/path and TCP enforcement, non-bypassable egress and mounted-workspace reachability on both the required Linux and Windows Docker engines.
5. **Owner reconciliation before shared lifecycle changes:** This plan treats `Paused` as resumable and `Stopped` as terminal; the separate multi-agent plan uses resumable `Stopped`. Either (A) keep Pause/Resume plus terminal Kill here and later adapt multi-agent terminology, or (B) align both on resumable Stop/Resume and a distinct terminal Kill. Do not implement parallel semantics; choose a single API/status vocabulary with the owner. Descendant lifecycle stays out of scope.
6. **Before protected-source Git jobs:** Precise `SecurityCritical` removal/restore/commit protocol. Such jobs must remain disabled until validated; the initial container/SH/ordinary-Git integration may use repositories without these markers.
7. **During Jobs API persistence implementation:** Apply section 5's bounded reconciliation rule (abandon pending, safely stop/cleanup interrupted active jobs, retain occupied capacity when uncertain). Resolve practical Docker discovery/label details without adding job resurrection.
8. **During Git workflow/UI implementation:** Exact job-scoped action names/DTOs/UI, required Git operations, safe idempotency on uncertain HTTP results, and final job status after authenticated post-processing.

Do **not** treat these as permission to invent new subsystems. Resolve them individually as the relevant implementation stage begins.

## 12. Implementation sequence and verification gates

1. **Architecture handoff (`main`, review only):** confirm ownership, record the independent security review and owner-level lifecycle terminology question; no implementation branch, application code or release metadata in this review.
2. **ContainerTool/security proof of concept:** independent CLI and bounded project sandbox config parsing, Docker engine selection, Linux/Windows mount/start/inspect/destroy and rollback. Demonstrate host-approved image/mount/privilege controls, both target-platform network enforcement paths (method/path, TCP, direct-egress tests) and workspace mount reachability **before** Agent API route integration. An unsupported target must fail closed, not claim success.
3. **SandboxExecutor + Agent API routing:** resolve the trusted allocation/claim and effective policy contracts first; persist optional immutable `SandboxId` on agent/chat and existing shell history as appropriate; add host-controlled target admission, container shell selection, attachment path handling, environment filtering, durable output/timeout/kill and restart behavior. Verify the same ordinary `<SH>` protocol against host and Docker without a second execution ledger.
4. **Jobs API foundation:** Git source preparation, SQLite job/container queue, capacity accounting, authenticated allocation/claim and CRUD, bounded startup reconciliation and terminal cleanup. Include integration calls to Agent API with read-back after uncertain creation; no second Agent execution state machine.
5. **Git workflow:** host-authorized job-scoped progress/comment/MR endpoints, minimal required-successful-command evaluation, `<ABORT>` support, verified Git-action post-processing, and cleanup/reporting after completion. Protected-source redaction/restore is a **separate acceptance gate** before enabling marked repositories.
6. **UI and end-to-end security validation:** Existing/Pending containers and jobs, status, Kill-then-Delete confirmation, queue release, both container OS families, concurrent requests, process termination and network escape attempts.

### Initial acceptance scenarios

- Personal trusted agent without `SandboxId` uses host Executor; restricted host rejects that same request.
- Sandbox-bound agent emits ordinary `<SH>`, never Docker syntax; Docker executes it in the correct assigned container.
- Caller cannot mutate an established `SandboxId` through prompt, project config or command environment; absent container never falls back to host. New executions/prompts cannot cross an already bound job chat into another container or host.
- Queue limit across mixed Linux/Windows and job sources holds under concurrent submissions; paused containers consume slots.
- Terminal execution causes Jobs API cleanup once, only after required job post-processing and **confirmed absence of active Agent-owned SH/start reservations**; container background services are stopped by Docker destroy, and the next pending request can begin only after confirmed destruction.
- Container DELETE while Running/Waiting/Paused returns conflict; UI Kill then Delete succeeds after terminal transition.
- Pending requests are not unexpectedly restarted after host restart; persisted state does not falsely imply recovery.
- Git credentials, original `.git` and protected source are not accessible in mounted workspace; path links cannot escape it, and project YAML cannot select unsafe mounts/images/privileges.
- HTTP method/path restriction and direct-egress prohibition are tested on both OS-specific runtime paths; Jobs API additionally rejects sandbox requests for another job even on an allowed endpoint.
- Pausing and killing docker-exec-launched shell process trees do not leave runaway child processes; shell history, recovery deduplication and ATTACH reference only the owning sandbox workspace.

## 13. Superseded proposals — do not accidentally revive

- **Not** a `GitTool` that directly owns only cloning while Agent API owns container CRUD and queues.
- **Not** container CRUD or FIFO queue inside `Mezhs.Agent.Api`.
- **Not** container queue, capacity tracking, persistent job state or blocking waiting loops inside `ContainerTool`.
- **Not** project YAML or an agent prompt selecting host execution via `executorType`.
- **Not** subclassing `Mezhs.Agent.Api` to create a `GitAgentApi` or separate worker per sandbox.
- **Not** a custom container command daemon/proxy/firewall when existing Docker exec and maintained proxy capabilities suffice.
- **Not** Agent API simultaneously destroying a job sandbox that Jobs API owns.
- **Not** extending the sandbox implementation into a full multi-agent orchestration rewrite before that separate plan is revisited.

**Guiding rule:** Jobs API manages the **work**, Agent API manages the **agent**, ContainerTool manages **containers**, and the selected Executor runs **commands**. Trust restrictions are enforced by the deployed host and authenticated assignment, not by editable project policy.

### Independent review disposition (2026-11-05)

**PASS WITH OPEN QUESTIONS — architecture direction retained.** Current `main` does not contain the new Jobs API, SandboxExecutor or ContainerTool, and no build/runtime Docker test was executed for this documentation review. Implement the small, *gated* ContainerTool/network feasibility stage first. The complete restricted-job implementation is **not** cleared until both-platform isolation, job-scoped authorization, policy delivery and termination/cleanup contracts are demonstrated. The owner must reconcile the Pause/Stopped vocabulary with the separate orchestration plan before shared Agent lifecycle changes; do not silently select one in code. Git protection-marker support remains disabled until its separate restoration proof succeeds.
