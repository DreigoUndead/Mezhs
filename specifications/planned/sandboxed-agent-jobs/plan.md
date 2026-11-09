# MEŽS sandboxed agent jobs — V1 architecture

**ID:** `sandboxed-agent-jobs`  
**Level:** High-risk — architecture, container/network boundary, Git credentials and concurrency.  
**Status:** Reconciled with owner during Grill Me, 2026-11-05; **not implemented or independently reviewed in this revision**.  
**Repository:** `DreigoUndead/Mezhs`  
**Location:** `specifications/planned/sandboxed-agent-jobs/plan.md` on `main`.  
**Related plan:** `docs/plans/multi-agent-orchestration.md` on `plan/multi-agent-orchestration` is separate and unimplemented. Do not implement or edit it under this task.

This is the V1 decision record, not a discussion transcript. Examples below illustrate a contract without freezing every DTO or YAML property name. A separate High-risk plan reviewer must inspect it against existing code before development; **review findings must be discussed with the owner before any subsequent plan edits**. A plan approval is not implementation or merge approval.

## Goal and scope

Jobs API accepts external requests to run a MEŽS agent against an exact Git revision inside a disposable local Docker sandbox. The agent reviews code and posts **one Markdown summary comment** on an existing GitHub pull request or GitLab merge request. Jobs API exposes status, scheduling and container management to the UI and callers. Trusted local MEŽS agents can continue using the host Executor when permitted by host configuration.

**Included in V1:**
- GitHub and GitLab, with existing PR/MR review comments (including a no-findings comment); no inline comments.
- Request-driven jobs from GitHub Actions, GitLab CI, another authorized-by-network caller, or the UI. The caller decides when to request a review; Jobs API does not watch branches or schedule a review on each push.
- Request identifies repository URL, **exact commit SHA**, existing PR/MR, and task. The requested SHA must be checked out even if the branch advances while queued.
- Project policy determines what the agent reviews (entire checkout, specific modules, changed code, etc.). No compulsory generated diff or additional review-diff API.
- Jobs/Containers management UI with Existing and Pending tabs, statuses, inspection, cancellation and deletion. The UI is part of V1 testing.
- Submission returns a `jobId`; callers/UI query Jobs API for status and result. No completion callbacks or GitHub/GitLab status/check updates.

**Not in V1:** source modification for submission, restoring redacted code on commit, commit/push, creating PRs/MRs, Jira, inline review comments, automatic webhook/commit triggers, GitHub App installations or credential UI, multi-agent orchestration, true resumable pause, remote Docker engines, simultaneous Linux/Windows containers on one installation, distributed services, or a generic recovery framework. Leave room to add these later without implementing them now.

## 1. Ownership and process boundaries

A **job** is the requested work and its queue/cleanup lifecycle. An **agent execution** is the AI task, conversation, commands and status. A **sandbox** is the Docker environment allocated for the job. Do not conflate their IDs or state ownership.

| Component | Authoritative responsibilities | Not responsible for |
| --- | --- | --- |
| `Mezhs.Jobs.Api` | Job submission/status, Git provider adapter and credentials, clone and workspace preparation, SQLite job/queue state and capacity, sandbox CRUD, Agent API status polling, reporting and final cleanup | Agent reasoning, conversations, shell execution |
| `Mezhs.Agent.Api` | Agent creation/execution, policy resolution and snapshot, immutable `SandboxId`, existing execution lifecycle/recovery, SH routing | Git access, jobs queue, container CRUD/cleanup |
| `Mezhs.ContainerTool` CLI | Create/start/inspect/stop/destroy Docker container and associated **host-managed per-sandbox NGINX proxy** using the configured local engine | Job queue/scheduling, agent state, HTTP CRUD service |
| Existing `Mezhs.Executor` shared base | Durable command state/history, output, timeouts, cancellation and restart semantics | Git and container scheduling |
| Derived `HostExecutor` / `SandboxExecutor` | Local shell launch / Docker `exec` launch in immutable assigned container | Separate duplicated execution persistence |
| MEŽS UI | Agent and Jobs/Containers views backed by respective APIs | Independent queue/container state |

