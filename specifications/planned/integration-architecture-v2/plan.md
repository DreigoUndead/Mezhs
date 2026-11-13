# Integration architecture v2

ID: `integration-architecture-v2`

**Status:** Proposed plan; architecture discussed with the owner through Q35 and revised following owner review decisions (2026-11-13). **No implementation approval or merge approval implied.**  
**Branch:** `integration-architecture-v2`, created from current `main` after browser-conversation-parallelism was incorporated.  
**Classification:** **High-risk** — changes the integration contracts, message identity/persistence, Electron/auth boundaries, and Core/Agent/Web consumers. Requires **independent plan review before implementation**, then independent code review and independent testing. Follow `.agents/workflow.md` and `.agents/specification.md`.

## 1. Goal and scope

Replace the current ad hoc integration contract with small, strongly typed, reusable building blocks. Minimize duplicated transport/auth/model code; make provider-specific implementations express endpoints, DTOs, stream interpretation and necessary orchestration only. Keep concurrent chats independent, retain existing public behavior, and make integration capabilities difficult to misuse.

In scope:
- `IntegrationBase`, `ApiIntegrationBase`, `BrowserIntegrationBase`, `BrowserAccountIntegrationBase`, `LocalModelIntegrationBase`, and migration from current `ChatIntegrationBase`/modules.
- Chat-bound execution context, deterministic message identifiers, minimal prompt request/result, model handling, on-demand history.
- Typed HTTP endpoints/execution through transport composition (Electron-authorized HTTP or normal `HttpClient`).
- Shared login in `BrowserAccountIntegrationBase`; simple `AllowFiles` property; provider-specific bounded error/recovery hooks.
- Adapt all existing providers and Core/Agent/Web integration callers without losing existing features.
- Preserve the existing ChatGPT Guest, Gemini Guest and Grok Account operational behavior during this contract/transport refactor. Their DOM-driven paths may remain until the separately planned provider-protocol update.

Out of scope:
- New response-text streaming architecture, a generic provider-state persistence framework, mandatory provider `Create()` calls, universal feature flags, a generic HTTP inheritance layer, a generic scheduler, or an Electron lifecycle redesign.
- A speculative ChatGPT-specific shared base. Extract common ChatGPT request/response code **only when Guest and Account actually share it**.
- Eliminating provider DOM automation or rewriting ChatGPT Guest, Gemini Guest or Grok Account to private/semantic HTTP protocols. This is a **separate future update**, not an acceptance criterion or feasibility gate here.
- Designing generalized login/diagnostic behavior for future non-browser integrations; handle that when such integrations are implemented.
- Version/changelog changes, release, merge into `main`, and unrelated Agent or UI feature work.

## 2. Existing owners and dependency boundaries

**Start from current `main`, not an older singleton-window design.** The implemented `browser-conversation-parallelism` architecture and `docs/architecture.md` are the authoritative ownership baseline:

- One integration object per **configured connection**, as used by `IntegrationRegistry`. Different ChatIds run using independent chat-bound execution contexts. Never store a mutable, unqualified `_chatId` on the connection-scoped singleton.
- One shared Electron main process per browser connection, independent authenticated per-ChatId window/context, shared account authentication. Guest turns use fresh ephemeral sessions/windows while sharing the process.
- `BrowserIntegrationBase` is the **only** owner of browser model-send admission (`limits.maxParallel` and `limits.minInterval`). `MessageService` already serializes active sends within a local ChatId.
- `BrowserAccountSession` owns login/session synchronization, warm host, context retention/release, cancellation cleanup and idle behavior. `BrowserAccountIntegrationBase` must **reuse**, not duplicate, this implementation. Ordinary chat windows retire after local sends; Agent execution retains its selected context across model → shell/wait → model, then releases it.
- A cancelled local send may have unknown remote outcome. Cancellation affects only the local operation/context and never kills unrelated chat contexts; no implicit replay.
- MEŽS Core (`ChatStore`/`MessageService`) owns local chats, messages, file storage, remote continuation mapping and durable persistence. Integrations should not query SQLite directly.
- Agent continues to call the shared Core message API and retain/release contexts through the existing registry owner. Do not add another Agent-side browser/connection registry.

**Source inventory before implementation:** `src/Mezhs.Integration.Abstractions/IntegrationContracts.cs`; `src/Mezhs.Integration.Browser/BrowserIntegrationBase.cs`, `BrowserAccountSession.cs` and `BrowserDiagnosticsModule.cs`; `transports/Mezhs.Browser.Abstractions/IChatBrowserTransport.cs` and Electron transport; `src/Mezhs.Api/Services/IntegrationRegistry.cs`, `MessageService.cs`, `ChatStore.cs`, `src/Mezhs.Api/MezhsApi.cs`; all integration assemblies; Core/Agent/Web consumers and contract tests.

