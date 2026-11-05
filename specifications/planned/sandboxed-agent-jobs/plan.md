# MEŽS sandboxed agent jobs — architecture handoff

**ID:** `sandboxed-agent-jobs`  
**Level:** High-risk (architecture, isolation/security, container lifecycle and concurrency).  
**Status:** Architecture agreed on 2026-11-04; planned on `main`, **not independently reviewed or implemented**.
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

## 2. End-to-end workflow

1. A webhook, GitHub/GitLab integration, Jira integration or UI caller submits a **job** to Jobs API.
2. Jobs API records the job, authenticates and interprets the request, clones the requested revision into a job-owned host directory and reads the project's configuration files.
3. Jobs API prepares a **sanitized writable workspace** (no mounted `.git` directory, Git credentials or protected original source). It retains the original checkout separately, outside container reach.
4. Jobs API adds the container request to its **single FIFO capacity queue**. All managed containers use the same capacity rule irrespective of origin; there is no `IsManual` special case.
5. When a slot is free, Jobs API calls ContainerTool CLI with the project sandbox configuration path and the host workspace directory. ContainerTool returns an opaque `SandboxId` or sufficient container metadata for that ID to be resolved.
6. Jobs API calls the regular Agent API creation endpoint with the task, authorized agent policy/configuration and **`SandboxId`**. Jobs API never needs an Agent API subclass. Agent API persists the assignment, and its runtime routes SH commands to SandboxExecutor.
7. The agent operates normally: it generates regular `<SH>` commands, never `docker exec`. SandboxExecutor calls Docker externally, using the assigned container, appropriate working directory and platform shell. The agent sees the available tools/files but does not choose whether execution is sandboxed.
8. During execution, the agent may call narrow, explicitly allowed Jobs API endpoints to report progress, comment on a branch and request Git operations such as creating a merge request. Jobs API holds Git credentials and handles these operations.
9. Jobs API **observes Agent API execution status**; Agent API remains authoritative for it. After terminal status, Jobs API performs any required final processing, asks ContainerTool to destroy the sandbox/proxy/network, and finally deletes its job-owned workspace. It releases capacity and starts the next pending job. Report the final job outcome to the original caller.

Jobs API is the **cleanup owner** for managed jobs (this expressly replaces the earlier proposed Agent API cleanup ownership). Agent API does **not** independently destroy the same sandbox on DONE/ABORT: there must be one cleanup owner, not two races. For containers not associated with a running execution, Jobs API's CRUD path can delete them under the same usage-state rules.

If startup fails after a partial container create, the owner initiates cleanup (`try/catch` or equivalent), and ContainerTool's destroy/rollback command must tolerate partially created resources. No elaborate recovery subsystem was agreed for V1.

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
- Persist the sandbox assignment with the agent execution at creation. It is immutable throughout that execution. New user prompts within the same execution do not change target.
- No user-controlled policy, prompt, shell text or ordinary environment variable may override the target. Validate the requested target under the API's authorization/trust boundary; sandbox IDs are resource identifiers, not authentication secrets.
- Sandbox IDs must resolve to the correct Docker **engine/context and container**, not merely assume all engine-local IDs share one namespace. The exact resolution representation is an implementation detail.
- Manual/host agents can continue to operate without Jobs API. A manually initiated sandbox agent needs an already assigned sandbox from the managed container API/tool path.

### Existing environment mechanism

MEŽS already sets execution context environment variables in `Mezhs.Agent.Api/Commands/Shell.cs`, with definitions in `Mezhs.Console/MezhsExecutionContext.cs` and persistence/launch in `Mezhs.Executor`. Reuse this pattern for **trusted routing context**:

~~~text
MEZHS_SANDBOX_ID=<persisted SandboxId>
~~~

Agent API constructs the value from the execution record, not from submitted shell text or repository YAML. SandboxExecutor receives it as the **host-side process environment**. It must not require exposing the ID or Docker socket to processes running inside the container.

Avoid forwarding a complete Agent API/host environment into the container. Pass the intended sandbox command environment only. The existing host executor snapshots an environment; this is not by itself a contract to forward all those variables through `docker exec`.

### CLI / shell semantics

`<SH>` remains the same agent protocol regardless of target. SandboxExecutor translates it internally to `docker exec` (or platform equivalent). Docker does not require an SSH/HTTP command agent inside the container.

New SH commands get new shell processes in the **same** container and workspace. Persistent files and independently running services remain; transient shell-local variables and `cd` do not automatically carry over between SH blocks.

Reuse as much as practical of the existing executor's durable execution IDs/history, result capture, timeout, kill and restart semantics. Do not assume simply killing the local `docker exec` client reliably kills the process inside the container. Container-aware **process-tree interruption** and output/exit-code propagation need explicit tests. Do not introduce a custom shell daemon unless a verified limitation requires one.

