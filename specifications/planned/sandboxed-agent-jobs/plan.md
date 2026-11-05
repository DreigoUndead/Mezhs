# MEŽS sandboxed agent jobs — V1 architecture

**ID:** `sandboxed-agent-jobs`  
**Level:** High-risk — architecture, container/network boundary, Git credentials and concurrency.  
**Status:** Independent High-risk architecture review performed against `main` at `32f9a545c41fa8a3e981676693d59220aa2926c5` on 2026-11-05. **Plan corrections recorded; open security/deployment gates below. Not implemented.**  
**Repository:** `DreigoUndead/Mezhs`  
**Location:** `specifications/planned/sandboxed-agent-jobs/plan.md` on `main`.  
**Related plan:** `docs/plans/multi-agent-orchestration.md` on `plan/multi-agent-orchestration` is separate and unimplemented. Do not implement or edit it under this task.

This is the V1 decision record, not a discussion transcript. Examples illustrate contracts without freezing DTO/YAML names. Independent review may correct verified documentation errors; **fundamental changes to the accepted trust model require owner decision**. A plan review does not authorize implementation or merge.

## Goal and scope

Jobs API accepts requests from a **trusted, network-admitted caller** to run a MEŽS agent against an exact Git revision inside a disposable local Docker sandbox. The agent reviews code and posts **one Markdown summary comment** on an existing GitHub pull request or GitLab merge request. Jobs API exposes status, scheduling and container management to the UI and callers. Trusted local MEŽS agents can continue using the host Executor when permitted by host configuration.

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

- Jobs API SQLite: jobs, pending queue, capacity reservations, sandbox/workspace association, status/result reporting, and the one-comment submission outcome (including an unknown delivery outcome).
- Agent API persistence: chats, execution records, immutable sandbox assignment and policy snapshot.
- Docker runtime: actual container processes, not queue state.
- Protected original Git checkout: exact repository revision, including `.git`, managed by Jobs API outside the normal sandbox mount.

Do not duplicate job state in Agent API, container CRUD in Agent API, or command history in SandboxExecutor.

## 2. V1 workflow

