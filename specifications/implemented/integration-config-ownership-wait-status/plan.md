# Integration configuration ownership and capacity-wait status

ID: integration-config-ownership-wait-status
Level: High-risk — configuration/DI boundary, integration admission activity, and Agent runtime status projection
Base: main (release-only); branch: integration-config-ownership-wait-status
Status: Implementation and review-driven corrections complete on the task branch; corrected developer checks PASS through `c3bd3ebb507ac0fcbaddf3fc284a4541f1c5160f`. Initial independent code review FAIL at `e56da443095701fd5ec9133998016dd2e3d27367`; fresh independent code review and independent testing pending. Owner-approved architecture and independent plan re-review PASS `exec_e1f6ef14c1b549ba910176d35d7150eb` (2026-11-15). No owner merge approval.

## Goal

Separate Agent and Core configuration abstractions. `mezhs.yaml` exclusively owns Core transport/storage and integration connections, including each connection's provider settings, `workspace`, `defaultModel`, `MaxParallel` and `MinInterval`. `agent.yaml` owns the Agent-specific loopback listener, Agent execution `workspace`, Agent persistence, message templates and policies. Policies select a Core `connectionId` and may set an optional `defaultModel` overriding that connection's default; an explicit execution model may override the policy model. Remove `AgentOptions : MezhsOptions` without duplicating integration configuration.

Show a truthful **Waiting** state and orange Agent status dot only while the active Agent model send is actually blocked on its Core integration's `MaxParallel` capacity or `MinInterval` start spacing. This is a global **effective Agent status**, not a stored execution status or a provider activity status. ChatGPT/Grok/Gemini activity states (including `waiting`, thinking and progress) can supply runtime message text but **must never determine Agent status**. Agent execution admission/scheduling is independent of model-send admission.

## Owner-confirmed architecture

- `Mezhs.Api` exposes the reusable Core DI, messaging, stores, integrations and endpoints. `Mezhs.Agent.Api` **composes** this Core API **in-process**, then adds Agent services/endpoints and Agent-specific behavior. Neither API inherits the other as a C# type.
- When Agent features are needed, Agent API is the active full API host, including the normal Core chat/message endpoints. Ordinary chat and Agent model sends in that host use **one** Core `IntegrationRegistry` and the existing `BrowserIntegrationBase` admission instance for each configured connection.
- Standalone Core API remains supported as an alternative host. Independently running Core and Agent API processes do **not** share an integration instance or aggregate permits; sharing live chat stores/browser profiles between such hosts is not part of this task. No distributed lock, cross-process admission service, new HTTP delegation, separate queue or remote prompt forwarding.
- `MaxParallel=0` remains unlimited and `MinInterval=0` means no start spacing **within the owning integration instance**, as already specified. No permit is held through Agent shell work.
- Admission-wait reporting is a dedicated transient signal emitted only by the existing `BrowserIntegrationBase` admission path, separate from `IntegrationActivity` / provider `ReportActivity`. It adds no gate, scheduler, database field or cross-process mechanism.

This replaces the superseded cross-process admission alternatives in the first plan. Reconsidering simultaneous independent hosts is a separate architecture task requiring its own owner decision.

## Verified baseline

- `src/Mezhs.Agent.Api/Program.cs` loads `agent.yaml` through `AgentConfigLoader.Load` and passes `AgentOptions` into `builder.AddMezhsApi(options)`.
- `AgentOptions : MezhsOptions` imports Core server/transport/storage/connections/extensions into the Agent configuration. `AgentConfigLoader` uses `MezhsConfigLoader.Load<AgentOptions>` and validates policy connection IDs against `options.Connections`.
- `src/Mezhs.Api/MezhsApi.cs` registers Core `MezhsOptions`, `ChatStore`, `FileStore`, `MessageService` and `IntegrationRegistry`; `AddMezhsApi` also sets the Core listener.
- `BrowserIntegrationBase` already limits each integration instance with its own capacity semaphore and monotonic start-spacing gate, but does not report admission waiting.
- `MessageService` marks the user message `Running` immediately before integration `SendMessageAsync`, and persists `IntegrationSendContext.ReportActivity` updates for provider activity. That activity may legitimately say `waiting` for provider-specific reasons; it is not evidence of admission blocking. `AgentWorker` sends model turns through that Core `MessageService` and retains/releases account conversation context around its execution.
- `AgentStore.GetLatestAgentStatuses` projects root execution status in one batched SQL query. `ChatStore` provides in-memory active-message activity; current `AgentChatView` has no effective Waiting status or message-to-execution association.
- Current `mezhs.yaml` sets `chatgpt-sub` to `maxParallel: 2`, `minInterval: 5000` and has the connection `workspace` but no `defaultModel`. Current `agent.yaml` duplicates Core connection declarations, including that connection's `defaultModel`, without limit values; those connection settings must migrate to `mezhs.yaml`.
- `ConfigPath.Find(args, "agent.yaml")` recognizes `--config`; test fixtures also currently embed Core connections/transport/storage in their Agent YAML.
- Observed four-agent test ended 2 Completed / 2 Failed (HTTP/2 retrieval error and browser-session timeout); a causal link to concurrency is unproven.