Jobs API is an **active API service**, not a Git-only helper or Agent API subclass. It alone owns job/container CRUD and scheduling. ContainerTool is a small CLI, not another scheduler. Agent API alone owns agent status.

For V1, **Jobs API and Agent API run as separate processes on the same trusted machine and communicate over loopback**, using a normal typed API client. No remote Agent API transport or additional distributed authentication is required. Agent API remains usable without Jobs API for manual trusted host agents.

### Sources of truth

- Jobs API SQLite: jobs, pending queue, allocated capacity, sandbox/workspace association, status/result reporting.
- Agent API persistence: chats, execution records, immutable sandbox assignment and policy snapshot.
- Docker runtime: actual container processes, not queue state.
- Protected original Git checkout: exact repository revision, including `.git`, managed by Jobs API outside the normal sandbox mount.

Do not duplicate job state in Agent API, container CRUD in Agent API, or command history in SandboxExecutor.

## 2. V1 workflow

1. Caller submits repository URL, exact SHA, PR/MR identifier and review task to Jobs API. It returns `jobId`. **Jobs API V1 has no API key, OAuth or repository allowlist**: caller access is governed by the deployment network. GitHub Actions/GitLab CI decide when to invoke it.
2. Jobs API resolves any Git token from the current host `jobs.yaml` when cloning and checks out the **exact requested revision**. A repository with no configured token may be cloned anonymously; fail clearly if access requires credentials.
3. Jobs API requires `.mezhs/agent.yaml` from the requested revision and `.mezhs/sandbox.yaml` from the source selected by host `jobs.yaml` (requested revision by default, or a configured trusted repository ref). Missing/invalid selected files fail the job; a failed trusted-ref lookup must never fall back to the PR version. It retains the original checkout, prepares a separate writable workspace without `.git`, and replaces `SecurityCritical` regions with unique placeholders before creating any container. Bad markers fail the job.
4. Jobs API enqueues the sandbox allocation under one shared FIFO capacity limit. When space is available, it calls ContainerTool with project Docker/network settings and the sanitized workspace. ContainerTool uses the configured **local** Docker engine and manages an associated **host-side** NGINX proxy.
5. Jobs API starts the agent through the **normal Agent API creation endpoint**, passing task, immutable `SandboxId` and the **absolute host path** to the cloned project's `.mezhs/agent.yaml` as the existing `policyId` parameter. Agent API resolves a registered policy name or a file path, loads policy once and persists the effective policy snapshot for the execution.
6. SandboxExecutor runs normal `<SH>` commands in the container. The agent inspects the prepared checkout as its project policy instructs. The agent does not receive Docker/Git credentials or the original checkout via the standard workspace preparation.
7. The project policy requires a successful SH call to the **job-scoped Jobs API comment endpoint** before `<DONE>`. Jobs API simply posts the summary to the already existing GitHub PR or GitLab MR and reports success/error; it does not judge review completeness. If the agent cannot satisfy its policy, `<ABORT>` ends it as failed.
8. Jobs API queries Agent API status. **If Agent API classifies the execution as stopped/terminal**, Jobs API stops/destroys the container with remaining processes, destroys its proxy, releases its mount and job directory, and frees queue capacity. Otherwise the sandbox remains allocated. Jobs API provides current status/result through its query API.

Jobs API is the **single cleanup owner** of managed sandboxes. Ordinary failures throw, mark job failed, and run regular exception handling and cleanup. Do not add cross-service transactions, special creation-acknowledgement protocols or bespoke orchestration recovery for every error scenario.

## 3. Agent policy, routing and Executor

### Execution assignment

Extend the ordinary Agent API creation request with optional `sandboxId`; no separate `/v1/sandboxed-agents` API. Illustrative payload (not frozen):

~~~json
{
  "input": "Review this revision and comment on the existing PR/MR",
  "policyId": "/jobs/482/repository/.mezhs/agent.yaml",
  "sandboxId": "sandbox-482"
}
~~~

