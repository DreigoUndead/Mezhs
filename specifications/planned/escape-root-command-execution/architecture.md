# MEŽS rich-tag pipeline — target architecture

Status: **design guideline / Development input**, not executable pseudocode.
Parent plan: [plan.md](plan.md). Branch: `escape-root-command-execution`.
Recorded: 2026-11-24. The task plan's active decisions are authoritative; verify the unmerged implementation against the actual source and tested SHA.

## Intent and boundaries

Replace the current partially duplicated tag-discovery mechanisms with **one implementation of root-level structural tag matching** reused for nested scopes. The owner-provided `Tag`/`TagContent` examples define responsibilities, not concrete C# signatures, exact fields or React filenames. Developer may adapt types/functions to fit the current Markdig, rich-UI, Agent and Web architecture. No parallel command parser, JS execution engine, DOM-sourced parsing or model-controlled dynamic React imports.

**One implementation of finding tags** does *not* require exactly one traversal of source bytes. It is acceptable to call that implementation on another bounded substring (e.g. root `escape`), provided no other code independently implements tag nesting and eligibility decisions. Distinguish tokenizer, structural matching, semantic validation and rendering without duplicating the structural logic.

Parsing only discovers/normalizes data and builds host-only command candidates. The Agent Interpreter validates the complete command batch against the authoritative Agent Registry/policy and executes commands afterward. React renders data using host-owned components, and can trigger only explicitly authorized user actions.

## Current implementation and seams

- `src/Mezhs.Common/RichUi/RichDocumentParser.cs`: owns shared structural tag matching, source spans, data-only rich projection and opaque editor boundaries; command candidates remain host-only.
- `src/Mezhs.Common/RichUi/RichDocument.cs`: versioned `RichDocument`/`RichNode`/`RichCommandCandidate` DTOs; `RichTagRegistry.cs` owns tag definitions/catalog.
- `src/Mezhs.Common/RichUi/RichMarkdownNodes.cs` and `RichQuestionGrammar.cs`: Markdown lowering and limited question grammar. SVG markup validation now belongs to `src/Mezhs.Web.Lib/src/rich-elements/Svg.tsx`.
- `src/Mezhs.Agent.Api/Commands/CommandBatchAdapter.cs`, `Interpreter.cs`, `Registry.cs`: command candidate adapter, batch validation, policy/host execution; keep this boundary.
- `src/Mezhs.Api.Contracts/ApiContracts.cs`, `Mezhs.Api/Services/MessageService.cs`, `ChatService.cs`: completed assistant messages currently include a validated `Document`. Agent API also transports `RichDocument`.
- `src/Mezhs.Web.Lib/src/RichDocumentView.tsx`, `MarkdownContent.tsx`, `rich-elements/*`: typed frontend document with known component map. `RichActionContext.ts` and `rich-elements/Question.tsx` already support limited local state and a host-authorized new-turn action.
- Tests: `tests/Mezhs.RichUi.TestApp/Program.cs`, `tests/rich-model-ui-rendering.test.cjs`, `tests/agent-protocol-result.ps1`, `tests/agent-web-shared-ui.ps1`, relevant Agent architecture/safety fixtures. Existing tests contain both older and now superseded ancestry expectations; update them deliberately.

## Parent-defined child policy (2026-11-24 owner correction)

The implemented tag contract supersedes the earlier conceptual `SearchNested`
and `AllowAsNested` fields below. A `RichTagDefinition.SupportedChildren`
set defines how children are admitted: `null` means ordinary registered UI
children, an empty set means opaque/no MEŽS children, and explicit registered
tag IDs mean only the listed MEŽS children. Nested-only variants set
`AtRoot=false` and can use a distinct `Id` for surface-name collisions.
The shared structural finder still recognizes complete excluded wrappers,
without promoting commands inside them.