## Approach

### 1. Independent configuration contracts and existing Core composition

1. Keep `MezhsOptions` / `MezhsConfigLoader` as the sole owner of `mezhs.yaml` and its Core connection settings, including connection `workspace` / `defaultModel` / limits, plus transport/storage. Make `AgentOptions` a standalone Agent-specific type: listener, `agentStorage`, Agent execution `workspace`, messages and policies. Do **not** embed or inherit `MezhsOptions`, `ConnectionOptions` or integration limits.
2. Agent API loads **both** files independently. Preserve `--config <agent.yaml>`; define a distinct optional `--mezhs-config <mezhs.yaml>` for Core YAML, with the existing Core default-location lookup when omitted. Keep this path handling small and backward-compatible for standalone Core startup. Resolve relative paths from each file's own directory.
3. Preserve Agent API's **Agent-specific loopback listener** and its fail-closed validation even though `AddMezhsApi(coreOptions)` currently sets Core `server.listen`. Establish a minimal, explicit listener override/composition contract, without introducing another API host or broad Core API refactoring.
4. Register Core `MezhsOptions` and Agent `AgentOptions` independently in DI. Reuse existing Core `MessageService`, `ChatStore`, `FileStore`, `IntegrationRegistry`, provider/browser lifecycle and endpoint mapping **in the same Agent API process**. Agent services consume those Core services, never their own provider configuration.
5. Validate each policy's `connectionId` against the Core-loaded connection IDs at startup. Preserve model selection semantics: on a new policy-targeted Agent conversation, **explicit execution model > optional policy `defaultModel` > Core connection `defaultModel`**; preserve existing-chat model inheritance, explicit provider-default selection, and explicit connection overrides rather than silently applying a different connection's policy default. Reject removed Core-only sections (`connections`, `transport`, `storage`, Core `extensions`) in `agent.yaml` with an actionable migration error rather than silently ignoring them. Preserve policy `limits.commandTimeoutSeconds` (unrelated to integration admission).
6. Plan the later migration of the repository's `agent.yaml`, `mezhs.yaml`, Agent test YAML fixtures, startup scripts and configuration tests. Put connection `workspace` / `defaultModel` / limits only in Core YAML; keep the separate Agent execution `workspace` in Agent YAML. Test separate paths and expected listener/storage roots; do not duplicate connection definitions in Agent YAML.

### 2. Emit a dedicated integration-admission signal (never provider activity)

1. Keep the existing `BrowserIntegrationBase.SendMessageAsync` capacity semaphore and start-spacing gate as the **only** model-send admission owner. Do **not** add a gate in `AgentWorker`, `MessageService`, `BrowserAccountSession` or Electron.
2. Add a small typed, per-send admission callback/state to `IntegrationSendContext` (for example `Blocked` with capacity/spacing reason and `Admitted` / cleared). Only `BrowserIntegrationBase` reports this signal when its existing `MaxParallel` or `MinInterval` admission really blocks. Check immediate acquisition first; do not pulse Waiting on an uncontended semaphore, a zero/unlimited limit, ordinary provider thinking or unrelated work. Pending start spacing must be positive to count.
3. Have `MessageService` bind that signal to the **exact user message ID** and expose only lightweight, transient in-process admission-wait state for the Agent projection. Keep it separate from the existing provider `ReportActivity`, `StoredMessage.Activity` / `ActivityDetail` / `Analysis` and persisted activity history. No SQLite migration or new persistence. Clear the admission flag on admission and all send exit paths, including cancellation/error/completion, so stale waiting cannot survive a turn.
4. Provider-specific ChatGPT/Grok/Gemini activity states including `waiting`, thinking and progress remain available for **runtime message detail text only**. They must not set, clear or otherwise influence the admission flag or global Agent status; a provider may still report `waiting` after admission while the Agent correctly displays Running.
5. Preserve existing gate release, cancellation, provider lifecycle and no automatic replay after uncertain remote outcomes. The admission signal is observational; it does not acquire permits or hold them across Agent shell work.