- `sandboxId != null`: route **all** SH commands of that execution to SandboxExecutor and the assigned container.
- `sandboxId == null`: use existing host Executor **only if trusted Agent API host settings permit it**. A restricted deployment rejects missing/unusable sandbox assignment rather than falling back to host. Personal deployment can allow either route.
- **`ATTACH` is prohibited for sandboxed executions (`sandboxId != null`) in V1.** Enforce the rejection in Agent API command dispatch regardless of project policy allowances, not solely through generated model instructions: the existing `ATTACH` implementation resolves file paths and imports them on the Agent API host and would bypass the sandbox. Leave `ATTACH` unchanged for ordinary trusted local agents under this task; its future removal/replacement is separate work.
- Sandbox assignment is immutable for the execution. Policy, prompt, project YAML or user-provided command environment cannot change the execution target. An opaque sandbox ID resolves to a container on the configured local Docker engine; it is not an authentication secret.
- Existing `MEZHS_*` execution context may convey a host-side `MEZHS_SANDBOX_ID` generated from persisted state; user input cannot set reserved routing variables. Never blindly forward host environment snapshots, credentials or internal API context into Docker.
- Normal/host agents work without Jobs API; Jobs API's project `agent.yaml` is not allowed to select host execution.

### File-path policy

The existing `policyId` parameter supports registered policies today. Extend resolution so it can also point to a project policy file in Jobs API's host checkout. **Read once when starting an execution; persist and use that exact effective policy throughout execution and recovery.** New executions can read modified files. No watcher/hot-reload, second policy submission API or new global policy registration per job.

### Inherit the existing Executor

Refactor the current sealed `ExecutorService` and `ExecutorRunner` only where needed into a shared base plus host-specific and sandbox-specific launch behavior (inheritance or narrowly factored base). Reuse the existing durable command IDs/history, command deduplication, outputs, exit status, timeout, kill and recovery. Do not create a second shell store or a new standalone container command daemon. Inspect the existing Agent API direct references and runtime launcher while implementing.

The agent still emits plain `<SH>`, never `docker exec`. SandboxExecutor translates to Docker `exec` using the assigned container and correct platform shell. Each command has a new shell process in the same persistent filesystem; shell-local `cd` and variables do not automatically carry over. Correct exit/output propagation and individual command timeout handling require platform tests: killing the host-side `docker exec` client does not necessarily kill the command inside Docker.

### Existing status behavior; new completion marker

**Do not implement a new resumable Pause/Resume lifecycle.** Use the existing Agent API pause/cancellation and normal recovery semantics for sandboxed agents. Jobs API uses the existing Agent API status/enum classification: **stopped/terminal ⇒ kill remaining sandbox processes and destroy all job-owned sandbox resources; non-stopped ⇒ keep the sandbox**. No duplicate status enum, force-delete lifecycle or multi-agent `Stopped` terminology change. The future multi-agent plan retains its own decisions.

- `<DONE>`: policy requirements must be fulfilled.
- `<ABORT>`: new standalone immediate unsuccessful termination, ignoring `requiredSuccessfulCommands`. If present with pending executable blocks, do not execute those blocks.
- Extend existing `requiredSuccessfulCommands` minimally beyond command names to **exact SH text or prefix match ending in `*`**, so the policy can demand a successful call to the job's comment endpoint. Do not add a separate `<JOB_COMMENT>` command or "review outcome" state machine.
- HTTP errors must yield a nonzero SH exit code (e.g. `curl --fail-with-body`). Failed calls cannot satisfy the successful-command requirement. Rejected `<DONE>` explains missing commands; `<ABORT>` remains an escape.
- A command timeout during a still-active agent follows the existing Executor behavior; it is **not** automatically a terminal job unless Agent API's status says so. Test Docker-specific cancellation behavior, then stop the entire container on terminal cleanup.

## 4. Configuration and credential ownership

### Required project configuration

Both files are **mandatory in their selected Git revision**; missing or malformed files fail job preparation, with no project defaults. The agent policy comes from the requested review SHA; the sandbox configuration comes from the source selected by host `jobs.yaml`:

| File | Meaning |
| --- | --- |
| `.mezhs/agent.yaml` | Instructions, model/policy, review scope, allowed commands, required successful Jobs comment call |
| `.mezhs/sandbox.yaml` | OS/image, resources, Docker options/mounts, HTTP endpoint/method/query permissions and TCP destinations |

Developers may modify either file in the reviewed branch itself. For a repository using a trusted sandbox-config ref in `jobs.yaml`, a proposed change to `.mezhs/sandbox.yaml` in the reviewed PR/MR does **not** affect that job; Jobs API reads the sandbox config from the trusted ref instead. Outside this configured source-selection rule, project YAML may configure Docker privileges, networking, mounts and destinations; **MEŽS must not add approval layers or arbitrary developer-facing restrictions**. DevOps governs host, Docker daemon permissions and wider network reachability. This remains an accepted trust model, not a guarantee that Docker settings preserve sandbox isolation.

**Host versus sandbox is the one separate trusted routing choice:** whether Agent API may execute host commands is decided by host-owned Agent API settings, not project YAML.

### Host-owned `jobs.yaml`

Use one Jobs API server-side `jobs.yaml` for Git credentials, trusted sandbox-config source selection, and Jobs API/container/proxy/queue settings as applicable. Do not introduce a separate `containertool.yaml` in V1 without an actual need. It is outside cloned repositories and **not part of the normal container mount**.

**Trusted sandbox-config selection (approved Grill Me decision):** `jobs.yaml` can opt a repository into reading `.mezhs/sandbox.yaml` from a host-configured trusted ref of the **target repository**, rather than from the reviewed PR/MR SHA. The configured ref must be protected by repository administration. Jobs API resolves that ref to an exact SHA **when accepting the job** and persists it for that job; branch movement while queued cannot change the selected config. Do not take the ref, repository identity or source mode from PR content or caller-supplied overrides. Apply the repository setting to all its jobs rather than trying to infer author trust from PR metadata. If a configured ref or its sandbox file cannot be read, fail the job; **do not fall back to the PR's config**. Repositories without this opt-in retain the prior requested-revision behavior and are suitable only for trusted-source jobs. This is source selection, not a new approval service or general Git push restriction. External-contributor jobs should use the trusted-ref mode. A trusted sandbox config that deliberately enables privileged access still does **not** make execution of untrusted code safe; DevOps must select appropriate sandbox privileges for such repositories.

GitHub and GitLab use **configured access tokens** in V1, not GitHub App installations or provider-native token renewal. Support both Git-server/host defaults and repository overrides; resolution priority:

1. Exact repository-specific token.
2. Matching Git-server/host default.
3. Anonymous Git operation when none is configured.

Tokens may be literal in protected host config or references to environment variables. Do not leak tokens through command output, container environment or unrelated Git hosts.

**Lazy lookup:** Each Git operation needing a token **reads `jobs.yaml` at that moment**. No cached config object, file watcher or reload logic. Changing a YAML token affects the next clone/comment call; environment-variable changes require the running process to receive the updated value, usually on restart. Invalid YAML makes that operation fail clearly; never silently reuse an older cached version.

Illustrative config (syntax still provisional):

~~~yaml
git:
  github:
    - host: github.com
      tokenEnv: GITHUB_TOKEN
    - repository: company/private-project
      tokenEnv: PRIVATE_PROJECT_TOKEN
  gitlab:
    - host: gitlab.example.org
      tokenEnv: GITLAB_TOKEN
projectConfiguration:
  repositories:
    - repository: company/private-project
      sandbox:
        source: trustedRef
        ref: refs/heads/main
# Other repositories: sandbox config from requested revision (trusted sources only).
~~~

Jobs API accepts **arbitrary repository URLs**, not just names in `jobs.yaml`. The token resolver must only use matching host/repository credentials; never send a credential to an unrelated server. An anonymous clone/comment may fail if authorization is required.

**Caller admission is distinct from Git credentials.** Jobs API has **no V1 caller authentication**: any entity that can reach its endpoints can submit/manage jobs and potentially invoke actions backed by configured Git credentials. Network-level access restrictions and appropriate deployment topology are DevOps responsibilities and must be explicitly documented. Do not silently add API keys/OAuth or a repository allowlist.

