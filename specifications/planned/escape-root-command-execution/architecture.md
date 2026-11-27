# MEŽS rich-tag pipeline — target architecture

Status: **design guideline / Development input**, not executable pseudocode.
Parent plan: [plan.md](plan.md). Branch: `escape-root-command-execution`.
Recorded: 2026-11-24; revised after 2026-11-27 owner architecture challenge. The active task plan is authoritative. Previous static-import/Common ownership descriptions below refer to superseded implementation until migrated and verified.

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

## Unique tag identity and ancestor eligibility (2026-11-26 owner decision)

One registered MEŽS source name has one structural definition per composed
`RichTagCatalog`. Duplicate names, including a raw-root/container-nested
combination, are invalid at registration. Different functions require
different tag names. No contextual variants, `Id`/`Key`, parent overrides
or `AtRoot` flag are retained.

A tag can declare optional `RichTagDefinition.RequiredAncestors`:
`null` accepts any location; a nonempty set of registered tag names
requires at least one name among its **structural ancestors**, direct or
indirect. The document root has no ancestors and therefore cannot admit a
restricted tag. This is checked **after** structural discovery; no
registration or eligibility test may suppress containment of a complete
excluded wrapper. The owner chose any-ancestor matching, not direct-parent
matching. Registered ancestor references and uniqueness are verified when
composing a catalog.

The existing `SupportedChildren` declaration remains a separate direct-child
rule: `null` means ordinary registered UI children, empty means opaque/no
MEŽS children, and an explicit name set admits only those direct children.
`AllowAsChild=false` remains an independent Agent host-command restriction:
root commands and root-`escape` direct commands use their existing
authorization path, not the general presentation ancestor rule. The shared
structural matcher always recognizes excluded and unknown wrappers so nested
commands remain inert.

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
immutably with host-only command definitions. The React frontend should replace manual `rich-elements/Registry.ts` imports/maps
with **build-time** discovery of curated host-owned TSX files using a literal
bounded directory glob or generated static manifest. The generic renderer
resolves the already validated tag name from a safe own-key map. Runtime
assistant-supplied import paths and arbitrary JSX remain forbidden.

## Tag registry and parsed shape

The source-level contract is defined in `RichTagRegistry.cs`:

- `RichTagDefinition` declares a globally unique name within the catalog, role, permitted attributes, `SupportedChildren`, optional `RequiredAncestors`, inline/interactive metadata and an optional registered `ProcessContent` callback.
- `RichTagContent` supplies validated data, original source/body spans, parent context, self-closing and root flags to handlers; handlers may emit candidates but never execute host side effects.
- `RichTagCatalog` is immutable per host. Core uses `RichTagRegistry.Ui`; Agent composes its own commands from `Mezhs.Agent.Api/Commands/Registry.cs`. This keeps Agent capabilities out of Core.
- `SupportedChildren`: null permits ordinary registered UI direct children; empty forbids MEŽS child processing (including opaque bodies); an explicit registered name set restricts direct children. `RequiredAncestors` optionally restricts descendants by any ancestor name. No tag-name-specific parser cases or contextual variant selection.
- The frontend has a build-time generated, eagerly bundled map from a curated directory of host-owned TSX modules to symbolic names, including raw/literal controls; no manual imports/maps or generic renderer cases for a new control. No model-controlled import paths, arbitrary JSX or dynamic host callbacks.

`RichDocument` remains versioned serialized data (`version`, `valid`, `nodes`, `diagnostics`) with source spans and inert, bounded attributes. Private `Parent`/handler references and host-only command candidates must never be serialized to React. Preserve existing safe literal data representation and streaming fallback; change the protocol only if a justified, synchronized migration is approved.

Architectural acceptance includes a **one-more-instance exercise**: an ordinary UI tag changes only its C# definition, a TSX component file and optional registered function: **no** explicit frontend binding edit and no generic parser or renderer changes. An Agent command still needs explicit policy/Interpreter capability authorization if it adds a new privileged host effect; an embedded language stays opaque to MEŽS and is safely decoded by its trusted component. Review actual code/caller paths, not just test outcomes.

