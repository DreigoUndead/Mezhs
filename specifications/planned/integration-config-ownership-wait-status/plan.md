# Integration configuration ownership and capacity-wait status

ID: integration-config-ownership-wait-status
Level: High-risk — configuration/DI boundary, integration admission activity, and Agent runtime status projection
Base: main (release-only); branch: integration-config-ownership-wait-status
Status: Planning; revised to the owner's approved **single in-process Core composition** direction on 2026-11-14; independent plan re-review required before implementation

## Goal

Separate Agent and Core configuration abstractions. `mezhs.yaml` is the only source of integration connections, provider settings, transport and `MaxParallel` / `MinInterval`; `agent.yaml` owns Agent-specific listener, workspace, persistence, message templates and policies, which reference Core connection IDs. Remove `AgentOptions : MezhsOptions` without duplicating integration configuration.

Show a truthful **Waiting** state and orange Agent status dot only while the active Agent model request is actually blocked on its Core integration's capacity or minimum send interval. Agent execution admission/scheduling is independent of model-send admission.

## Owner-confirmed architecture

- `Mezhs.Api` exposes the reusable Core DI, messaging, stores, integrations and endpoints. `Mezhs.Agent.Api` **composes** this Core API **in-process**, then adds Agent services/endpoints and Agent-specific behavior. Neither API inherits the other as a C# type.
- When Agent features are needed, Agent API is the active full API host, including the normal Core chat/message endpoints. Ordinary chat and Agent model sends in that host use **one** Core `IntegrationRegistry` and the existing `BrowserIntegrationBase` admission instance for each configured connection.
- Standalone Core API remains supported as an alternative host. Independently running Core and Agent API processes do **not** share an integration instance or aggregate permits; sharing live chat stores/browser profiles between such hosts is not part of this task. No distributed lock, cross-process admission service, new HTTP delegation, separate queue or remote prompt forwarding.
- `MaxParallel=0` remains unlimited and `MinInterval=0` means no start spacing **within the owning integration instance**, as already specified. No permit is held through Agent shell work.

This replaces the superseded cross-process admission alternatives in the first plan. Reconsidering simultaneous independent hosts is a separate architecture task requiring its own owner decision.

## Verified baseline

- `src/Mezhs.Agent.Api/Program.cs` loads `agent.yaml` through `AgentConfigLoader.Load` and passes `AgentOptions` into `builder.AddMezhsApi(options)`.
- `AgentOptions : MezhsOptions` imports Core server/transport/storage/connections/extensions into the Agent configuration. `AgentConfigLoader` uses `MezhsConfigLoader.Load<AgentOptions>` and validates policy connection IDs against `options.Connections`.
- `src/Mezhs.Api/MezhsApi.cs` registers Core `MezhsOptions`, `ChatStore`, `FileStore`, `MessageService` and `IntegrationRegistry`; `AddMezhsApi` also sets the Core listener.
- `BrowserIntegrationBase` already limits each integration instance with its own capacity semaphore and monotonic start-spacing gate, but does not report admission waiting.
- `MessageService` marks the user message `Running` immediately before integration `SendMessageAsync`, and persists `IntegrationSendContext.ReportActivity` updates. `AgentWorker` sends model turns through that Core `MessageService` and retains/releases account conversation context around its execution.
- `AgentStore.GetLatestAgentStatuses` projects root execution status in one batched SQL query. `ChatStore` provides in-memory active-message activity; current `AgentChatView` has no effective Waiting status or message-to-execution association.
- Current `mezhs.yaml` sets `chatgpt-sub` to `maxParallel: 2`, `minInterval: 5000`. Current `agent.yaml` duplicates Core integration declarations without limit values.
- `ConfigPath.Find(args, "agent.yaml")` recognizes `--config`; test fixtures also currently embed Core connections/transport/storage in their Agent YAML.
- Observed four-agent test ended 2 Completed / 2 Failed (HTTP/2 retrieval error and browser-session timeout); a causal link to concurrency is unproven.

## Approach

### 1. Independent configuration contracts and existing Core composition