### 3. Project effective Agent Waiting status without changing durable execution state

1. Keep root execution status `Running` in Agent SQLite and existing status precedence. Introduce a lightweight **execution-to-active-model-user-message association** around `AgentWorker`'s existing `SendWithReplyAsync(..., onSubmitted)` flow; clear it on completion, cancellation, error or transition to shell/reasoning. It must not mistake an unrelated ordinary chat message in the same ChatId for the Agent's own waiting turn. No persistent status migration or new global scheduler.
2. For an Agent chat, project effective `Waiting` only when **all** conditions hold: the chosen root execution status is `Running`; the associated user message belongs to that execution and is currently `Running`; and its **dedicated integration-admission-wait flag** is true. Never inspect provider `activity` / `activityDetail` (even when `activity == "waiting"`) to choose this status. `Queued`, `CancelRequested`, terminal and stale messages must not produce `Waiting`. On admission it projects `Running` again, even if provider activity still says `waiting`; shell and inter-turn work remain `Running`.
3. Feed the existing Agent chat list/detail effective-status projection from the batched root-status query plus cheap local snapshots/lookups; **do not poll `/runtime` per sidebar row** or append large analysis payloads. Keep selected-chat runtime activity available for detail text. Use the same effective status in sidebar and selected header; respect existing polling and refresh cadence.
4. Display orange Waiting dot and accessible label/hover/keyboard focus. Preserve blue unread-completion, green running/completed, paused, stopping, failed and cancelled precedence. Respect `prefers-reduced-motion`.

## Implementation sequence (only after independent plan re-review)

1. Update the config contracts/loader and Agent host startup; migrate sample and test YAML (including Core-only connection `defaultModel` / `workspace`) and update old inheritance assertions.
2. Verify Core and Agent hosts each start with their intended listener and Core config. Verify Agent policy references, capabilities, model selection, file paths and usual chat endpoints still work.
3. Add dedicated admission-state transitions at the existing gates in `BrowserIntegrationBase` and per-message transient wiring in `MessageService`, **without** changing provider activity handling; cover both wait reasons and cancellation.
4. Add execution/message association, effective Waiting projection and Agent Web status styling. Preserve existing storage schema, Agent scheduler and provider activity text.
5. Add deterministic tests, adjust applicable current specs and architecture docs, and refresh Codegraph if needed. Move completed plan to `specifications/implemented/` on this task branch before final code review/testing.
6. Obtain independent code review, **independent runtime testing** on the exact candidate commit, then pause for explicit owner merge/release approval. No direct changes to `main`; no version/changelog edits before release.

## Acceptance criteria

- [ ] `AgentOptions` neither derives from nor embeds Core integration configuration. Agent YAML has no connection definitions, provider metadata, integration `workspace` / `defaultModel` / `MaxParallel` / `MinInterval`. Core `mezhs.yaml` is their sole source; Agent YAML retains its **separate execution workspace** and Agent policy `connectionId` / optional `defaultModel`.
- [ ] Agent API continues to compose Core services/routes in-process; its own listener remains loopback-only. Core and Agent YAML load independently, with explicit optional Core config path and tested relative-path resolution.
- [ ] Ordinary and Agent model sends through the **same Agent API process** share the integration's configured capacity and start spacing; only the existing `BrowserIntegrationBase` gate reports admission waiting. No new cross-process infrastructure, status persistence or second admission owner.
- [ ] Existing `MaxParallel=1,2,0` and `MinInterval=0,5000` behavior remains correct, with deterministic tests of contention, cancellation/release and spacing; no duplicate submissions or automatic replay.
- [ ] An actually admission-blocked Agent model send displays effective global Waiting/orange; admission restores Running/green. Provider-reported `waiting`, thinking or progress alone **never** produces Waiting, and provider `waiting` remaining after admission **never** keeps Waiting. A queued message, unrelated chat message, shell command, cancellation, failure or completed execution cannot produce stale orange status.
- [ ] Sidebar and header agree. Existing unread blue, paused/stopping, terminal statuses, keyboard/hover labels, reduced-motion and efficient refresh remain correct.
- [ ] Explicit execution model > Agent policy optional `defaultModel` > Core connection `defaultModel` for new policy-targeted sends, with existing-chat inheritance/explicit provider-default and connection overrides preserved. Existing API Web and Agent Web routes, standalone Core/Agent startup, Electron account/guest flows, file staging, recovery and execution history continue to work.
- [ ] Concurrent test evidence distinguishes Agent root overlap from model-send overlap. Historical provider/network failures are not assigned a cause without evidence.