The current repository's `ExecutorService` is sealed and used directly by various Agent API services. Implementation must inspect these references and add a small, shared routing boundary rather than promise a drop-in type swap that the code does not presently support.

## 4. Configuration boundaries

Two **project-owned** files are planned:

| Location | Purpose | Trust |
| --- | --- | --- |
| `.mezhs/agent.yaml` | Agent instructions, model defaults, command policy, required-successful-command rules and project task behavior | Read from project/job, **not authorized** to request host execution |
| `.mezhs/sandbox.yaml` | Desired OS/image, resources, workspace/container mount shape, allowed outbound HTTP endpoint and TCP destinations | Project request, subject to enforced host infrastructure restrictions |

And a **host-owned** configuration (e.g. `containertool.yaml` and host/Agent API settings) controls actual engines/contexts, proxy implementation, capacity, deployment permissions and whether host execution is enabled. Do not give a project file authority over host privilege or the Docker socket.

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

**Open issue:** Agent API currently reads host-side `agent.yaml` and central policies. How Jobs API supplies project-specific policy values or a snapshot to one execution, how those values are validated, and how they behave on restart **must be designed before Agent API integration**. Do not silently choose live repo YAML, a new global policy, or a new trust mechanism.

Existing `MEZHS_*` runtime environment names are reserved by the Agent API for its internal context.

## 5. Scheduling and container management

**Jobs API**, not ContainerTool or Agent API, owns the SQLite queue and concurrency limit.

- One shared capacity rule for every managed job container; no manual-vs-automated distinction. Proxy sidecars are implementation resources of the same sandbox, **not additional job slots**.
- The counted resource is an **allocated sandbox/container**, including one retained by a paused execution; do **not** count SH invocations as separate container slots.
- An incoming request becomes Pending; Jobs API queues it FIFO. Starting reserves a slot atomically so competing requests cannot overbook capacity.
- Once a container is successfully allocated, it is visible as an existing/created resource. Container creation error releases the reservation and does not block the entire queue.
- A destroyed container releases capacity and triggers admission of the next pending request. Do not free the slot before destruction finishes.
- On a **host/application restart**, pending requests **need not be resumed**; they may be marked Cancelled/Abandoned. The queue is stored in SQLite for status and consistency during normal operation, **not a promise of post-restart job recovery**. The exact treatment of already running/orphaned containers after a restart remains to be specified.
- Queue processing is initiated by job submission and container slot release under Jobs API's orchestration. No blocking `while (...) Thread.Sleep(...)` in a simple CLI, and no additional standalone ContainerTool scheduler/service.

ContainerTool remains a straightforward CLI operating on a specified target/engine with no knowledge of Jobs API's queue.

### UI / API

The Jobs/Containers area should expose at least **Existing** and **Pending** tabs, current status and a means to cancel pending requests, inspect existing containers and request deletion. It can appear as new tabs in the Agent UI, backed by Jobs API rather than duplicated Agent API CRUD.

Deleting an existing container must be rejected with a conflict response when an associated execution is **active**. The UI may present a Yes/No confirmation; on approval it invokes the execution's Kill/terminal transition, waits for it to be non-active, then calls Delete. Do not create a secret force-delete bypass in the public API.

**Derived predicate, not extra authoritative persisted boolean:**

~~~csharp
bool IsActive => Status is Running or Waiting or Paused;
~~~

Here `Stopped`, `Completed`, `Failed/Errored` and `Cancelled` are **non-active/terminal** for deletion purposes. Exact enum names/status mapping need agreement with the existing agent runtime.

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

The existing `plan/multi-agent-orchestration` document currently uses **Stopped** for a *resumable* state. That terminology conflicts with the provisional table above. **Do not silently change the orchestration plan or implement both meanings**; reconcile in its own design/implementation session.

Desired behaviors:
- **Pause:** interrupt the current SH process tree, keep sandbox and workspace, and allow additional prompts while paused. No new commands run until Resume.
- **Resume:** continue under the same sandbox with updated prompts/context, following the eventual canonical Agent runtime semantics.
- **Kill:** terminate execution permanently, enabling Jobs API cleanup; not an alias for Pause.
- **`<DONE>`:** successful agent completion after policy requirements.
- **`<ABORT>`:** a new standalone termination marker; ends the agent as Failed immediately, regardless of required-successful-command policy. When returned together with pending executable blocks, do not run them.