## 3. Intended hierarchy

~~~text
IntegrationBase
├── ApiIntegrationBase
├── BrowserIntegrationBase
│   ├── ChatGptGuestIntegration
│   ├── GeminiGuestIntegration
│   └── BrowserAccountIntegrationBase
│       ├── ChatGptAccountIntegration
│       ├── GrokAccountIntegration
│       └── other authenticated browser providers
└── LocalModelIntegrationBase
~~~

- Base classes are practical toolsets with enforced obligations where needed. Keep `ApiIntegrationBase` and `LocalModelIntegrationBase` as **minimal placeholders** until concrete providers require additional behavior; do not build speculative frameworks.
- Retain `BrowserAccountIntegrationBase` as the shared **browser authentication owner**. Guest does not inherit it.
- API integrations do not inherit browser or ChatGPT-specific classes.
- Shared typed HTTP lives behind **composition**, not `HttpIntegrationBase`.
- Any integration using Electron is a browser integration even when it uses only HTTP/JS and no rendered UI.
- Existing `ChatGptAccountIntegration : ChatGptWebIntegration` inheritance is removed. Guest and Account only share code where their actual protocol overlaps.

## 4. Proposed contracts (illustrative, refine exact signatures in implementation)

These names/signatures convey the approved **semantics**, not a requirement to create additional interfaces or duplicate an already existing contract.

~~~csharp
public abstract class IntegrationBase : IAsyncDisposable
{
    protected IntegrationBase(IntegrationConnection connection, IIntegrationHost host);

    public IntegrationConnection Connection { get; }
    public virtual bool AllowFiles => false;

    // Outgoing user message: determine ID before Core persists it.
    public virtual string CreateMessageId();

    public abstract Task<PromptResult> PromptAsync(
        ChatContext chat, PromptRequest request, CancellationToken ct = default);

    // No feature-discovery boolean/module needed.
    public virtual Task<IReadOnlyList<IntegrationModel>> GetModelsAsync(
        CancellationToken ct = default); // exactly one "default" model

    protected virtual Task SetModelAsync(
        ChatContext chat, string model, CancellationToken ct = default);
        // default model => no-op; unsupported non-default => error

    public virtual Task LoginAsync(CancellationToken ct = default);
    public virtual Task OpenBrowserAsync(CancellationToken ct = default);
    public virtual Task<JsonElement> DiagnoseAsync(
        string operation, JsonElement arguments, CancellationToken ct = default);

    protected virtual Task<ErrorAction> HandleErrorAsync(
        IntegrationError error, CancellationToken ct = default);

    public virtual ValueTask DisposeAsync();
}

public sealed class ChatContext
{
    public string Id { get; }             // permanent MEŽS chat ID
    public string? ProviderId { get; }     // provider conversation ID, per chat+connection
    public string ConnectionId { get; }

    public Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(
        int? limit = null,
        int? offset = null,
        bool fromStart = true,
        CancellationToken ct = default);
}

public sealed record PromptRequest(
    string MessageId,
    string Text,
    string? Model,
    IReadOnlyList<InputFile> Files);

public sealed record PromptResult(
    string Text,
    string MessageId,
    string Model,
    string? ProviderId = null,
    IReadOnlyList<OutputFile>? Files = null);
~~~