## Discovery and processing

1. `FindRootTags` is the shared structural matcher for normal parsing and bounded root-`escape` command discovery. Structural matching includes known and unknown wrappers, parent relationships, paired delimiters, self-closing tags and markers, even when a tag is not eligible for display/execution. Filtering only affects returned tags, never ancestry.
2. `RenderChildren` consumes the located tree, applies direct-parent `SupportedChildren` and any-ancestor `RequiredAncestors` admission while retaining structurally located wrappers, produces Markdown/rich nodes and invokes the **single** `DispatchTag` for validated registered handlers. There is no separate public `FindTags` or `SearchNested` mechanism.
3. **`SH`, `ATTACH`, and `DONE` are ordinary registered tags with optional functions**, contributed by the Agent host, never Core. The generic matcher and dispatcher do not branch on their names or on special command/marker identities. Declarative syntax/placement metadata determines how each tag is structurally matched; callbacks collect candidates with original spans and byte-exact bodies, without side effects. The Agent command adapter and Interpreter authorize the complete batch and perform effects only after full validation. `DONE` final/unique is an Agent protocol invariant, not a generic rich-document syntax rule.
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

React uses a **build-time discovered, static bundle of trusted host-owned components** keyed by validated symbolic names; only vetted control files participate in the literal directory glob. The renderer looks up an own key and maps only approved params into component props; do not use assistant-provided URLs/import paths or arbitrary DOM prop spread. The direct `esbuild.buildSync` Node SSR tests will require a compatible compile-time manifest or migration to Vite-based transformation if using Vite's `import.meta.glob`. Reuse current `rich-elements/Style.ts` bounded decoding for spacing, border, colors, layout, links, and the current `json:` literal value protocol unless a demonstrably necessary change is approved. Preserve safe SVG and unknown diagnostic fallback. `escape` displays muted literal source with executable spans omitted. Raw editors render raw inert content. Host-only commands do not mount as React controls.

Interactive components may own local React state (`Question.tsx` already does); host actions flow through `RichActionContext` or another explicitly approved host capability and require real user interaction. Model-provided attributes never become JS functions or privileged callbacks. If a richer generic action system would be needed, **leave it outside this refactor** unless already supported by the current safe finite grammar; do not infer permission from `Tag.Function`. Keep Agent transcript actions read-only where already enforced.

## Correctness, containment and performance

- Full paired matching for nested same-name containers, unknown tags, opaque editors, SVG, escapes and command bodies. A fake parent closer inside an opaque command body cannot escape its structural parent.
- Mixed direct commands and prose retain original order, exact bodies, source spans and display projection; no duplicated command execution or double-counting during normalization.
- Preserve bounded message length, tag/attribute count, nested depth, node budget, SVG budget, safe URL and attribute policies; fail closed on untrusted input with no partially authorized batch.
- No competing **MEŽS tag** parser, multiple implementations of structural matching, hard-coded executable tag names in escape processing or frontend-originated command execution. The bounded SVG-only TSX decoder is intentionally separate: it interprets a distinct embedded language and cannot grant MEŽS/Agent capabilities. Reusing the same C# finder on a bounded escape body is allowed.
- Avoid unnecessary typed-parameter/schema rewrite, additional abstractions, network protocols, locks, queues, dynamic imports or component redesign. Retain proven behavior of ordinary Markdown, radio question grammar, visual unknowns and raw editors.
- Since source is untrusted model output, maintain strict data-only serialization and correct distinction between visible markup and host-only executable candidates.

## Development sequence and migration