Existing `requiredSuccessfulCommands` supports command names. Plan a minimal extension for exact SH request text, or prefix matching with a trailing `*`, so a policy can require successful calls to specific Jobs API actions before DONE. Avoid a second "required outcomes" framework, extra rejection counters, or automatic retry/loop systems. Runtime instructions must list missing required commands plainly on rejected DONE, with the ABORT escape route when completion is impossible. For HTTP shell calls, ensure failure status produces nonzero command exit (e.g. `curl --fail-with-body`).

Jobs API polls/reads the Agent API's persisted execution status; callbacks are not required. Jobs API separately owns reporting back to the original Git/Jira/UI caller. Agent API is not expected to understand which trigger initiated the task.

## 7. Filesystem and Git trust boundary

Jobs API owns both the source checkout and the cleanup of the complete job directory. Example:

~~~text
jobs/job-482/
  repository/      # Original clone incl. .git and protected source; NOT mounted
  workspace/       # Sanitized writable copy; mounted read/write in sandbox
~~~

Git credentials, git push/commit/MR creation and source-system authorization remain in Jobs API, not in the sandbox or Agent API. The agent reports progress and requests permitted Git actions via Jobs API; it does not use Git credentials.

The originally proposed `//<SecurityCritical> ... //</SecurityCritical>` redaction mechanism may strip protected sections from the sandbox copy. Exact transformation/restoration and merging algorithm is **unresolved** and security-sensitive: do not commit redacted placeholders over original protected code or allow agent output to overwrite preserved protected segments. The full original checkout stays outside the mounted workspace for safe reconstruction and diff application.

Mount only paths needed for the job. Ensure configured Windows/Linux Docker engines can actually mount the job workspace on the host where Jobs API prepared it; this constraint must be validated, especially for distinct engine hosts.

Cleanup ordering: finish required Git operations/readbacks; stop/destroy container and proxy, detach mount; then delete the Jobs API-owned job directory. Failure and cancellation must release resources without inventing a complicated retry framework.

## 8. Network boundary and deployment security

Sandboxed code is **untrusted execution**, even when its prompt or project YAML appears benign.

The previously agreed intent is a per-sandbox network isolation layer with **explicit allowed HTTP methods/paths (and, when needed, query constraints)** for limited Jobs API operations, plus separately authorized TCP destinations for database access. A simple hostname/port allowlist is insufficient for Git/Jobs API endpoints. Use existing NGINX/Envoy (or equivalent maintained component), **not a custom application firewall**. Earlier discussion preferred one proxy companion per sandbox to avoid central mutable rule ownership.

**Implementation validation still required:**
- Linux and Windows workloads must be supported **concurrently**, potentially by different Docker engines/contexts. Do not assume one Docker Desktop engine automatically runs both container types simultaneously.
- A Linux proxy image is not automatically usable as a Windows sidecar. Specify a compatible per-platform enforcement approach and demonstrate method/path rules and TCP allowlisting.
- A workload container must not bypass the proxy via direct egress, host gateway, Docker daemon/socket, other container network, metadata endpoint, etc. Docker `--internal` alone must not be assumed sufficient without testing.
- HTTP inspection requires controlled forwarding/TLS termination. An arbitrary HTTPS CONNECT tunnel cannot satisfy endpoint-level rules.
- The Docker/container control plane, host directories and Agent API must be reachable only by authorized processes/roles. The work host may additionally enforce no-host-execution by OS permissions.
- No credentials or unrelated host environment variables should leak into container processes through snapshot propagation.

The organization security team owns deployment-level network and permission governance. Do not add speculative preflight cross-validation of every required SH command against the proxy YAML; runtime enforcement and focused validation tests are the agreed approach.

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
- `src/Mezhs.Agent.Api/Program.cs` composes existing MEŽS API services and registers `ExecutorService`.
- `src/Mezhs.Agent.Api/Commands/Shell.cs` directly calls `ExecutorService.Execute` / `WaitAsync` and constructs execution environment.
- `src/Mezhs.Executor/ExecutorEnvironment.cs` snapshots environment and `ExecutorRunner.cs` restores it into child shell processes.
- `src/Mezhs.Console/MezhsExecutionContext.cs` defines current `MEZHS_*` execution variables.
- `src/Mezhs.Agent.Api/Services/AgentService.cs` currently selects host-registered policies and rejects caller-supplied `MEZHS_*` names.
- Other Agent API services reference `ExecutorService` directly: worker/recovery, policy completion, logs, endpoints. Audit these when introducing executor routing.
- `README.md` warns that existing Agent API host SH is intentionally not sandboxed and intended for loopback trusted use.
- The separate multi-agent plan proposes canonical `POST /v1/agents` and expanded state management, but those are **future** contracts. Avoid prematurely changing that plan's behavior as a side effect of this work.

## 11. Deliberately unresolved before implementation

