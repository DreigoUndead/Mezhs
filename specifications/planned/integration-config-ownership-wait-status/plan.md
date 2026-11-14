# Integration configuration ownership and capacity-wait status

ID: integration-config-ownership-wait-status
Level: High-risk — cross-process integration architecture, concurrency admission, and runtime status projection
Base: main (release-only); branch: integration-config-ownership-wait-status
Status: Planning; implementation blocked on independent plan review and explicit owner choice of the cross-process admission boundary

## Goal

Remove the accidental `AgentOptions : MezhsOptions` inheritance and any agent ownership of integration configuration. `mezhs.yaml` alone configures integrations, including `MaxParallel` and `MinInterval`. Agent execution is independent of integration admission. Show a truthful **Waiting** state and orange Agent status dot when an agent's active model request is waiting for an integration capacity permit or the configured minimum send interval.

All clients of the same connection must honor its configured limits; merely loading the same YAML into multiple processes is not sufficient.

## Verified baseline

- `Agent.Api/Program.cs` loads `agent.yaml` via `AgentConfigLoader.Load`, then passes its `AgentOptions` directly into `builder.AddMezhsApi(options)`.
- `AgentOptions` inherits `MezhsOptions`, bringing Server/Transport/Storage/Connections/Extensions into the agent configuration. The generic `MezhsConfigLoader.Load<T>` validates and instantiates connection objects, with `IntegrationLimits.MaxParallel` defaulting to 1.
- `IntegrationRegistry` is a singleton **per API process** and creates one integration instance per configured connection. `BrowserIntegrationBase` gates `SendCoreAsync` with an instance-local `SemaphoreSlim` and a start-spacing semaphore.
- `MessageService.ProcessAsync` marks a queued user message `Running` before calling the integration. If blocked on the integration's limit, the stored state and Agent status dot currently look like actual activity.
- `AgentStore.GetLatestAgentStatuses` returns root execution status only; `AgentChatView` and `AgentStatus` render `Running` green. `ChatStore` already exposes active-message runtime activity.
- Current `mezhs.yaml` configures `chatgpt-sub` `maxParallel: 2` and `minInterval: 5000`, while `agent.yaml` has no such limits (and should not gain any).
- Existing PR #57 (`integration-architecture-v2`) is a feasibility-only typed HTTP transport change. Avoid coupling this work to its unmerged transport redesign.
- Observed four-agent test ended 2 Completed / 2 Failed (HTTP/2 retrieval error and 10s browser-session timeout); causality to parallelism has **not** been proven.

## Scope and design constraints

1. Replace inheritance with independent `AgentOptions` containing only agent policy/runtime configuration. The agent config may reference a `connectionId` in a policy, but must not define provider metadata, limits, session ownership or transport configuration. Validation should resolve policy connection IDs against the integration owner without cloning its config.
2. Separate the core integration config loading/DI from agent-specific configuration. Core config remains sourced from `mezhs.yaml`. Agent-specific server/workspace/storage settings require explicit standalone contracts or composition, not inherited integration options. Avoid duplicating the full integration definition or limits in agent YAML.
3. **Cross-process admission must be real.** Choose exactly one supported, testable integration-owned admission boundary, shared by API Web, Agent API and future clients; uphold `MaxParallel=0` as unlimited and `MinInterval=0` as no delay, and preserve cancellation and fault-release behavior. A per-process `SemaphoreSlim` does not satisfy this requirement.
4. Prefer a single authoritative integration execution owner/service with bounded client adapters **if** it can reuse the existing core message API and lifecycle without introducing a second queue or replaying prompt submissions. This needs a feasibility/dependency checkpoint: maintaining separate API processes, chat/file store access, browser session affinity, authentication and cancellation must remain correct. If that introduces disproportionate remote API/proxy complexity, pause and present the simpler alternative: integration-layer cross-process admission coordination with one YAML source and explicit limitations. Do **not** silently select the architecture or implement an arbitrary distributed lock.
5. Report integration admission waiting through the existing `IntegrationSendContext.ReportActivity` mechanism. Include both capacity-gate and `MinInterval` wait. Emit a distinct machine-readable state (`waiting`), provide a descriptive detail, and transition out immediately on permit acquisition. An agent working in its own shell or idle between model turns is **not** waiting on an integration permit.
6. Derive an Agent **effective UI status** of `Waiting` only for a running root execution with an actively waiting model message. Keep durable root status `Running` (no SQLite root-status migration), and project waiting through the existing agent-chat/runtime API efficiently. A cancelled, failed, completed, or stale message may not leave the dot orange.
7. In Agent Web, show the Waiting dot orange in both sidebar and selected-chat header, with accessible label/hover/focus and reduced-motion compatibility. Preserve existing blue unread-completion, green running/completed, paused/stopping, and failed precedence.