**SVG exception (owner-approved 2026-11-24):** `svg` is one registered
`RawEditor` with `SupportedChildren=empty`. It carries its bounded root
attributes and opaque body as `RichNode(kind=raw, name=svg)`; there are no
C# `g`/`rect`/`text` SVG sub-tag definitions. The trusted `Svg.tsx`
decodes a finite XML/SVG subset, validates element and attribute allowlists,
nesting, value and resource bounds, and constructs React geometry without
HTML insertion or external references. Invalid SVG renders inert diagnostics.
The generic C# opaque-boundary matcher skips quoted child attributes/comments
and nested same-name tags when locating the outer closing delimiter; it
never discovers executable commands inside SVG.

The Agent `CommandBatchAdapter` projects host candidates/visible content; it
does not tokenize the source. Core uses the UI catalog, and Agent extends it
immutably with host-only command definitions. The React frontend retains its
separate statically imported, trusted component registry under
`rich-elements/Registry.ts`, so a UI component does not change the generic
`RichDocumentView`. This explicitly preserves the frontend import/DOM
security boundary; build-time automatic file discovery was not introduced.

## Tag registry and parsed shape

The source-level contract is defined in `RichTagRegistry.cs`:

- `RichTagDefinition` declares name, role, permitted attributes, `SupportedChildren`, root/nested eligibility, inline/interactive metadata and an optional registered `ProcessContent` callback.
- `RichTagContent` supplies validated data, original source/body spans, parent context, self-closing and root flags to handlers; handlers may emit candidates but never execute host side effects.
- `RichTagCatalog` is immutable per host. Core uses `RichTagRegistry.Ui`; Agent composes its own commands from `Mezhs.Agent.Api/Commands/Registry.cs`. This keeps Agent capabilities out of Core.
- `SupportedChildren`: null permits ordinary registered UI descendants; empty forbids MEŽS child processing (including opaque bodies); an explicit registered ID set restricts descendants. Nested-only variants use `AtRoot=false`. Avoid new generic parser name-specific exceptions when extending the catalog.
- The frontend `rich-elements/Registry.ts` statically binds approved tag symbols to locally imported TSX components and raw-editor factories. No model-controlled import paths, arbitrary JSX or dynamic host callbacks.

`RichDocument` remains versioned serialized data (`version`, `valid`, `nodes`, `diagnostics`) with source spans and inert, bounded attributes. Private `Parent`/handler references and host-only command candidates must never be serialized to React. Preserve existing safe literal data representation and streaming fallback; change the protocol only if a justified, synchronized migration is approved.

Architectural acceptance includes a **one-more-instance exercise**: an ordinary UI tag changes only its C# definition, TSX implementation and trusted frontend binding (plus optional handler); an Agent command changes Agent registration/handler rather than the generic parser; an embedded language can remain opaque to MEŽS and be safely decoded by its owner. Review actual code and caller paths, not only test outcomes.

## Discovery and processing

1. `FindRootTags` is the shared structural matcher for normal parsing and bounded root-`escape` command discovery. Structural matching includes known and unknown wrappers, parent relationships, paired delimiters, self-closing tags and markers, even when a tag is not eligible for display/execution. Filtering only affects returned tags, never ancestry.
2. `RenderChildren` consumes the located tree, applies `RichTagCatalog` parent eligibility (`SupportedChildren`, root/nested rules), produces Markdown/rich nodes and invokes the **single** `DispatchTag` for validated registered handlers. There is no separate public `FindTags` or `SearchNested` mechanism.
3. Root-only executable tags are registered by the Agent host, never by Core. Their callbacks collect candidates with original spans and byte-exact bodies. The Agent command adapter and Interpreter authorize the batch and perform side effects only after full validation.
4. `escape` is a registered opaque/literal MEŽS control. Its callback dispatches only **direct registered root-only handler tags** when the escape itself is at document root, using the same bounded structural finder. It does not name `SH`, `ATTACH` or `DONE` specifically. Nested escapes cannot promote commands. Non-command escape content remains muted literal display, omitting collected command spans.
5. Other known opaque editors, SVG raw content and unknown/ineligible wrappers preserve nested command inertness. For raw editors, the C# structural matcher finds an outer boundary without interpreting the editor's embedded language. Embedded SVG geometry/attributes are validated and rendered only by trusted `Svg.tsx`; unsupported input is visibly inert.
6. Preserve existing incomplete-opener behavior, source order, quotas, final/unique `DONE`, Markdown code-fence behavior and fail-closed invalid batches with **no partial side effects**. Root-command eligibility must not be expanded by malformed closers or skipped wrapper ancestry.