## 5. Git workspace, protected code and review comments

Jobs API owns the entire job directory and original clone. The default layout is:

~~~text
jobs/job-482/
  repository/     # Exact original checkout, .git and protected source; host only
  workspace/      # Sanitized copy, mounted read/write into sandbox
~~~

Never intentionally include `.git`, Git credentials or protected original code in the **default** sandbox workspace. The agent can inspect, test, compile and modify scratch/workspace files, but no V1 code modifications are committed or pushed.

**Redaction before Docker creation:** Recognize `//<SecurityCritical>` through `//</SecurityCritical>` and replace each full region with a unique single comment, e.g. `//secure-code#a81f2`. Keep originals in Jobs API's checkout. Missing start/end, unexpected closing or nested markers fail the job with a useful file/location error. Do **not** create stubs or require sanitized code to compile; the agent reports compilation limitations. Reconstructing original code for future code edits/commits is **future scope**, not V1.

Project policy dictates review scope and method. Do not force a Git diff workflow: the agent receives a working source tree without `.git`; its policy must be written accordingly. No synthetic diff required.

The sandbox's comment call is restricted by its generated NGINX rules to the owning job. Jobs API resolves repository/PR/MR target from the **job record**, not from arbitrary destination fields in the agent's comment body. Jobs API simply attempts to post one summary comment to the existing GitHub PR/GitLab MR. Policy enforces a successful comment operation before `<DONE>`; Jobs API does not add independent outcome verification. **If the Git provider request times out, fails, or returns an ambiguous response, treat it as an ordinary error**: report failure through the existing command/job error path, without automatic retry, duplicate detection, reconciliation, or a special delivery status. The external provider may have accepted a comment despite the reported error; V1 does not guarantee exactly-once delivery in this failure case.

Cleanup sequence: finish any outstanding Git/comment operation that requires job state; stop and destroy container/proxy and release mounts; then delete the Jobs API job directory and free capacity. No separate container file export pipeline.

## 6. NGINX and deployment trust boundary

Provide **one NGINX instance/configuration per sandbox, running on the host**, managed by ContainerTool. **Not** a Linux-only Docker sidecar and **not** a new custom firewall service. Jobs API generates job-bound proxy permissions from project `sandbox.yaml` and trusted job ID.

- HTTP permissions can specify exact **method, path and query**, e.g. allow only `POST /Git/Comment?id=<jobId>` for that sandbox's own job. An NGINX `location` path match does not automatically validate query arguments; implement explicit matching for job ID, duplicates/encoding and unintended parameter variations.
- Support developer-requested TCP destinations such as database endpoints, and allowed HTTP targets. The developer can change those as needed; MEŽS does not build a second network-access approval list.
- Jobs API supplies the job identity and recorded comment target. The sandbox cannot select a different job just by submitting a new ID/body parameter **through normally enforced per-sandbox proxy rules**.
- Test method/path/query filtering, TCP forwarding, ordinary proxy routing and attempts to reach Jobs API directly or through another sandbox in normal isolated configurations. Arbitrary HTTPS CONNECT tunnels cannot provide path-level filtering without controlled termination.

**Explicit accepted limitation and owner decision:** Docker network isolation and enforcement of proxy-only traffic are **entirely DevOps' responsibility**, including ordinary (non-privileged) containers. MEŽS configures its per-sandbox NGINX rules but does **not** implement or guarantee firewall/egress enforcement. DevOps must prevent direct container access to protected destinations when the deployment requires it; configuring NGINX alone does not block direct Docker bridge-network connections. Developers may request Docker `privileged`, host networking, host mounts or Docker socket access, which can bypass/defeat NGINX isolation or expose the host. **MEŽS does not reject such Docker configurations**. DevOps owns Docker daemon access, host privileges, firewall rules and network segmentation. A firewall on network traffic alone does not prevent misuse of a privileged Docker socket. Document deployment requirements and verify normal supported configurations without adding unapproved safety subsystems.

Agent API is internal to the trusted same-host setup; Jobs API exposure is explicitly a network/deployment decision. Do not assume private networking supplies per-user authorization—none is implemented in V1.