1. Keep `MezhsOptions` / `MezhsConfigLoader` as the Core owner of `mezhs.yaml` and its connections/limits/transport/storage. Make `AgentOptions` a standalone Agent-specific type: listener, `agentStorage`, workspace, messages and policies. Do **not** embed or inherit `MezhsOptions`, `ConnectionOptions` or integration limits.
2. Agent API loads **both** files independently. Preserve `--config <agent.yaml>`; define a distinct optional `--mezhs-config <mezhs.yaml>` for Core YAML, with the existing Core default-location lookup when omitted. Keep this path handling small and backward-compatible for standalone Core startup. Resolve relative paths from each file's own directory.
3. Preserve Agent API's **Agent-specific loopback listener** and its fail-closed validation even though `AddMezhsApi(coreOptions)` currently sets Core `server.listen`. Establish a minimal, explicit listener override/composition contract, without introducing another API host or broad Core API refactoring.
4. Register Core `MezhsOptions` and Agent `AgentOptions` independently in DI. Reuse existing Core `MessageService`, `ChatStore`, `FileStore`, `IntegrationRegistry`, provider/browser lifecycle and endpoint mapping **in the same Agent API process**. Agent services consume those Core services, never their own provider configuration.
5. Validate each policy's `connectionId` against the Core-loaded connection IDs at startup; preserve policy default models and per-execution connection/model overrides. Reject removed Core-only sections (`connections`, `transport`, `storage`, Core `extensions`) in `agent.yaml` with an actionable migration error rather than silently ignoring them. Preserve policy `limits.commandTimeoutSeconds` (unrelated to integration admission).
6. Migrate the repository's `agent.yaml`, Agent test YAML fixtures, startup scripts and configuration tests. Test separate paths and expected listener/storage roots; do not duplicate connection definitions in Agent YAML.

### 2. Report actual integration admission waiting

1. Keep the existing `BrowserIntegrationBase.SendMessageAsync` parallel and start-spacing gates; **do not add a gate in AgentWorker, MessageService, BrowserAccountSession or Electron**.
2. Through `context.ReportActivity`, emit machine-readable `waiting` with a human-readable capacity/spacing detail only if an admission wait is genuinely pending. Include contention on capacity and a pending start-spacing interval; avoid a false Waiting pulse for immediate acquisition.
3. Upon admission and before invoking `SendCoreAsync`, replace `waiting` with a non-wait state (for example `preparing`). Provider-specific subsequent activity continues to work. Cancellation/errors while waiting release capacity as current `finally` blocks guarantee; terminal message status supersedes old activity.
4. Admission remains local model-send lifetime only, with unchanged cancellation semantics and no automatic replay after uncertain remote outcomes.

### 3. Project effective Agent Waiting status without changing durable execution state

1. Keep root execution status `Running` in Agent SQLite and existing status precedence. Introduce a lightweight **execution-to-active-model-message association** around `AgentWorker`'s existing `SendWithReplyAsync(..., onSubmitted)` flow; clear it on completion, cancellation, error or transition to shell/reasoning. It must not mistake an unrelated ordinary chat message in the same ChatId for the Agent's own waiting turn. No persistent status migration or new global scheduler.
2. For an Agent chat, project effective `Waiting` only when: the chosen root execution status is `Running`; the associated user message belongs to that execution and is currently `Running`; and its activity is `waiting`. `Queued`, `CancelRequested`, terminal and stale messages must not produce `Waiting`. On admission it projects `Running` again; shell and inter-turn work remain `Running`.
3. Feed the existing Agent chat list/detail effective-status projection from the batched root-status query plus cheap local snapshots/lookups; **do not poll `/runtime` per sidebar row** or append large analysis payloads. Keep selected-chat runtime activity available for detail text. Use the same effective status in sidebar and selected header; respect existing polling and refresh cadence.
4. Display orange Waiting dot and accessible label/hover/keyboard focus. Preserve blue unread-completion, green running/completed, paused, stopping, failed and cancelled precedence. Respect `prefers-reduced-motion`.

## Implementation sequence (only after independent plan re-review)