1. Caller submits repository URL, exact SHA, PR/MR identifier and review task to Jobs API. It returns `jobId`. **Jobs API V1 has no API key, OAuth or repository allowlist**: caller access is governed by the deployment network. GitHub Actions/GitLab CI decide when to invoke it.
2. Jobs API validates a supported GitHub/GitLab HTTPS repository URL and that the PR/MR belongs to it, resolves any Git token from the current host `jobs.yaml`, and checks out the **exact requested revision**. The SHA must exist in the selected repository; do not silently substitute a branch tip. A repository with no configured token may be cloned anonymously; fail clearly if access requires credentials. Keep credentials out of Git URLs, logs and unintended redirect hosts; use argument-safe Git invocation.
3. Jobs API requires valid `.mezhs/agent.yaml` and `.mezhs/sandbox.yaml` from that revision. It retains the original checkout, prepares a separate writable workspace without Git metadata or references back to protected host files, and replaces `SecurityCritical` regions with unique placeholders **before** creating any container. Reject malformed markers and unsafe symlink/path traversal; do not let submodules, nested Git metadata or copy behavior bypass sanitization.
4. Jobs API enqueues the sandbox allocation under one shared FIFO capacity limit. When space is available, it calls ContainerTool with project Docker/network settings and the sanitized workspace. ContainerTool uses the configured **local** Docker engine and manages an associated **host-side** NGINX proxy.
5. Jobs API starts the agent through the **normal Agent API creation endpoint**, passing task, immutable `SandboxId` and the **absolute host path** to the original checkout's `.mezhs/agent.yaml` as the existing `policyId` parameter. Agent API validates the sandbox assignment and resolves the project policy as one policy definition (not a second global Agent configuration); it persists the compiled effective policy and sandbox assignment together **before enqueueing**. Executing/recovered work uses the persisted policy, not a later file read.
6. SandboxExecutor runs normal `<SH>` commands in the container. The agent inspects the prepared checkout as its project policy instructs. The agent does not receive Docker/Git credentials or the original checkout via the standard workspace preparation.
7. The project policy directs a successful SH call to the **job-scoped Jobs API comment endpoint** before `<DONE>`. Jobs API posts at most one Markdown summary to the existing PR/MR and durably records a confirmed provider result; ambiguous provider outcomes are not blindly retried. An SH command matching text and exiting zero is **not proof that a comment was posted**. Jobs API judges neither review quality nor findings, but only classifies a job successful after a confirmed comment record and terminal successful Agent execution. `<ABORT>` ends the Agent unsuccessfully.
8. Jobs API queries Agent API status. **If Agent API classifies the execution as terminal** (`Completed`, `Failed` or `Cancelled` in today's implementation), Jobs API stops/destroys the container with remaining processes, destroys its proxy, releases its mount and job directory, and frees queue capacity **only after confirmed destruction**. Nonterminal executions (`Queued`, `Running`, `CancelRequested`) retain their sandbox; API unavailability must not be interpreted as termination. Jobs API exposes Agent outcome and comment delivery outcome separately from job outcome.

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

- `sandboxId != null`: route **all** SH commands of that execution to SandboxExecutor and the assigned container. **Never fall back to host execution** for an unknown, missing, destroyed or unreachable assignment.
- `sandboxId == null`: use existing host Executor **only if trusted Agent API host settings permit it**. A restricted deployment rejects missing/unusable sandbox assignment rather than falling back to host. Personal deployment can allow either route.
- Sandbox assignment is immutable for the execution, recorded with its root Agent row and copied to each durable Executor shell row as a backend/target discriminator. On an existing `chatId`, reject mixing sandboxed and host executions or changing the sandbox. Policy, prompt, project YAML or user-provided environment cannot change target. An opaque ID resolves to a **Jobs-managed** container on the configured local engine; it is not an authentication secret. Only host-trusted Agent API/Jobs API code can approve a sandbox assignment.
- Existing `MEZHS_*` execution context may convey a host-side `MEZHS_SANDBOX_ID` generated from persisted state; user input cannot set reserved routing variables. Never blindly forward host environment snapshots, credentials or internal API context into Docker.
- Normal/host agents work without Jobs API; Jobs API's project `agent.yaml` is not allowed to select host execution. **Non-SH Agent actions must obey the same boundary:** today's `ATTACH` reads host paths via `AgentAttachmentStaging`; explicitly reject it for sandboxed executions in V1 rather than allowing project policy to turn it on. Audit future command handlers for host-side effects.

### File-path policy

The existing `policyId` parameter supports registered policies today; extend resolution for the Jobs-owned checkout's project policy file. Reuse the existing `PolicyDefinition`/`PolicyDecoder` validation and compiled rules for one policy without interpreting the file as a complete Agent API host configuration. Require a valid host-registered connection/model. **Read once at admission, persist and reconstruct the effective compiled policy from `ExecutionRecord.PolicySnapshot` during execution/recovery.** Merely storing the snapshot is insufficient: `AgentWorker` currently calls `PolicyRegistry.Get(execution.PolicyId)`. New executions may read updated files; no watcher/hot-reload or global policy registration per job. Do not expose arbitrary filesystem reads through new untrusted policy-path inputs.

### Inherit the existing Executor

Factor only the actual host-versus-sandbox **process launch** difference in `ExecutorService`/`ExecutorRunner` (a narrow dispatcher/strategy is sufficient; inheritance is optional). Retain one Executor store, IDs, deduplication, output, timeout, kill and restart lineage. `ExecutorRuntimeLauncher` starts a detached runner with only the SQLite path and execution ID; **persist backend and SandboxId on each shell row, including restarted rows**, so the detached runner and restart cannot accidentally launch a host shell. Reject sandbox shell restart once its container is destroyed or job terminal. No second shell store or standalone container daemon. Inspect all direct Agent API references while refactoring.

The agent still emits plain `<SH>`, never `docker exec`. SandboxExecutor translates to Docker `exec` using the assigned container and correct platform shell. Each command has a new shell process in the same persistent filesystem; shell-local `cd` and variables do not automatically carry over. Correct exit/output propagation and individual command timeout handling require platform tests: killing the host-side `docker exec` client does not necessarily kill the command inside Docker.

### Existing status behavior; new completion marker

**Do not implement a new resumable Pause/Resume lifecycle.** Use the existing Agent API pause/cancellation and normal recovery semantics for sandboxed agents. Jobs API uses the existing Agent API status/enum classification: **stopped/terminal ⇒ kill remaining sandbox processes and destroy all job-owned sandbox resources; non-stopped ⇒ keep the sandbox**. No duplicate status enum, force-delete lifecycle or multi-agent `Stopped` terminology change. The future multi-agent plan retains its own decisions.

- `<DONE>`: policy requirements must be fulfilled.
- `<ABORT>`: new standalone immediate unsuccessful termination, ignoring `requiredSuccessfulCommands`. If present with pending executable blocks, do not execute those blocks.
- Extend existing `requiredSuccessfulCommands` minimally beyond command names to **exact SH text or a prefix ending in `*`**, retaining backward compatibility with command-name rules. A matching successful SH is an **Agent-level workflow requirement, not trusted proof of HTTP delivery**: shell text can return zero without making the intended call, and a repository policy can omit the rule. Jobs API uses its own existing job record of a confirmed comment before reporting job success. Do not add `<JOB_COMMENT>` or a second Agent outcome state machine.
- HTTP errors must yield a nonzero SH exit code (e.g. `curl --fail-with-body`). Failed calls cannot satisfy the successful-command requirement. Rejected `<DONE>` explains missing commands; `<ABORT>` remains an escape.
- A command timeout during a still-active agent follows the existing Executor behavior; it is **not** automatically a terminal job unless Agent API's status says so. Test Docker-specific cancellation behavior, then stop the entire container on terminal cleanup.

## 4. Configuration and credential ownership

### Required project configuration

Both files are **mandatory in the requested Git revision**; missing or malformed files fail job preparation, with no project defaults:

| File | Meaning |
| --- | --- |
| `.mezhs/agent.yaml` | Instructions, model/policy, review scope, allowed commands, required successful Jobs comment call |
| `.mezhs/sandbox.yaml` | OS/image, resources, Docker options/mounts, HTTP endpoint/method/query permissions and TCP destinations |

Developers may modify either file in the reviewed branch. Project YAML can request Docker privileges, networking, mounts and destinations; **MEŽS does not add developer-facing approval systems or silently change these project choices**. This makes the repository revision's configuration **host-privileged input**, not data safe to run from an unknown fork or adversarial author. DevOps must admit **trusted repository owners/revisions and network callers** and protect Docker/host access. With `privileged`, host mounts/network or Docker socket requested, there is **no host security boundary**; calling it a sandbox does not change that fact. This explicitly accepted trust model must be communicated to operators, not described as isolation from malicious project authors.

**Host versus sandbox is the one separate trusted routing choice:** whether Agent API may execute host commands is decided by host-owned Agent API settings, not project YAML.

### Host-owned `jobs.yaml`

Use one Jobs API server-side `jobs.yaml` for Git credentials and Jobs API/container/proxy/queue settings as applicable. Do not introduce a separate `containertool.yaml` in V1 without an actual need. It is outside cloned repositories and **not part of the normal container mount**.

GitHub and GitLab use **configured access tokens** in V1, not GitHub App installations or provider-native token renewal. Support both Git-server/host defaults and repository overrides; resolution priority:

1. Exact repository-specific token.
2. Matching Git-server/host default.
3. Anonymous Git operation when none is configured.

Tokens may be literal in protected host config or references to environment variables. Do not leak tokens through shell arguments/logs, command output, clone URL, workspace, container environment or unrelated/redirected Git hosts. Disable implicit credential-helper leakage or user-controlled Git protocol/transport configuration; never evaluate a repository URL through a shell.

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

Do not include `.git` (including nested worktrees/submodules), Git credentials, symlinks escaping the prepared workspace or protected original code in the **normal** sandbox workspace. Copy physical files safely without following links outside the checkout; either reject symlinks or validate and materialize their in-tree targets and reapply redaction. The agent can inspect, test, compile and modify scratch files, but no V1 modifications are committed or pushed. Explicit project-requested privileged host mounts bypass this promise and belong to the documented trusted-config exception.

**Redaction before Docker creation:** Recognize `//<SecurityCritical>` through `//</SecurityCritical>` and replace each full region with a unique single comment, e.g. `//secure-code#a81f2`. Keep originals in Jobs API's checkout. Missing start/end, unexpected closing or nested markers fail the job with a useful file/location error. Do **not** create stubs or require sanitized code to compile; the agent reports compilation limitations. Reconstructing original code for future code edits/commits is **future scope**, not V1.

Project policy dictates review scope and method. Do not force a Git diff workflow: the agent receives a working source tree without `.git`; its policy must be written accordingly. No synthetic diff required.

The sandbox's comment call is restricted by its generated NGINX rules to the owning job. Jobs API resolves repository/PR/MR target from the **job record**, not from arbitrary destination fields in the agent's comment body. Jobs API owns a job-scoped, **single-comment** operation: atomically claim a submission attempt, post only to the PR/MR stored in that job, and persist confirmed result (or `unknown` if delivery may have happened but the response was lost). Repeated concurrent SH requests cannot generate multiple comments; an unknown outcome requires inspection, not automatic repost. The Agent policy encourages and checks the intended command; **only the Jobs API provider result** establishes comment delivery for job-success classification. This is job reporting, not review-content adjudication.

Cleanup sequence: finish any outstanding Git/comment operation that requires job state; stop and destroy container/proxy and release mounts; then delete the Jobs API job directory and free capacity. No separate container file export pipeline.

## 6. NGINX and deployment trust boundary

Provide **one NGINX instance/configuration per sandbox, running on the host**, managed by ContainerTool. **Not** a Linux-only Docker sidecar and **not** a new custom firewall service. Jobs API generates job-bound proxy permissions from project `sandbox.yaml` and trusted job ID.

- HTTP permissions can specify exact **method, path and query**, e.g. allow only `POST /Git/Comment?id=<jobId>` for that sandbox's own job. An NGINX `location` path match does not automatically validate query arguments; implement explicit matching for job ID, duplicates/encoding and unintended parameter variations.
- Support developer-requested TCP destinations such as database endpoints, and allowed HTTP targets. The developer can change those as needed; MEŽS does not build a second network-access approval list.
- Jobs API supplies job identity and the recorded comment target. The container cannot select another job **only if** it cannot bypass its assigned per-sandbox proxy. The host Jobs API listener must not be directly reachable from normally isolated containers; restrict other sandbox proxy listeners too. NGINX path/query rules are not a substitute for Docker/network egress policy.
- Test method/path/query filtering, TCP forwarding, ordinary proxy routing and attempts to reach Jobs API directly or through another sandbox in normal isolated configurations. Arbitrary HTTPS CONNECT tunnels cannot provide path-level filtering without controlled termination.

**Enforcement requirement for the normal configuration:** A default Docker bridge gives containers direct outbound networking; **host NGINX alone does not force traffic through it**. A platform-specific Docker network + DevOps host/network firewall/ACL setup must demonstrably deny direct container egress and direct Jobs API/other-proxy access while permitting only the assigned proxy and explicitly configured TCP routes. No custom MEŽS firewall service is required, but the supported deployment configuration and bypass tests are mandatory. If a Linux/Windows setup cannot enforce this, fail that isolated deployment path rather than claim enforced egress.

**Explicit accepted privileged exception:** Developers may request Docker `privileged`, host networking, host mounts or Docker socket access. MEŽS does not reject these configurations; they may bypass all NGINX and host isolation. DevOps owns access to the host, Docker daemon, network and trusted Git revisions. Document the exception clearly: this deployment is **trusted-code execution**, not a security sandbox. A network firewall cannot contain a mounted Docker socket.

Agent API is internal to the trusted same-host setup; Jobs API exposure is explicitly a network/deployment decision. Do not assume private networking supplies per-user authorization—none is implemented in V1.

## 7. Local Docker, scheduling and UI

V1 supports the **configured local Docker engine only**. ContainerTool does not connect to remote engines, orchestrate engine provisioning or automatically switch Docker Desktop Linux/Windows modes. Use the image/OS supported by the active local engine; an incompatible requested image fails clearly.

Test Linux and Windows configurations **separately** on appropriate installations; **simultaneous Linux and Windows execution is not a V1 requirement**. The host NGINX proxy choice is intended to avoid platform-specific sidecar images, but actual host/guest routing and workspace mounts still need proof on both supported platforms.

Jobs API has one SQLite FIFO queue and one shared capacity rule for all job and manual managed sandboxes. Count **allocated containers** (including retained non-stopped ones), not SH commands or proxies. Reserve slots atomically; release only after destruction. Container creation failures must not block later queued jobs.

**Recovery:** Agent API recovers ordinary agents under existing semantics. On Jobs API startup perform **one bounded reconciliation** of SQLite allocations against containers/proxies identified by managed labels/IDs before admitting new capacity; keep confirmed nonterminal allocations reserved, retry safe cleanup for confirmed terminal work and surface unknown/orphaned allocations for operator action. Pending jobs need not auto-resume and may be marked interrupted. Do **not** infer free capacity from an unverified SQLite state, silently restart an ambiguously created container or delete a live unknown agent. No distributed coordinator, cross-service transaction or generic retry framework; uncertain resources can remain reserved until explicitly resolved. Agent API unavailable means unknown/nonterminal, not stopped.

**UI is required in V1:** Existing and Pending container/job lists, status/progress, inspection, pending cancellation and deletion controls. Agent Web currently proxies `/v1/*` only to Agent API; explicitly wire Jobs views to the separate Jobs API without inventing a UI-side store or redirecting existing Agent endpoints. An active/non-stopped execution must not be deleted through an ordinary active-container action; the UI can invoke existing cancellation/kill controls and request deletion after status is stopped/terminal. Do not create a hidden force-delete endpoint or duplicate job state in the UI.

Submission returns `jobId`; Jobs API exposes status/result queries for UI/CI. No callbacks, provider status posting or long-running CI-job dependency.

## 8. Existing code touchpoints

The new services/tools are **not yet implemented on `main`**. Inspect these again before coding:

- `src/Mezhs.Agent.Api/Program.cs` — current service composition and `ExecutorService` registration.
- `src/Mezhs.Agent.Api/Commands/Shell.cs` — direct `ExecutorService.Execute` / `WaitAsync` and environment construction.
- `src/Mezhs.Agent.Api/Services/AgentService.cs` and `PolicyRegistry.cs` — current registered `policyId` lookup; add path resolution and persist/use per-execution snapshot.
- `src/Mezhs.Agent.Api/Services/AgentWorker.cs` and related persistence/recovery — status, pause/cancel semantics and resumed work.
- `src/Mezhs.Executor/ExecutorService.cs`, `ExecutorRunner.cs` and launcher — currently sealed/existing durable process behavior; factor a shared base and specialized launch without duplication.
- `src/Mezhs.Executor/ExecutorEnvironment.cs` and `src/Mezhs.Console/MezhsExecutionContext.cs` — trusted execution variables, avoiding accidental host environment forwarding.
- Completion policy/parser — `requiredSuccessfulCommands` enhancement and new `<ABORT>` marker; `PolicyDecoder` compiles current rules and `Interpreter`/`Registry` only know `DONE`, `SH`, `ATTACH`.
- `src/Mezhs.Agent.Api/Services/AgentAttachmentStaging.cs` — currently reads arbitrary host file paths; sandboxed executions must not invoke host-side `ATTACH` in V1.
- `src/Mezhs.Executor/ExecutorStore.cs` and `ExecutorRuntimeLauncher.cs` — persist/reload shell backend and sandbox target across detached process starts, deduplication and restarts.
- `src/Mezhs.Agent.Web/Program.cs` — existing `/v1` reverse proxy targets only Agent API; Jobs API needs its own explicit UI connection.
- Existing Agent API client and UI — same-host API calls; Jobs API adds a separate owning API.

Do not silently incorporate the separately proposed `plan/multi-agent-orchestration` lifecycle/status changes.

## 9. Implementation sequence and acceptance tests

1. **Independent High-risk plan review**, using a separate reviewer, with findings presented to the owner before any edits. No implementation until the plan review has been resolved and the owner authorizes coding.
2. **ContainerTool feasibility / implementation gate:** configured local engine, requested image/OS, safe workspace mount, cleanup, host-managed NGINX and job-scoped HTTP/query/TCP rules **plus demonstrably enforced normal-case egress and Jobs API separation**. Prove Linux and Windows deployments separately before claiming both supported. If mandatory isolation/host reachability fails, bring the platform/design conflict to the owner instead of adding a silent unsafe fallback.
3. **Executor and Agent API:** shared/derived executor refactor, persistent immutable sandbox assignment, file-path policy loaded/snapshotted once, normal pause/recovery behavior, required successful SH exact/prefix matching, `<ABORT>`, shell output/exit/timeout behavior.
4. **Jobs API:** `jobs.yaml` lazy Git token resolution, GitHub and GitLab adapters, exact SHA clone, mandatory project YAML, protected-code placeholder handling, queue/capacity, container CRUD, Agent API status monitoring, one-comment operation, cleanup and job status queries.
5. **Full Jobs/Containers UI** and end-to-end runs for both Git providers, concurrent requests, error paths and status-based deletion, followed by independent code review and independent testing under `.agents/workflow.md`.

### Observable acceptance criteria

- [ ] GitHub/GitLab requests run the **exact SHA** when submitted; later branch pushes do not alter the review. There is no automatic scheduling or callback.
- [ ] An unconfigured repository is attempted anonymously. `jobs.yaml` exact-repo override beats host default; editing YAML affects the **next** clone/comment credential lookup without restarting Jobs API.
- [ ] Missing project YAML, malformed YAML or malformed `SecurityCritical` markers fails before sandbox creation. Valid protected regions become unique placeholders, and no compilation repair is performed.
- [ ] Agent API reads a path policy once, persists and actually **executes** the compiled snapshot and immutable sandbox assignment during normal execution/recovery; reused chats cannot switch targets. Host execution remains optional under trusted host settings. Sandbox `ATTACH` is denied (no host-file import).
- [ ] Sandbox agent emits ordinary `<SH>`; shared Executor records output/exit/timeout, durable backend/target and restart lineage using specialized Docker launch with no second store. Docker process behavior and no-host-fallback/terminated-container restart are tested.
- [ ] The policy requires a successful Jobs API comment command before `<DONE>`; failed HTTP exit does not satisfy it and `<ABORT>` fails execution. A forged zero-exit SH or omitted project rule cannot cause a successful **job** without a confirmed provider comment. Concurrent/repeated/ambiguous comment calls do not silently duplicate posts.
- [ ] Jobs API status classification controls cleanup: **stopped/terminal => stop/destroy remaining container processes and release resources; non-stopped => retain**. No resumable Pause/Resume implementation is introduced.
- [ ] NGINX exact job-specific method/path/query and TCP rules work; **default Docker bridge direct egress is denied by a demonstrated deployment boundary**, direct Jobs API and cross-proxy bypass attempts fail in a supported normal configuration. Privileged developer-configured bypass is openly documented as trusted-code execution.
- [ ] One FIFO capacity rule governs all managed containers; concurrent submissions do not overbook; only confirmed terminal destruction frees a slot; startup reconciles reservations and retains uncertain allocations rather than admitting excess containers.
- [ ] Existing/Pending UI, inspection, cancellation, deletion and status queries work. The UI does not store another authoritative job state.
- [ ] Only supported **local** Docker engine OS/images are accepted; Linux/Windows deployment paths are validated separately, without simultaneous-mode or remote-engine features.
- [ ] No code commit/push, new PR/MR, inline comment, Jira, automatic review trigger or callback is shipped in V1.

## 10. Risks, deferred work and superseded designs

**Accepted operational risks:** Jobs API has no caller authentication, so network-reachable callers can invoke its exposed operations; DevOps controls admission. Developers can configure Docker privileges/mounts that compromise NGINX containment; DevOps controls Docker/host trust. An abnormal crash or ambiguous inter-service call can orphan work; no cross-service transaction system. Redaction may make sanitized code uncompilable; no auto-stubs.

**Technical proof still required before full development handoff:** NGINX routing/query enforcement **and independent egress blocking** on each claimed supported platform; no direct Jobs API/cross-sandbox proxy reachability; safe host/container mounts; Docker exec timeout/kill semantics and durable routing; safe checkout without symlink/Git metadata leakage; FIFO cleanup and restart reconciliation. Do not mistake the choice of NGINX for isolation evidence.

**Deferred:** Git commit reconstruction of protected code, code changes/PR creation, inline comments, Jira, review status callbacks, GitHub Apps, token UI, automatic review triggers, resumable Pause, multi-agent descendants, remote Docker engines, cross-OS simultaneous engines and any generic recovery/auth policy subsystem not requested.

**Do not revive:** Container queue/CRUD in Agent API; Git-only tool owning source but not job lifecycle; scheduler or SQLite queue in ContainerTool; project YAML choosing host execution; subclassing Agent API for Git; a second Executor persistence model; Agent API destroying Jobs-owned containers; custom network firewall or container shell daemon without verified need; opportunistic multi-agent orchestration rewrite.

**Release discipline:** Planning commits do not change the version or changelog. Independent code review and independent testing are required for this high-risk feature. Implementation merge into `main` needs separate explicit owner approval; version/changelog are finalized once at approved merge.

## 11. Independent review — owner/deployment gates (2026-11-05)

This section records verified conflicts and minimum implementation contracts, **not a new infrastructure design**.

### Verified against current `main`

- `AgentWorker.ProcessAsync` resolves `PolicyRegistry.Get(execution.PolicyId)`, while `AgentStore` already persists `PolicySnapshot`. V1 must compile and run the persisted snapshot, including after restart, or the promised immutability is false.
- `Shell.ExecuteAsync` sends `ExecutorEnvironment.SnapshotForChildExecution()` into the host Executor. Sandbox routing must construct a **separate allowlisted container environment**; never forward this host snapshot, storage path, credentials or host workspace. Preserve only necessary harmless execution identifiers and project-approved variables.
- `AgentAttachmentStaging.StageAsync` resolves local host file paths against `AgentOptions.Workspace` and imports their contents. The project can allow `ATTACH` in a policy today; V1 sandboxed executions must reject this action before executing it.
- `ExecutorRuntimeLauncher` starts a separate runtime from only execution ID + SQLite path, and `ExecutorStore` deduplicates by `(ChatId, TriggerMessageId, CommandIndex)`. Target and backend must be durable and deduplicated calls must verify the original target; a retry cannot rebind a command to the host or another container.
- `AgentService.SetPaused` currently cancels active work; it is **not resumable Pause**. `AgentExecutionStatus` has `Queued/Running/CancelRequested/Completed/Failed/Cancelled` and no `Stopped`. Use current terminal predicate, not future multi-agent states.
- The standalone orchestration branch proposes **live policy reloading and a different Stop/Resume model**. Those incompatible choices are not implemented here. Future merging of both features needs an explicit precedence/migration design; it is not V1 scope.

### Operator trust boundary requiring explicit confirmation

**Decision A — trusted-only V1 (the current owner-selected direction):** Jobs API has no caller authentication and accepts repository-scoped Docker config, including privileged settings. Restrict reachability and permitted repository/revision authors at deployment. Explicitly do **not** offer this as safe execution of adversarial fork/PR configuration. V1 can proceed only in deployments that accept this trust boundary and can prove the normal-case network rules.

**Decision B — hostile-repository safe service (not approved for V1):** To run code/config from untrusted Git contributors, reject or separately authorize host-privileged Docker options, constrain mounts/network/daemon, and authenticate/authorize the caller and project. This changes the agreed scope. **Do not silently implement B.**

**Owner question:** Is trusted-only A sufficient for the actual intended PR/MR inputs, including external contributors/forks? If yes, confirm the deployment documentation and never promise protection from malicious project YAML. If no, stop and redesign admission/isolation before implementation.

### Fail-closed lifetime invariant

A Jobs API reservation remains occupied until its job-owned container/proxy is proven destroyed; an Agent API timeout/unavailable response is not terminal. A sandboxed command always has a valid persisted container target and cannot fall back to a host shell. A job is reported successful only with both a successful terminal Agent result and a confirmed job-owned provider comment. These invariants are required without introducing a second scheduler, shell store or agent lifecycle.