## 7. Local Docker, scheduling and UI

V1 supports the **configured local Docker engine only**. ContainerTool does not connect to remote engines, orchestrate engine provisioning or automatically switch Docker Desktop Linux/Windows modes. Use the image/OS supported by the active local engine; an incompatible requested image fails clearly.

Test Linux and Windows configurations **separately** on appropriate installations; **simultaneous Linux and Windows execution is not a V1 requirement**. The host NGINX proxy choice is intended to avoid platform-specific sidecar images, but actual host/guest routing and workspace mounts still need proof on both supported platforms.

Jobs API has one SQLite FIFO queue and one shared capacity rule for all job and manual managed sandboxes. Count **allocated containers** (including retained non-stopped ones), not SH commands or proxies. Reserve slots atomically; release only after destruction. Container creation failures must not block later queued jobs.

**Recovery:** Agent API recovers ordinary agents under its existing semantics. Jobs API reads execution status and applies stopped/non-stopped cleanup decisions. Do not add a distributed coordinator, special creation-response reconciliation or a second execution-recovery engine. Pending jobs need not auto-resume after Jobs API restart; they can be marked interrupted/abandoned. Ambiguous calls or crashes may leave orphans, an accepted V1 trade-off. Use normal exception handling and best-effort cleanup rather than building recovery machinery for every possibility.

**UI is required in V1:** Existing and Pending container/job lists, status/progress, inspection, pending cancellation and deletion controls. An active/non-stopped execution must not be deleted through an ordinary active-container action; the UI can invoke existing cancellation/kill controls and request deletion after status is stopped/terminal. Do not create a hidden force-delete endpoint or duplicate job state in the UI.

Submission returns `jobId`; Jobs API exposes status/result queries for UI/CI. No callbacks, provider status posting or long-running CI-job dependency.

## 8. Existing code touchpoints

The new services/tools are **not yet implemented on `main`**. Inspect these again before coding:

- `src/Mezhs.Agent.Api/Program.cs` — current service composition and `ExecutorService` registration.
- `src/Mezhs.Agent.Api/Commands/Shell.cs` — direct `ExecutorService.Execute` / `WaitAsync` and environment construction.
- `src/Mezhs.Agent.Api/Services/AgentService.cs` and `PolicyRegistry.cs` — current registered `policyId` lookup; add path resolution and persist/use per-execution snapshot.
- `src/Mezhs.Agent.Api/Services/AgentWorker.cs` and related persistence/recovery — status, pause/cancel semantics and resumed work.
- `src/Mezhs.Executor/ExecutorService.cs`, `ExecutorRunner.cs` and launcher — currently sealed/existing durable process behavior; factor a shared base and specialized launch without duplication.
- `src/Mezhs.Executor/ExecutorEnvironment.cs` and `src/Mezhs.Console/MezhsExecutionContext.cs` — trusted execution variables, avoiding accidental host environment forwarding.
- Completion policy/parser — `requiredSuccessfulCommands` enhancement and new `<ABORT>` marker.
- Existing Agent API client and UI — same-host API calls; Jobs API adds a separate owning API.

Do not silently incorporate the separately proposed `plan/multi-agent-orchestration` lifecycle/status changes.

## 9. Nine independently deliverable implementation tasks

The architecture above is the approved parent decision record. Implement the feature in the following dependency order, **one task at a time**, rather than as one long-lived implementation branch. Each task begins from the latest owner-approved `main` and must build, have focused regression evidence, pass the applicable independent review/testing, and receive **separate owner approval before merge**. Do not start the next code task until its predecessor is merged (unless the owner explicitly authorizes an exception). Create a small child plan under `specifications/planned/<task-id>/plan.md` before that task; the child plan links here and may not silently revise previously grilled architecture decisions. Classification and independent checks follow `.agents/workflow.md`.