**Current owner-authoritative command matrix** (applies to registered executable root-only tags, not shell alone):

| Structure | Collect for execution? |
| --- | --- |
| `SH` / `ATTACH` / `DONE` at document root | Yes |
| `escape -> SH` with escape at root | Yes |
| `box -> SH` | No |
| `box -> escape -> SH` | No |
| `escape -> box -> SH` | No |
| `escape -> escape -> SH` | No |
| `CodeBlock -> escape -> SH` | No |
| `Widget (unknown) -> escape -> SH` | No |
| `svg -> escape -> SH` | No |
| `escape -> Widget -> SH` | No |

A command-like sequence directly in Markdown code fences, inline code, quotes or comments remains root-level eligible under the **existing** protocol; do not silently change this rule while refactoring the structural parser. Unsupported/malformed controls remain inert and preserve non-command source as currently specified. Revisit a code-fence behavior only via an explicit separate owner decision.

## Rich document and React transport

Keep a serialized `RichDocument { version, valid, nodes, diagnostics }`. A `RichNode` carries a stable ID, source span, kind, validated `attributes`, optional `text`, `name` and `children`; commands and private parsing fields (`Parent`, handlers, callable code, authorization state) **must not** appear in JSON.

Current API `ApiMessage.Document` / `ApiChatHistoryMessage.Document` already carries this document to both chat UIs. `MarkdownContent` uses `RichDocumentView` and shows source verbatim before a completed/validated document exists. Preserve that streaming/legacy fallback and the versioned payload contract. Existing Markdown normalization remains the sole interpretation layer for Markdown; do not parse arbitrary assistant JSX, execute JS or add a second React-side tag grammar.

React uses a **static registry of imported host-owned components** keyed by symbolic name. The renderer looks up the validated key and maps only approved params into component props; do not use assistant-provided URLs/import paths or arbitrary DOM prop spread. Reuse current `rich-elements/Style.ts` bounded decoding for spacing, border, colors, layout, links, and the current `json:` literal value protocol unless a demonstrably necessary change is approved. Preserve safe SVG and unknown diagnostic fallback. `escape` displays muted literal source with executable spans omitted. Raw editors render raw inert content. Host-only commands do not mount as React controls.

Interactive components may own local React state (`Question.tsx` already does); host actions flow through `RichActionContext` or another explicitly approved host capability and require real user interaction. Model-provided attributes never become JS functions or privileged callbacks. If a richer generic action system would be needed, **leave it outside this refactor** unless already supported by the current safe finite grammar; do not infer permission from `Tag.Function`. Keep Agent transcript actions read-only where already enforced.

## Correctness, containment and performance

- Full paired matching for nested same-name containers, unknown tags, opaque editors, SVG, escapes and command bodies. A fake parent closer inside an opaque command body cannot escape its structural parent.
- Mixed direct commands and prose retain original order, exact bodies, source spans and display projection; no duplicated command execution or double-counting during normalization.
- Preserve bounded message length, tag/attribute count, nested depth, node budget, SVG budget, safe URL and attribute policies; fail closed on untrusted input with no partially authorized batch.
- No competing **MEŽS tag** parser, multiple implementations of structural matching, hard-coded executable tag names in escape processing or frontend-originated command execution. The bounded SVG-only TSX decoder is intentionally separate: it interprets a distinct embedded language and cannot grant MEŽS/Agent capabilities. Reusing the same C# finder on a bounded escape body is allowed.
- Avoid unnecessary typed-parameter/schema rewrite, additional abstractions, network protocols, locks, queues, dynamic imports or component redesign. Retain proven behavior of ordinary Markdown, radio question grammar, visual unknowns and raw editors.
- Since source is untrusted model output, maintain strict data-only serialization and correct distinction between visible markup and host-only executable candidates.