1. **Project agent policy delivery:** How Jobs API passes a per-job `.mezhs/agent.yaml` or validated policy snapshot into Agent API without granting a repo permission to elevate execution target; persistence and restart semantics.
2. **Container creation vs. agent start API (former Q32):** Keep distinct container CRUD and agent start operations. The automated Jobs API workflow requires both, but exact public API/transaction boundaries for independently created containers are not finalized.
3. **Sandbox identity and executor lifecycle:** Opaque ID resolution across engines; sharing existing Executor's persistence; timeout, pause/kill process-tree behavior and Docker exec cancellation.
4. **Linux/Windows networking enforcement:** Platform-compatible proxy, method/path filters, TCP policy, egress bypass tests and mounted-workspace reachability.
5. **Status alignment with multi-agent orchestration:** `Paused` vs existing plan's `Stopped` and root/descendant lifecycle; defer detailed multi-agent design.
6. **Sanitization safety:** Precise `SecurityCritical` removal/restore/commit protocol.
7. **Crash/orphan handling:** Pending queue does not resume after restart by decision; treatment of containers that still exist and jobs that were active when Jobs API stopped needs a small explicit rule.
8. **Job-facing action/API contracts and UI:** Auth scopes and exact Jobs API endpoint/DTO/UI shapes, including which Git operations must happen synchronously before DONE.

Do **not** treat these as permission to invent new subsystems. Resolve them individually as the relevant implementation stage begins.

## 12. Implementation sequence and verification gates

1. **Architecture handoff (this branch):** review and confirm service boundaries, obsolete decisions, unresolved contracts. No implementation or merge without explicit approval.
2. **ContainerTool proof of concept:** independent CLI and project sandbox config parsing, Docker engine selection, Linux/Windows mount/start/inspect/destroy, resource cleanup and initial network enforcement probes. Test under actual target engines.
3. **SandboxExecutor + Agent API routing:** trusted optional `SandboxId` in creation/execution persistence, host-controlled target admission, command routing, environment filtering, durable output/timeout/kill behavior, fail-closed restricted deployment. Verify identical ordinary `<SH>` agent protocol against host and Docker.
4. **Jobs API foundation:** Git source preparation, SQLite job/container queue, capacity accounting, basic CRUD and terminal cleanup. Include integration calls to Agent API and the project-policy contract resolved beforehand.
5. **Git workflow:** allowed progress/comment/MR endpoints, required-successful-command evaluation, `<ABORT>` support, protected-code handling and cleanup/reporting after completion.
6. **UI and end-to-end security validation:** Existing/Pending containers and jobs, status, Kill-then-Delete confirmation, queue release, both container OS families, concurrent requests, process termination and network escape attempts.

### Initial acceptance scenarios

- Personal trusted agent without `SandboxId` uses host Executor; restricted host rejects that same request.
- Sandbox-bound agent emits ordinary `<SH>`, never Docker syntax; Docker executes it in the correct assigned container.
- Caller cannot mutate an established `SandboxId` through prompt, project config or command environment; absent container never falls back to host.
- Queue limit across mixed Linux/Windows and job sources holds under concurrent submissions; paused containers consume slots.
- Terminal execution causes Jobs API cleanup once, only after required job post-processing; next pending request can begin.
- Container DELETE while Running/Waiting/Paused returns conflict; UI Kill then Delete succeeds after terminal transition.
- Pending requests are not unexpectedly restarted after host restart; persisted state does not falsely imply recovery.
- Git credentials, original `.git` and protected source are not accessible in mounted workspace.
- HTTP method/path restriction and direct-egress prohibition are tested on both OS-specific runtime paths.
- Pausing and killing docker-exec-launched shell process trees do not leave runaway child processes.

## 13. Superseded proposals — do not accidentally revive

- **Not** a `GitTool` that directly owns only cloning while Agent API owns container CRUD and queues.
- **Not** container CRUD or FIFO queue inside `Mezhs.Agent.Api`.
- **Not** container queue, capacity tracking, persistent job state or blocking waiting loops inside `ContainerTool`.
- **Not** project YAML or an agent prompt selecting host execution via `executorType`.
- **Not** subclassing `Mezhs.Agent.Api` to create a `GitAgentApi` or separate worker per sandbox.
- **Not** a custom container command daemon/proxy/firewall when existing Docker exec and maintained proxy capabilities suffice.
- **Not** Agent API simultaneously destroying a job sandbox that Jobs API owns.
- **Not** extending the sandbox implementation into a full multi-agent orchestration rewrite before that separate plan is revisited.

**Guiding rule:** Jobs API manages the **work**, Agent API manages the **agent**, ContainerTool manages **containers**, and the selected Executor runs **commands**. Trust restrictions are enforced by the deployed host and execution assignment, not by editable project policy.