## Decisions required before code

- **Admission ownership:** (A) central integration host: agents use core API integration execution, one governor; (B) retain both hosts but use integration-owned, cross-process coordinated admission. Plan reviewer must assess compatibility, failure handling, complexity and whether the existing core API can safely carry agent lifetimes without duplicate prompt submission.
- **API boundary:** if the agent host continues exposing core-compatible endpoints to Agent Web, specify which are local and which are forwarded; avoid a broad unreviewed rewrite.
- Do not accept a change that only loads `mezhs.yaml` into two independent registries and claims global enforcement.

## Proposed sequence (after independent plan review and owner architecture decision)

1. Document current service/DI and provider-call ownership; add executable regressions proving independent configurations. Capture affected current specs and CLI/test fixtures.
2. Split `AgentOptions` and `MezhsOptions`. Establish clear loading and DI composition, move integration ownership solely to core configuration, preserve standalone startup and policy connection checks.
3. Implement the selected **integration-owned** cross-client admission owner, including capacity/start-spacing and cancellation/release guarantees. Avoid changes to the agent scheduler.
4. Add activity transitions around permit/spacing waits, and a safe projection of `Waiting` from the active message to Agent Web status, including orange dot.
5. Add focused tests for the gate and state transitions; refresh current spec and codegraph as needed. Before final reviews move plan to `specifications/implemented/` on the task branch.
6. Independent code review and **independent runtime testing** on the exact candidate commit; report outcomes and pause for explicit owner merge/release approval. No main commits, merge, version or changelog update before approval.

## Acceptance criteria

- [ ] Agent options do not derive from or embed core integration options. No `MaxParallel` / `MinInterval` in agent configuration, policy or scheduling. `mezhs.yaml` is the only source.
- [ ] Provider-specific account/browser state and admission have one documented owning boundary; simultaneous clients of the same integration enforce the aggregate limit, including agent + ordinary chat across processes.
- [ ] `MaxParallel=1,2,0` and `MinInterval=0,5000` have deterministic tests at the owner boundary; no permit leaks on cancellation, exceptions, startup/shutdown/restart, and no duplicate prompt submit/replay.
- [ ] While a request is actually blocked on capacity/spacing, its running Agent shows Waiting/orange; on admission it becomes Running/green, with no stale waiting after failure, cancellation, completion, or another work phase.
- [ ] Other agent status colors and unread completion, selected header, accessibility, refresh and keyboard focus remain correct.
- [ ] Existing API Web, Agent Web, Electron account/guest flows, tests and standalone start behavior stay compatible (or documented approved migration).
- [ ] Concurrent test evidence distinguishes root execution overlap from actual provider-call overlap. Observed historical network errors are not casually attributed to concurrency.

## Verification plan

- Unit/contract tests: options validation and policy IDs; integration limits and contention; waiting activity transitions and cancellation before admission.
- Integration tests: Agent and ordinary-chat clients sharing the same connection and owner; 3+ request overlap at limit 2; spacing windows; process failure/restart; cross-chat isolation.
- Agent status tests: waiting/green transitions, cancellation/error/terminal precedence, stale message exclusion, unread completion, hover/keyboard text and reduced motion.
- Regression: `tests/browser-limits-config.ps1`, `tests/agent-architecture.ps1`, `tests/agent-api-smoke.ps1`, `tests/agent-status-unread-browser.test.cjs`, `tests/agent-provider-lifetime.ps1`, and related suites.
- Check live provider behavior separately and safely; no killing shared Electron sessions and no copying auth profiles.
- Independent plan review **before** development, independent code review, independent test; exact commit SHA and CI evidence recorded.

## Risks and exclusions

- The inter-process integration owner may require more coordination than an isolated options fix. Per `.agents/workflow.md`, stop for owner decision if central-host delegation becomes disproportionate to value.
- Retain command policy behavior, agent scheduler, existing SQLite execution history, renderer sandbox and transport auth rules.
- Do not expand into the unmerged typed HTTP migration in PR #57 or unrelated provider resiliency patches.