| # | Task / implementation branch | Scope and independent completion gate | Depends on |
| --- | --- | --- | --- |
| 1 | **agent-policy-foundation** | Extend Agent API file-path policy loading; use persisted effective policy snapshot for queued, running and recovered executions; add `<ABORT>` and exact/prefix SH successful-command matching. Preserve existing host agents. Unit/integration tests verify each behavior without Docker or Jobs API. | Existing Agent API |
| 2 | **container-tool-docker** | Standalone `Mezhs.ContainerTool` CLI to create/start/inspect/stop/destroy containers on the configured local Docker engine, mount a supplied workspace and handle errors/cleanup. No proxy or scheduler yet. Prove the lifecycle on supported available engine configurations. | 1 (sequential merge; technically independent) |
| 3 | **container-tool-proxy** | Extend ContainerTool with host-managed per-sandbox NGINX, exact HTTP method/path/query permissions, TCP destinations, connectivity and cleanup. Test permitted/denied proxy requests and the relevant Linux/Windows deployment paths; **DevOps**, not MEŽS, enforces network isolation/egress. | 2 |
| 4 | **executor-sandbox-routing** | Reuse existing durable Executor history and runner; add backend selection, immutable persisted sandbox assignment in Agent API and detached Executor state, Docker SH launch, recovery/kill/timeout behavior and no host fallback. **Reject `ATTACH` for sandboxed executions here**, when `SandboxId` exists. Preserve trusted host execution. | 2–3 |
| 5 | **jobs-git-preparation** | Prepare Jobs API-owned Git services: `jobs.yaml` credential lookup, exact-SHA checkout, trusted-ref sandbox config pinning, required project YAML validation, protected-code marker replacement and sanitized workspace. No Docker allocation or job scheduler yet. | 4 (sequential merge; component independent) |
| 6 | **jobs-api-lifecycle** | Add `Mezhs.Jobs.Api` service, SQLite FIFO queue/capacity, status endpoints, ContainerTool allocation and cleanup, Agent API creation/status integration, failure paths. Test the full job-to-terminal lifecycle with controlled local/test policy **without** real provider comment publishing. | 2–5 |
| 7 | **jobs-github-review** | GitHub PR identity/revision validation, job-scoped comment endpoint and real provider summary submission; failures including ambiguous responses are ordinary errors without special retry logic. End-to-end GitHub review and comment tests. | 6 |
| 8 | **jobs-gitlab-review** | GitLab MR adapter using the same Jobs API workflow, token rules and job-scoped comment behavior. End-to-end GitLab review tests without provider-specific orchestration duplication. | 7 |
| 9 | **jobs-management-ui** | Jobs/Containers Existing and Pending lists, status/progress, inspection, cancellation and permitted deletion via the Jobs API. Full cross-component and regression verification for both providers, concurrency, failures and cleanup. | 6–8 |

**Task boundaries:** Tasks 1–4 establish behavior and infrastructure but do not introduce a parallel Jobs API scheduler. Task 1 does **not** implement sandbox `ATTACH` blocking before a persisted sandbox identity exists: Task 4 owns that requirement. Tasks 2–3 do not implement DevOps' firewall/network admission policy. Task 5 does not secretly start jobs. Task 6 may use a controlled test-only agent completion path while Git provider publishing is not yet integrated; production review completion remains subject to the parent comment requirement. Tasks 7–8 must reuse the same job record and lifecycle. Task 9 completes the agreed V1 UI, not a new UI state store.

**Review gates:** The approved overall architecture has undergone owner Grill Me and an independent high-risk review. Each task must honor `.agents/workflow.md`; any substantive new high-risk design must undergo independent plan review before coding. No child task may amend a grilled owner decision without the owner's explicit approval. Review/test evidence is attached to the exact implementation SHA. Only task 9 closes the complete parent feature.

**First child plan:** [agent-policy-foundation](../agent-policy-foundation/plan.md). Create subsequent child plans when their task starts; do not pre-design all nine implementations or expand their scope.

### Observable acceptance criteria