## Developer verification evidence (2026-11-15)

Development implementation through commit `c9550ef840edb776441af6c58c135dec65ba2679` on `integration-config-ownership-wait-status`:

- `dotnet build src/Mezhs.Agent.Api/Mezhs.Agent.Api.csproj -c Release` — PASS (zero warnings/errors).
- `dotnet build Mezhs.sln -c Release` — PASS (one existing WhatsApp C# hiding-member warning, npm audit notices).
- `tests/agent-architecture.ps1`, `tests/policy-decoder.ps1`, `tests/browser-admission-wait.ps1`, `tests/browser-limits-config.ps1`, `tests/agent-provider-lifetime.ps1`, `tests/integration-cancellation-contract.ps1` — PASS.
- `tests/agent-status-unread-browser.test.cjs` — PASS in Chromium, including Waiting/orange and unread behavior.
- `tests/agent-api-smoke.ps1` — PASS; Windows/.NET unhandled-exception process may linger despite writing the expected loopback rejection; test assertion verifies the rejection and absence of listener.
- Codegraph 3.0.0 export — PASS (262 tracked files and 694 edges); generated graph must be committed with the finalized plan move before independent review.

These are **developer** checks, not the required independent code review or independent testing. Admission-to-Agent projection, unusual races, actual same-host concurrent model sends and broader regressions remain important independent verification targets. Preserve owner merge approval boundary.

## Independent code review FAIL and branch remediation (2026-11-15)

- Independent static code review of exact candidate `e56da443095701fd5ec9133998016dd2e3d27367` returned **FAIL** (`exec_17284693d03745df8f2b500c4ade2c21`). Findings: (P1) no real Agent-host admission-to-effective-status projection regression test, (P2) no measured 5000 ms spacing assertion, (P2) `specifications/current/agent-runtime.md` missing the new behavior/configuration contract. No established production-code defect was reported.
- The task branch subsequently gained `tests/AgentAdmissionProbe.cs` and `tests/agent-admission-projection.ps1` (controlled real Agent host with an ordinary chat and Agent competing for the same BrowserIntegrationBase permit), a monotonic minimum-spacing assertion in `tests/browser-admission-wait.ps1`, and an updated current Agent runtime specification. These are review-driven test and documentation corrections, not a change to approved architecture.
- Follow-up correction `cba732d418c9cee4e804e89a2832a78a14c9eeac`: ensure the end-to-end test waits for the new Agent's own queued user message, rather than accepting an unrelated ordinary message in the same chat as evidence. Without the guard, the expected Queued assertion could target an already Running ordinary message.
- Follow-up correction `3d69da1e433f2eb5f4d00e359a449bfee4764485`: cover explicit provider Default (`model: ""`) overriding both policy and Core defaults, and retaining the choice in an existing Agent chat, in `tests/agent-api-smoke.ps1`.
- Additional test-harness hardening `42a718557d6854b40302ba0db1ed8605cc162f9b`: cleanup only deletes the test provider DLL if this run installed it; a pre-existing DLL is not overwritten or removed after a guarded startup failure.
- Additional model-precedence test `399c883dad41f5d0fa51b95b63ef90cf79ba8235`: cover Core connection `defaultModel` fallback when policy does not override it.
- Additional provider-failure test support and scenario `97d2d4bf57d50e5192b44bf3edc56c11673f105e` / `4da916e9717a4bea9f3629b475c2ef17398f5538`: a test-only provider can fail deliberately after admission; assert Waiting→Running→Failed with no stale Waiting.
- **Next gate:** developer checks have now passed on the corrected candidate. Regenerate/commit Codegraph, then obtain fresh independent code review at its exact final SHA; only after review PASS may independent runtime testing begin. No merge is authorized.

## Corrected-candidate developer verification (2026-11-15)

At task branch commit `c3bd3ebb507ac0fcbaddf3fc284a4541f1c5160f`, after the review-driven corrections:

- `tests/agent-admission-projection.ps1` — **PASS** on `c8740ccd00bcbd18cbab5d9a8b099935098905d6` (unchanged by the later Agent Web smoke assertion fix). Real isolated Agent API; test-only BrowserIntegrationBase provider; ordinary-chat/Agent shared capacity; exact execution/message identity; Waiting → Running → Completed and Waiting → Running → Failed; provider activity `waiting` does not govern effective status; queued unrelated same-chat message; cancel/terminal cleanup; start spacing; separately rooted YAML configurations. Test plugin creates no Electron sessions.
- `tests/browser-admission-wait.ps1` — **PASS**, including monotonic verification of 5000 ms minimum start spacing (4900 ms tolerance), capacity 1/2/0, cancellation and permit cleanup.
- `tests/agent-api-smoke.ps1` — **PASS**, including explicit provider Default (empty model), policy/Core model-default precedence, existing-chat inheritance, loopback-only API/Web and no listening socket after rejected non-loopback configs. The Agent Web negative-startup harness accepts the observed Windows/.NET error-written-but-process-lingering behavior, as already supported by the existing Agent API negative-startup harness.
- `tests/agent-architecture.ps1`, `tests/policy-decoder.ps1`, `tests/browser-limits-config.ps1`, `tests/agent-provider-lifetime.ps1`, `tests/integration-cancellation-contract.ps1` — **PASS**.
- `node tests/agent-status-unread-browser.test.cjs` — **PASS** in real Chromium on the corrected branch, testing effective status display and unread behavior.
- `dotnet build Mezhs.sln -c Release` — **PASS**, zero errors, one pre-existing WhatsApp member-hiding warning (CS0114).
- Verification used two isolated temporary worktrees, one for Chromium npm dependencies and one for clean .NET build outputs. A policy decoder build in the first worktree was blocked by DLL locks held by unidentified dotnet processes; the identical test **passed** in the fresh worktree without terminating those processes.
- Refresh portable Codegraph before the new review candidate; independent code review and independent runtime testing are **not yet PASS**. No merge into `main`, version or changelog changes.

## Verification plan

- **Config/DI:** independent deserialization, reject removed Agent Core sections, policy ID validation, `--config`/`--mezhs-config` semantics and relative roots, Core/Agent host bind addresses, missing/bad Core YAML, no second integration registry in Agent host. Verify integration `workspace` / `defaultModel` / limits exist only in Core YAML, while Agent execution `workspace` and policy `defaultModel` remain Agent-owned; verify model precedence, explicit provider-default, existing-chat inheritance and explicit connection override.
- **Admission:** deterministic fake browser integration with capacity 1/2/unlimited and spacing 0/5000 (use controlled time or bounded timing assertions), capacity and positive-spacing blocked/unblocked signals, no false immediate wait, cancellation before gate and during spacing, no leaked permits. Check admission signal is distinct from provider `ReportActivity` and remains non-persistent.
- **Message/Agent:** Agent and ordinary-chat sends against one hosted `IntegrationRegistry`; admission-blocked Agent Waiting→Running transitions and cleanup on cancellation/error/completion/shell phase; same-chat unrelated-message isolation and startup recovery. **Required counterexamples:** (a) provider reports `waiting` or thinking/progress with no admission block → Agent Running; (b) genuine admission block with no provider `waiting` → Agent Waiting; (c) provider still reports `waiting` after admission clears → Agent Running; (d) provider `waiting` before/during/after a capacity/spacing wait never controls the effective status.
- **UI:** sidebar/header Waiting and other status precedence, selected/unselected behavior, unread completion, hover/focus/accessibility and reduced motion.
- **Regression:** `tests/browser-limits-config.ps1`, `tests/agent-architecture.ps1`, `tests/agent-api-smoke.ps1`, `tests/agent-status-unread-browser.test.cjs`, `tests/agent-provider-lifetime.ps1`, `tests/integration-cancellation-contract.ps1` and relevant related suites.
- If a startup test runs standalone Core and Agent API at once, use **separate fixture storage and profiles**; it does not verify or imply cross-process capacity coordination. Do not kill shared live Electron sessions or copy auth profiles.
- Independent plan re-review before code, independent code review and independent runtime testing; record exact commits and check outcomes.

## Risks and exclusions

- The config split affects startup and tests that currently depend on inherited options and duplicated Agent YAML connections; focus changes there rather than introducing new host abstractions.
- Waiting projection must be tied to the actual Agent request and a **dedicated central admission signal**, not just a ChatId or any provider activity value (including `waiting`). Preserve cancellation and message terminal precedence; admission flag is transient and always cleared.
- Preserve Agent scheduling, shell policy, SQLite history, browser authorization, renderer sandbox and existing provider lifetime semantics.
- No cross-process admission coordination, shared browser-profile ownership, transport redesign in unmerged PR #57, unrelated provider resiliency work, version or changelog edits.
- If a new requirement forces extra gates, processes, persistent coordinator state, status persistence or broader forwarding, stop under `.agents/workflow.md`, inform the owner of the added complexity and obtain an explicit new decision before proceeding.