Contract details:
- `ChatContext` is independent **per operation/chat**; no separate integration object per prompt and no second Electron/session manager. Model/account configuration belongs to the connection; per-turn state belongs to the execution context.
- `GetHistoryAsync` includes messages from **all providers in the MEŽS chat**. Nullable limit/offset; `fromStart=true` selects from oldest, `false` from newest. Return selected history **chronologically** in either case. More filters (role/provider/etc.) only when needed.
- `CreateMessageId()` is called **before** persisting an outgoing user message. Default MEŽS-generated; provider integrations override for provider-compatible client IDs. No ID changes after persistence.
- Each integration declares/implements a **deterministic** reply-ID policy: require the provider message ID where that protocol supplies one, otherwise deliberately generate one. No runtime “missing ID, silently fallback” policy. Avoid introducing a separate policy enum unless code enforcement genuinely needs one.
- `PromptResult.MessageId` is the assistant reply ID. There is no second outgoing ID in the result: Core already knows the submitted user ID.
- `ChatContext.Id` never changes after creation. `ProviderId` is a provider's **conversation** ID, maintained per **chat + connection**; it can be returned on first send and changed on a documented remote conversation restoration. No renaming Core IDs.
- Model choice is **per request**. Provider-specific `SetModelAsync` is invoked only as part of the correct chat's prompt execution, not as a racy connection-global selection mechanism. `GetModelsAsync` defaults to `[new IntegrationModel("default","Default")]` and should not fail solely for lack of model-selection support.
- `PromptResult.Model` is the integration's best available model identity: use the provider-reported executed model when available; otherwise the current model or selected/requested model, as appropriate to that integration. Missing executed-model metadata is **not** a contract failure. If the returned model differs from the requested selection, warn the user; do not retry or change the UI selection automatically. Do not claim the provider verified execution if the value is only current/selected.
- Preserve existing progress/activity callbacks and complete-result behavior. **No new incremental-text streaming or observable-based return type.**
- No generic `ProviderState`, versioned JSON blob, generic persistent key/value store, or universal `Create()`. Provider creates/obtains remote conversation on first prompt. Keep only concrete continuation identifiers required by real protocols.
- Preserve existing chat deep-link/`RemoteChatUrl` functionality through simple derivation or an on-demand provider lookup where possible; do not silently drop UI behavior because the illustrative `PromptResult` omits a URL.
- `AllowFiles` is a single virtual bool on `IntegrationBase`: `false` disables input attachments; `true` allows submission to the provider. Unsupported type/size errors propagate normally; MEŽS security/size/storage validation stays. Output files need no capability flag, and returned links/IDs can be resolved/downloaded by the integration before Core import.
- Remove `IntegrationCapabilities` and the delegating `ILoginModule`, `IModelModule`, `IIntegrationDiagnosticsModule` wrappers from the new browser integration path. Login/diagnostics/models use direct virtual/base-class methods. Browser login availability is indicated by inheritance from `BrowserAccountIntegrationBase` (no `RequiresLogin` property); retain existing localhost/diagnostics access safeguards. Do not invent `SupportsModels` etc. Only mechanically adapt current mock test fixtures where necessary to keep tests/builds usable; generalized non-browser login/diagnostics design is a separate future task.
- `IntegrationError`/`ErrorAction` are small implementation-level concepts for virtual `HandleErrorAsync`. Provide exception, operation, attempt, and whether submission may have occurred; bounded recovery only. Shared runtime must never blindly replay an uncertain submitted prompt.
- Keep `IntegrationRegistry` as connection-scoped owner and retain/dispose behavior. Remove duplicated wrappers/adapters as replacements become stable; avoid parallel legacy and new processing frameworks.

### Provider continuation and legacy persistence

**Do not blindly delete existing `RemoteParentMessageId` or alter old `ChatStore` records.** Current ChatGPT continuation uses the latest assistant message ID as the provider parent, whereas existing local assistant IDs are generated independently. Migrate/reconcile existing chats and provider ancestry so that old conversations still continue or restore safely after deploying the new ID policy. Keep a narrow compatibility mapping if necessary; do not replace it with generic `ProviderState`. Validate behavior across restarts, alternate connections, queued messages, replay and chat deletion.

If a provider needs additional durable state beyond the agreed `Id`, `ProviderId` and message lineage, document a real case and obtain owner approval before extending the contract.

## 5. Browser/API transport and typed HTTP endpoints

Introduce a **single reusable HTTP execution mechanism** used through composition by both API and browser bases. Define strongly typed, static, stateless endpoint descriptors:

~~~csharp
public static class ChatGptEndpoints
{
    public static readonly HttpEndpoint<SendRequest, SendResponse> Send = ...;
    public static readonly HttpEndpoint<SetModelRequest, SetModelResponse> SetModel = ...;
    public static readonly HttpEndpoint<EmptyRequest, ModelsResponse> Models = ...;
}

// API: HttpClient-backed executor. Browser: bound Electron executor.
var reply = await browser.Http.CallAsync(ChatGptEndpoints.Send, request, ct);
~~~