1. Update the config contracts/loader and Agent host startup; migrate sample and test YAML and update old inheritance assertions.
2. Verify Core and Agent hosts each start with their intended listener and Core config. Verify Agent policy references, capabilities, model selection, file paths and usual chat endpoints still work.
3. Add precise admission activity transitions within `BrowserIntegrationBase`; cover both wait gates and cancellation.
4. Add execution/message association, effective Waiting projection and Agent Web status styling. Keep existing storage schema and Agent scheduler.
5. Add deterministic tests, adjust applicable current specs and architecture docs, and refresh Codegraph if needed. Move completed plan to `specifications/implemented/` on this task branch before final code review/testing.
6. Obtain independent code review, **independent runtime testing** on the exact candidate commit, then pause for explicit owner merge/release approval. No direct changes to `main`; no version/changelog edits before release.

## Acceptance criteria

- [ ] `AgentOptions` neither derives from nor embeds Core integration configuration. Agent YAML has no `connections`, provider metadata, `MaxParallel` or `MinInterval`. Core `mezhs.yaml` is the sole source; Agent policies may reference Core connection IDs.
- [ ] Agent API continues to compose Core services/routes in-process; its own listener remains loopback-only. Core and Agent YAML load independently, with explicit optional Core config path and tested relative-path resolution.
- [ ] Ordinary and Agent model sends through the **same Agent API process** share the integration's configured capacity and start spacing. No new cross-process infrastructure or second admission owner.
- [ ] Existing `MaxParallel=1,2,0` and `MinInterval=0,5000` behavior remains correct, with deterministic tests of contention, cancellation/release and spacing; no duplicate submissions or automatic replay.
- [ ] An actually blocked Agent model request displays Waiting/orange; admission restores Running/green. A queued message, unrelated chat message, shell command, cancellation, failure or completed execution cannot produce stale orange status.
- [ ] Sidebar and header agree. Existing unread blue, paused/stopping, terminal statuses, keyboard/hover labels, reduced-motion and efficient refresh remain correct.
- [ ] Existing API Web and Agent Web routes, core/agent standalone startup, Electron account/guest flows, file staging, model overrides, recovery and execution history continue to work.
- [ ] Concurrent test evidence distinguishes Agent root overlap from model-send overlap. Historical provider/network failures are not assigned a cause without evidence.

## Verification plan

- **Config/DI:** independent deserialization, reject removed Agent Core sections, policy ID validation, `--config`/`--mezhs-config` semantics and relative roots, Core/Agent host bind addresses, missing/bad Core YAML, no second integration registry in Agent host.
- **Admission:** deterministic fake browser integration with capacity 1/2/unlimited and spacing 0/5000 (use controlled time or bounded timing assertions), both wait reasons, no false immediate wait, cancellation before gate and during spacing, no leaked permits.
- **Message/Agent:** Agent and ordinary-chat sends against one hosted `IntegrationRegistry`; waiting-to-running transitions and exit from waiting on cancellation, error and shell phase; same-chat unrelated-message isolation and startup recovery.
- **UI:** sidebar/header Waiting and other status precedence, selected/unselected behavior, unread completion, hover/focus/accessibility and reduced motion.
- **Regression:** `tests/browser-limits-config.ps1`, `tests/agent-architecture.ps1`, `tests/agent-api-smoke.ps1`, `tests/agent-status-unread-browser.test.cjs`, `tests/agent-provider-lifetime.ps1`, `tests/integration-cancellation-contract.ps1` and relevant related suites.
- If a startup test runs standalone Core and Agent API at once, use **separate fixture storage and profiles**; it does not verify or imply cross-process capacity coordination. Do not kill shared live Electron sessions or copy auth profiles.
- Independent plan re-review before code, independent code review and independent runtime testing; record exact commits and check outcomes.

## Risks and exclusions

- The config split affects startup and tests that currently depend on inherited options and duplicated Agent YAML connections; focus changes there rather than introducing new host abstractions.
- Waiting projection must be tied to the actual Agent request, not just a ChatId or historical `activity` value. Preserve cancellation and message terminal precedence.
- Preserve Agent scheduling, shell policy, SQLite history, browser authorization, renderer sandbox and existing provider lifetime semantics.
- No cross-process admission coordination, shared browser-profile ownership, transport redesign in unmerged PR #57, unrelated provider resiliency work, version or changelog edits.
- If a new requirement forces extra processes, persistent coordinator state or broader forwarding, stop under `.agents/workflow.md` and obtain a new owner decision before adding complexity.