## Development sequence and migration

1. Inspect current source and all relevant regression expectations. Decide the smallest compatible `TagDefinition` and `TagContent` forms, identify the shared matcher and the one dispatcher. Establish a baseline on the task worktree.
2. Implement a reusable first-level finder with complete structural boundaries, source offsets and registry filters; add root/nested matching tests first. Replace `ReadChildren`/`ScanOpaqueContent` structural duplication and redundant close matching as a single cohesive migration, not a parallel indefinite codepath.
3. Connect recursion, `Parent`, root-only escape handler and shared command candidate collector; align tests to the latest owner-approved matrix. Keep batch validation and policy untouched and prove invalid replies do not cause side effects.
4. Preserve versioned `RichDocument` projection for Markdown, rich UI, CodeBlock/WritingBlock, Unknown and escape; represent SVG as a raw editor node interpreted by trusted TSX. Update C#/TS tests for that deliberate representation change, while retaining API/React safety.
5. Independently review architecture and security, run isolated live Agent/Executor and browser UI checks, fix identified issues, and update task plan with evidence. Remove obsolete helpers, tests and docs for superseded behavior; never leave both tag-discovery implementations behind.

## Verification matrix

Run tests against the **exact final revision**, not historical green output:

- `dotnet run --project tests/Mezhs.RichUi.TestApp/Mezhs.RichUi.TestApp.csproj -c Release --nologo`: root/nested/unknown/opaque matching; direct/indirect escape; `SH`/`ATTACH`/`DONE`; wrong/duplicate/missing closers; fake closing tags inside opaque bodies; source spans; validation/limits; no commands under disallowed ancestry.
- `node --test tests/rich-model-ui-rendering.test.cjs`: full document SSR, escape muted projection, raw editor/unknown/SVG containment, literal attributes, React local controls, no unintended command text in presentation.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/agent-protocol-result.ps1`: isolated live Agent/Executor, root and root-escape execution exactly once, nested escape inert, fail-closed invalid batches, completion/continuation and durable result evidence.
- Relevant shared UI and architecture tests (`tests/agent-web-shared-ui.ps1`, `tests/agent-architecture.ps1`) and complete `dotnet build Mezhs.sln -c Release --nologo -v minimal`.
- TypeScript/Vite production build and an **independent live browser** confirmation in an isolated profile for rich rendering, radio/submit host action and missing/invalid documents.
- `git diff --check main...HEAD`, diff review for remaining multiple structural scans, and task-worktree cleanliness.
- Isolated MEŽS processes must not touch the owner's active Agent DB, API ports or browser profile.

## Risk, rollback and remaining design latitude

This is now an **architectural/security-sensitive refactor**; its prior Small override applied only to the superseded narrow change. Require independent Plan Review before active Development, fresh Code Review and independent Testing; no Deploy without owner approval. A Developer may challenge implementation details under `.agents/batman.md`, but must not expand host authority or silently reverse the matrix.

The branch already contains unmerged earlier attempts; those commits are evidence/history, not acceptance. The previous full test results at `fc586fba` do not prove current branch behavior. `main` has advanced independently; reconcile with the approved base as part of the task workflow, with no direct commits/merge to `main`. For rollback, keep the work isolated on this branch and revert task-specific refactor commits if necessary; do not alter runtime storage or API persistence format without separate approval.