- Descriptors bind method, route, query, typed request/response DTO, serialization and response interpretation; support JSON, ordinary HTTP and streaming/SSE/NDJSON where actually needed. For paths migrated to typed HTTP, keep their provider response interpretation in C# rather than duplicating it in JavaScript. Existing provider JS/DOM flows not yet migrated remain operational. Preserve existing streaming **activity updates** without a new public result-stream contract.
- Electron owns cookie/session/authenticated HTTP execution and browser-generated anti-abuse/security requirements (e.g. real ChatGPT Sentinel), invoked through generic transport operations. C# owns typed endpoint definitions and provider orchestration. No secret copying into endpoint descriptors, fake security tokens, unsafe IPC or renderer sandbox exceptions.
- `BrowserIntegrationBase` exposes a narrow per-chat browser operation and JS hooks (attach/execute when needed); leverage existing `ForContext`, `BrowserAccountSession`, host and diagnostics rather than a second context abstraction. Keep limits and lifecycle enforced centrally. Browser code may use JavaScript for protocol mechanics and diagnostics.
- Shared HTTP executor has explicit **safe replay semantics** per operation; mutations/prompt sends are non-replayable after ambiguous submission by default. Provider-specific `HandleErrorAsync` may recover only within those bounds. No large error taxonomy or exactly-once delivery framework.
- Account and guest protocol-specific code stays separate except for **proved** shared DTOs/builders/parsers. Do not force unrelated official APIs into shared private-web code.
- **Required early transport feasibility test:** before locking in endpoint/executor design or broad migration, implement and verify one end-to-end typed endpoint call over the existing Electron account session, exercising authenticated execution, error propagation, cancellation and streaming/progress where relevant. Ensure this can reuse native security requirements without new session/host ownership or duplicated protocol logic. If it cannot, stop and present simpler alternatives to the owner before continuing the broad refactor.

### Deferred operational DOM replacement

The existing DOM-based ChatGPT Guest, Gemini Guest and Grok Account workflows are **allowed to remain** for this architecture update. Their private-API/protocol migration, including any feasibility research and no-DOM enforcement, belongs to a **separate future task**. This refactor must keep those providers functional without introducing new DOM dependencies or accidentally treating their current behavior as a blocker to the new contracts. ChatGPT Account's already implemented HTTP-backed protocol should be reused where possible; do not rewrite unaffected provider protocol mechanics simply to satisfy the new hierarchy.

## 6. Implementation sequence (on the same branch)

1. **Impact inventory and transport proof**: locate old modules, capabilities, transport methods, IDs, API DTOs and persisted remote state; map migration impact. Before wide changes, demonstrate one authenticated typed HTTP endpoint end-to-end over the existing Electron transport, including safe cancellation/error handling and streaming/progress. Stop for owner decision if this requires a second session owner, duplicated protocol stack or unsafe security changes.
2. **Contracts and Core migration**: implement base types (including lightweight API/local-model placeholders), chat execution context/history retrieval, deterministic user/reply IDs, per-chat+connection `ProviderId` and legacy remote-parent continuation compatibility. Preserve ChatStore relationships, public endpoints and Agent history/replay semantics; add focused tests.
3. **Typed HTTP transport**: endpoint DTOs/descriptors and a single HTTP executor through composition; adapters for ordinary `HttpClient` and Electron-authenticated HTTP/streaming based on the proven transport boundary. Keep browser protocol/security hooks and activity reporting; enforce safe replay.
4. **Browser bases and existing providers**: migrate `BrowserAccountIntegrationBase` using the existing account session; adapt registry/auth/models/diagnostics and split ChatGPT Guest/Account inheritance. Keep ChatGPT Guest, Gemini Guest and Grok Account DOM workflows functioning unchanged except necessary contract adaptation; do **not** attempt their private-API rewrites. Mechanically adjust mock fixtures only as required for build/tests; defer generalized non-browser login/diagnostics.
5. **Core/Agent/Web callers**: migrate upload validation to `AllowFiles`, models to direct virtual methods and default model, browser authentication to account base, reply identity/model reporting, downloaded files, diagnostics, chat links and Agent attachments.
6. **Cleanup/verification**: remove superseded browser contracts and wrappers where replacements are stable; only remove JavaScript endpoint/parsing logic actually superseded by the typed transport. Align `docs/architecture.md`, `specifications/current/` and generated Codegraph. Move the final plan from `planned/` to `implemented/` on this branch **before independent code review/testing**. No VERSION/CHANGELOG edits before release.

**Complexity stop rule:** if source reality requires duplicate session managers, per-provider persistence frameworks, global model locks, unbounded retry coordination, a second HTTP protocol stack or unsafe transport/security workarounds, stop and present the smallest alternatives to the owner per `.agents/workflow.md`. Operational DOM removal is outside this task and is not a reason to block this refactor.

## 7. Acceptance criteria