- [ ] GitHub/GitLab requests run the **exact SHA** when submitted; later branch pushes do not alter the review. There is no automatic scheduling or callback.
- [ ] An unconfigured repository is attempted anonymously. `jobs.yaml` exact-repo override beats host default; editing YAML affects the **next** clone/comment credential lookup without restarting Jobs API.
- [ ] Missing selected project YAML, malformed YAML or malformed `SecurityCritical` markers fails before sandbox creation. Valid protected regions become unique placeholders, and no compilation repair is performed.
- [ ] For repositories configured with `projectConfiguration.repositories[].sandbox.source: trustedRef` in host `jobs.yaml`, modifying `.mezhs/sandbox.yaml` in an external PR/MR has no effect on the sandbox. The configured trusted ref is pinned to a commit per job; missing/unreadable trusted config fails closed without PR fallback. Non-opted-in repositories retain requested-revision configuration for trusted sources only.
- [ ] Agent API reads a path policy once, persists effective policy and immutable sandbox assignment, and uses them during normal execution/recovery. Host execution remains optional under trusted host settings.
- [ ] Sandbox agent emits ordinary `<SH>`; shared Executor records output/exit/timeout using specialized Docker launch and no duplicate execution store. Docker process behavior is tested.
- [ ] `ATTACH` commands are rejected for sandboxed executions even if project policy allows them; no sandbox-origin file path can trigger an Agent API host-file import. Trusted local agents retain existing `ATTACH` behavior for V1.
- [ ] The policy requires a **successful** Jobs API comment command before `<DONE>`; unsuccessful HTTP calls do not satisfy it; `<ABORT>` fails the execution. Jobs API posts one summary or no-findings comment to the correct existing PR/MR. Timeout/ambiguous provider responses follow the ordinary error path without automatic retries or special handling.
- [ ] Jobs API status classification controls cleanup: **stopped/terminal => stop/destroy remaining container processes and release resources; non-stopped => retain**. No resumable Pause/Resume implementation is introduced.
- [ ] NGINX exact job-specific method/path/query and TCP rules work in normally isolated configurations; bypass attempts and developer-configured privilege limitations are tested/documented, not silently hidden or overpromised.
- [ ] One FIFO capacity rule governs all managed containers; concurrent submissions do not overbook; terminal destruction frees a slot and admits the next pending job.
- [ ] Existing/Pending UI, inspection, cancellation, deletion and status queries work. The UI does not store another authoritative job state.
- [ ] Only supported **local** Docker engine OS/images are accepted; Linux/Windows deployment paths are validated separately, without simultaneous-mode or remote-engine features.
- [ ] No code commit/push, new PR/MR, inline comment, Jira, automatic review trigger or callback is shipped in V1.

## 10. Risks, deferred work and superseded designs

**Accepted operational risks:** Jobs API has no caller authentication, so network-reachable callers can invoke its exposed operations; DevOps controls admission. Developers can configure Docker privileges/mounts that compromise NGINX containment; DevOps controls Docker/host trust. An abnormal crash or ambiguous inter-service call can orphan work; no cross-service transaction system. Redaction may make sanitized code uncompilable; no auto-stubs.

**Technical proof still required:** MEŽS NGINX routing/query enforcement and host-side NGINX reachability from Linux/Windows containers; demonstration that deployment-owned network controls block direct egress when proxy-only isolation is claimed (not a MEŽS enforcement feature); Docker exec output and timeout/kill semantics; same-host workspace mounts; FIFO and cleanup behavior. Do not mistake a design choice for demonstrated isolation.

**Deferred:** Git commit reconstruction of protected code, code changes/PR creation, inline comments, Jira, review status callbacks, GitHub Apps, token UI, automatic review triggers, resumable Pause, multi-agent descendants, remote Docker engines, cross-OS simultaneous engines, replacing/removing the existing `ATTACH` command, and any generic recovery/auth policy subsystem not requested.

**Do not revive:** Container queue/CRUD in Agent API; Git-only tool owning source but not job lifecycle; scheduler or SQLite queue in ContainerTool; project YAML choosing host execution; subclassing Agent API for Git; a second Executor persistence model; Agent API destroying Jobs-owned containers; custom network firewall or container shell daemon without verified need; opportunistic multi-agent orchestration rewrite.

**Release discipline:** Planning commits do not change the version or changelog. Independent code review and independent testing are required for this high-risk feature. Implementation merge into `main` needs separate explicit owner approval; version/changelog are finalized once at approved merge.