1. Inspect actual Core/Agent project dependencies, tag/raw rendering paths, syntax readers, ownership and regression expectations. Prefer moving typed `RichDocument` contracts into `Mezhs.Api.Contracts`, parser/catalog/handlers into `Mezhs.Api`, Agent commands into `Mezhs.Agent.Api`, and TSX into `Mezhs.Web.Lib` without creating a new project or a dependency cycle. Decide the smallest compatible tag/content forms and establish a baseline.
2. Refactor the existing finder into small readers for paired/self-closing tags and opaque comment/CDATA tokens, all feeding one bounded structural cursor/stack. Unify their use across rich and raw-editor boundary matching, without a second command parser. Keep root-comment Markdown compatibility. Retire unused `UnsafeRawTags` and the generic parser's hardcoded `DONE` invariant; confirm the Agent Interpreter enforces final/unique completion.
3. Connect recursion, `Parent`, root-only escape handler and shared command candidate collector; align tests to the latest owner-approved matrix. Keep batch validation and policy untouched and prove invalid replies do not cause side effects.
4. Preserve versioned `RichDocument` projection for Markdown, rich UI, CodeBlock/WritingBlock, Unknown and escape; represent SVG as an opaque raw editor. Make registered ordinary/raw/literal UI components use one generic renderer dispatch with no hardcoded `escape` name branch. Replace manual frontend registry imports through safe build-time discovery. Update C#/TS tests and the build harness accordingly; do not permit model-directed runtime import.
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

The branch already contains unmerged earlier attempts; those commits are evidence/history, not acceptance. Independent Step 4/5 PASS recorded on 2026-11-27 applies to the prior architecture and must be repeated on the forthcoming refactor. `main` has advanced independently; reconcile with the approved base as part of the task workflow, with no direct commits/merge to `main`. For rollback, keep the work isolated on this branch and revert task-specific refactor commits if necessary; do not alter runtime storage or API persistence format without separate approval.

## 2026-11-27 owner re-evaluation — design boundary

The request in MEŽS execution `exec_0276bf3123db4b05bda2388dd5dfad62` challenges (1) generic parser hardcoding, (2) mixing token syntaxes, (3) UI implementation located in `Mezhs.Common`, and (4) manual `Registry.ts` maintenance. This guideline now favors these corrections. **It does not claim they are implemented.** Step 6 is paused; no new runtime tests or architectural review PASS has been established.

The security-critical counterpoint to splitting syntax into independent parsers is that each would need to duplicate wrapper containment logic, exactly the source of earlier fake-closer regressions. Use distinct **small token-reader methods** feeding one consistent stack and shared opaque boundary checks. The unused parser declaration `UnsafeRawTags`, generic `DONE` literal, name-specific `escape` view branch and handwritten TSX map are removable responsibilities; Markdown and the finite radio grammar are independently owned languages with deliberate integration points, not tag-specific parser branches.

No project move is justified by taste alone. In this repository the API and Agent hosts are the actual C# rich-UI users, while API contracts need only wire DTOs, so the existing Api/Api.Contracts project boundary is a plausible target. Confirm dependency graph and test fixtures during migration; no cyclic reference or new general-purpose utility assembly. A frontend control file should export a single conventional adapter for either element or literal/raw content so a curated eager glob can bundle it without manual mapping; prevent unregistered names and model-controlled paths from triggering arbitrary components.

## 2026-11-27 — All Agent commands are tags with functions

Owner direction in MEŽS `exec_43b7fa389fd4492c84067af2b322fa0c` resolves the remaining ambiguity: `DONE`, `SH`, `ATTACH` are just registered tags, each with a function. The source may use distinct *declarative* syntax forms (paired opaque body or bodyless marker), but the generic C# parser must have no hardcoded name-specific logic or command/marker execution path; registration and its function determine semantics. Agent-only handlers collect candidates, and only the Agent Interpreter/policy acts on them after whole-batch validation. An ordinary UI tag needs only a C# definition, one trusted TSX and optional handler; a new privileged Agent capability additionally needs explicit policy authorization, not parser changes.

The owner authorized direct Step 3 Development without an additional Plan Review for this iteration. Earlier Step 4/5 PASS does not validate the new architecture; review, testing and Deploy approval remain required. No merge or modification of `main` is authorized.