- [ ] One connection-scoped integration and independent immutable chat contexts; existing parallelism limits, per-ChatId send ordering, shared authenticated host, Agent context retention/release, guest ephemerality and cancellation isolation all remain correct.
- [ ] Browser Account login flows use `BrowserAccountIntegrationBase` and existing auth owner; Guest never requires account login; browser security and diagnostics checks remain intact.
- [ ] Existing ChatGPT Guest, Gemini Guest and Grok Account DOM workflows remain functional through the contract migration; DOM removal/private-API rewrites are explicitly deferred to a separate task.
- [ ] All active integrations build and support their existing operations without newly duplicated provider boilerplate.
- [ ] One authenticated typed Electron endpoint is proven end-to-end **before** broad transport migration; typed API/browser calls then share semantics, headers/auth remain session-owned, cancellation/progress and bounded safe retry work, with **no duplicate prompt** on uncertain submission.
- [ ] MEŽS chat `Id` stable; `ProviderId` persisted per chat+connection; provider-compatible user IDs generated before persistence; returned reply IDs follow deterministic policy. Existing chat history, reply ancestry and legacy remote continuation remain accessible after restart.
- [ ] On-demand history returns cross-provider messages, nullable limit/offset, both `fromStart` directions and chronologically ordered slices; no forced history payload on every prompt.
- [ ] `GetModelsAsync` defaults to one `Default`; explicit model selection per prompt; integration reports provider-executed model when known, otherwise its current/selected model; UI warns on returned mismatch without silently changing selection; concurrent chat model/effort behavior live-tested where possible.
- [ ] `AllowFiles` is the sole input attachment capability; accepted files are passed through, provider-type errors displayed normally; provider output files and links import/render correctly. No input/output image flag matrix.
- [ ] Existing progress/activity and complete prompt result unchanged in observable behavior; no new public text streaming framework.
- [ ] No `ProviderState`, universal `Create()`, `HttpIntegrationBase`, redundant capability booleans/modules for new browser integrations, separate connection scheduler or undocumented second state owner. API/local-model bases stay lightweight placeholders.
- [ ] Tests and affected behavior specs updated and independent review/testing completed before owner merge approval.

## 8. Verification and review

**Developer checks (after implementation):**
- `dotnet build Mezhs.sln -c Release` and relevant .NET tests.
- Targeted Core/Agent/API/Web tests for chat CRUD, history slicing, model selection/validation, uploads, cancellations, message IDs, migration and SQLite persistence.
- Node/Electron integration, IPC sandbox/authorization, authenticated session, guest isolation, context retention/release, browser limits and diagnostic tests, including `tests/integration-cancellation-contract.ps1`, `tests/agent-provider-lifetime.ps1` and actual per-context Electron tests.
- Provider packaging checks (`node --check` for browser modules, `node --test tests/*.test.js`, `tests/integration-resources.ps1` where applicable).
- Typed HTTP tests using simulated transports: GET/POST/PATCH, typed body/query/response, streams/activity, cancelled requests, auth failures, explicit safe replay boundaries and model metadata.
- Live provider validation when credentials/terms permit: ChatGPT Account typed/private-API call, existing ChatGPT Guest/Grok/Gemini workflows (without protocol rewrites), two simultaneous account chats with different model choices, agent model → shell → model, generated file download links and unavailable remote-conversation recovery. Do not require no-DOM behavior in this update.
- Record command results, mock versus live evidence, and remaining limitations; do not claim tests before running them.

**Required stage order (High-risk):** commit plan on branch → **independent plan review** and owner resolution of meaningful findings → implementation on same branch → final plan relocation/current spec updates → **independent code review** → **independent testing** → owner explicitly approves merge/release. No merge, tagging or version bump at planning stage.

## 9. Risks and preimplementation questions for reviewers

1. **Existing ID scheme:** old assistant records have independent Core IDs and remote ChatGPT parent IDs; a direct field deletion may break continuation. Favor the smallest migration/compatibility mapping grounded in stored data.
2. **Deferred provider DOM work:** ChatGPT Guest, Gemini Guest and Grok Account still depend on DOM operation; preserve their behavior, and handle protocol feasibility and DOM removal in a separate task.
3. **Model reporting:** some providers (e.g. Grok) do not return executed-model metadata. The integration chooses the best available executed/current/selected model; do not require new protocol discovery or claim an inferred model was independently verified.
4. **Shared Electron auth and transport feasibility:** typed C# HTTP invocation must pass an early end-to-end proof and must not bypass native auth/security requirements or introduce parallel host/session machinery.
5. **Existing link/output/progress features:** a minimal proposed result must not regress provider conversation links, files or activity UI. Prefer minimal provider methods over new generalized state.
6. **Scope:** this touches many assemblies. Deliver in small reviewed changes on the same task branch and escalate if preserving an unrealistic guarantee causes disproportionate complexity.

**Planning-only artifact.** This revision documents owner decisions on deferred DOM work, early Electron typed-HTTP feasibility, provider-selected model fallback, simple history, deferred non-browser behavior and placeholder API/local bases. No source, migrations, tests or runtime behavior are changed by this commit.
