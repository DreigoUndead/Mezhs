# MEŽS rich-tag pipeline — target architecture

Status: **design guideline / Development input**, not executable pseudocode.
Parent plan: [plan.md](plan.md). Branch: `escape-root-command-execution`.
Recorded: 2026-11-24. Current unmerged implementation is **not** the reference design; verify migration against the actual source.

## Intent and boundaries

Replace the current partially duplicated tag-discovery mechanisms with **one implementation of root-level structural tag matching** reused for nested scopes. The owner-provided `Tag`/`TagContent` examples define responsibilities, not concrete C# signatures, exact fields or React filenames. Developer may adapt types/functions to fit the current Markdig, rich-UI, Agent and Web architecture. No parallel command parser, JS execution engine, DOM-sourced parsing or model-controlled dynamic React imports.

**One implementation of finding tags** does *not* require exactly one traversal of source bytes. It is acceptable to call that implementation on another bounded substring (e.g. root `escape`), provided no other code independently implements tag nesting and eligibility decisions. Distinguish tokenizer, structural matching, semantic validation and rendering without duplicating the structural logic.

Parsing only discovers/normalizes data and builds host-only command candidates. The Agent Interpreter validates the complete command batch against the authoritative Agent Registry/policy and executes commands afterward. React renders data using host-owned components, and can trigger only explicitly authorized user actions.

## Current implementation and seams

- `src/Mezhs.Common/RichUi/RichDocumentParser.cs`: currently contains `ReadChildren`, `ScanOpaqueContent`, `FindContainerClose`, `FindOpaqueClosing`, `TryTag`, special content handler, normalization and source spans; this is the duplication to remove/reconcile.
- `src/Mezhs.Common/RichUi/RichDocument.cs`: registry and `RichDocument`/`RichNode`/`RichCommandCandidate` contracts.
- `src/Mezhs.Common/RichUi/RichMarkdownNodes.cs`, `RichQuestionGrammar.cs`, `RichSvgValidation.cs`: Markdown lowering, limited interactive question grammar, SVG validation.
- `src/Mezhs.Agent.Api/Commands/CommandBatchAdapter.cs`, `Interpreter.cs`, `Registry.cs`: command candidate adapter, batch validation, policy/host execution; keep this boundary.
- `src/Mezhs.Api.Contracts/ApiContracts.cs`, `Mezhs.Api/Services/MessageService.cs`, `ChatService.cs`: completed assistant messages currently include a validated `Document`. Agent API also transports `RichDocument`.
- `src/Mezhs.Web.Lib/src/RichDocumentView.tsx`, `MarkdownContent.tsx`, `rich-elements/*`: typed frontend document with known component map. `RichActionContext.ts` and `rich-elements/Question.tsx` already support limited local state and a host-authorized new-turn action.
- Tests: `tests/Mezhs.RichUi.TestApp/Program.cs`, `tests/rich-model-ui-rendering.test.cjs`, `tests/agent-protocol-result.ps1`, `tests/agent-web-shared-ui.ps1`, relevant Agent architecture/safety fixtures. Existing tests contain both older and now superseded ancestry expectations; update them deliberately.

## Parent-defined child policy (2026-11-24 owner correction)

The implemented tag contract supersedes the earlier conceptual `SearchNested`
and `AllowAsNested` fields below. A `RichTagDefinition.SupportedChildren`
set defines how children are admitted: `null` means ordinary registered UI
children, an empty set means opaque/no children, and explicit registered tag IDs
mean only the listed children. Nested-only variants set `AtRoot=false` and
use a distinct `Id` where surface names collide (ordinary `text` versus
`svg:text`). The shared structural finder still recognizes complete excluded
wrappers, without promoting commands inside them.

`svg` and `g` admit graphic descendants by explicit ID; SVG `text` and
`tspan` admit only `svg:tspan`. SVG-specific attribute validation and quotas
remain in SVG-owned helpers. A new registered UI tag does not require a
generic parser name-case or another structural scanner.

The Agent `CommandBatchAdapter` projects host candidates/visible content; it
does not tokenize the source. Core uses the UI catalog, and Agent extends it
immutably with host-only command definitions. The React frontend retains its
separate statically imported, trusted component registry under
`rich-elements/Registry.ts`, so a UI component does not change the generic
`RichDocumentView`. This explicitly preserves the frontend import/DOM
security boundary; build-time automatic file discovery was not introduced.

## Tag registry and parsed shape

Conceptual fields (exact API left to implementation):

```csharp
record TagDefinition(
    string Name,             // protocol tag name, not an arbitrary component path
    bool AllowAsNested,      // normal nested eligibility
    bool SearchNested,       // whether ordinary child tags are discovered/processed
    string? ControlName,     // symbolic frontend component identity
    TagHandler? Function);   // host-owned handler; may collect candidate, never execute
record TagContent(
    TagDefinition Definition,
    ValidatedParameters Params,
    string Content,
    IReadOnlyList<TagContent> SubTags,
    TagContent? Parent,
    SourceSpan Span,
    SourceSpan BodySpan);
```

Do **not** copy these declarations mechanically. Existing roles (`Container`, `Leaf`, `OpaqueLiteral`, `RawEditor`, `Command`, `Marker`) and attribute allowlists may remain if they reduce complexity. A function/handler may be assigned only to registered tags; a null handler means display-only behavior. `ControlName` is a symbolic key (`box`, not `box.tsx`) and may be absent for host-only commands. Internal `Parent` references are set during discovery but are never serialized (avoid cycles); the UI needs `children` only.

`Params` are bounded and validated data, not `dynamic` or arbitrary executable expressions. Preserve the existing public `attributes: Record<string,string>` and `json:` encoded structured values initially, unless a well-tested API migration is demonstrably simpler. Unrecognized/unsafe attributes become inert UI diagnostics; they do not grant handler authority. Source spans remain original UTF-16 start/end offsets, end-exclusive.

## Discovery and processing

1. `FindRootTags(content, allowedTags)` is the **single structural tag finder**: identify direct complete tags, their matching delimiters, body spans, parsed attributes and source positions. Match all encountered recognized/unknown paired wrappers structurally, even those not returned by `allowedTags`, so filtering can never promote a nested command to root. Self-closing elements and marker tags are not assumed to have matching closers. Opaque block command bodies are never reparsed as tags. Unclosed openers remain literal according to existing behavior.
2. `FindTags(content, allowedTags, parent)` calls the root finder and recursively populates `SubTags` only when `SearchNested=true`, reducing the allowed child set to normally nested tags (`AllowAsNested=true`). Assign `Parent` explicitly. Avoid double-adding children to both parent list and return collection; one ordered tree is authoritative.
3. One dispatcher processes the parsed tree in **original source order**, invoking registered handlers where eligible. Handlers may emit command candidates, UI projections or normalizations, never perform shell/attachments/host side effects. Preserve the same final order between root-level and escaped candidates.
4. A root-level `escape` is `AllowAsNested=true`, `SearchNested=false`, with a registered handler. The handler reuses `FindRootTags` **on its body**, selects only registered `!AllowAsNested` tags with a callable command function, and enqueues eligible **direct** command candidates. It does not know `SH`/`ATTACH`/`DONE` by name, and never recursively promotes commands inside other paired wrappers. Command candidates' exact original spans are removed from escape's muted literal display. Other content is shown as literal source.
5. A nested `escape` has a non-null parent and its handler does not collect commands. Inert nested escape contents remain literal/ordinary nested presentation as appropriate. A `SearchNested=false` wrapper such as `CodeBlock`/`WritingBlock` is opaque: do not dig through it looking for escape. Unknown and invalid UI, SVG and raw-editor boundaries remain inert for nested commands.
6. Root-level registered commands are collected normally. Missing closers do not enqueue. Validate `DONE` final/unique, forbidden attributes, command body, limits and all Agent policy checks before the first command side effect. Preserve `SH` body bytes exactly and the existing incomplete-opener behavior.

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
- No second scanner, multiple copies of structural matching, special-case hard-coded executable names in escape processing, repeated raw-source reparsing in React, or frontend-originated execution. Reusing the **same** root finder on a bounded escape body is allowed.
- Avoid unnecessary typed-parameter/schema rewrite, additional abstractions, network protocols, locks, queues, dynamic imports or component redesign. Retain proven behavior of ordinary Markdown, radio question grammar, visual unknowns and raw editors.
- Since source is untrusted model output, maintain strict data-only serialization and correct distinction between visible markup and host-only executable candidates.

## Development sequence and migration

1. Inspect current source and all relevant regression expectations. Decide the smallest compatible `TagDefinition` and `TagContent` forms, identify the shared matcher and the one dispatcher. Establish a baseline on the task worktree.
2. Implement a reusable first-level finder with complete structural boundaries, source offsets and registry filters; add root/nested matching tests first. Replace `ReadChildren`/`ScanOpaqueContent` structural duplication and redundant close matching as a single cohesive migration, not a parallel indefinite codepath.
3. Connect recursion, `Parent`, root-only escape handler and shared command candidate collector; align tests to the latest owner-approved matrix. Keep batch validation and policy untouched and prove invalid replies do not cause side effects.
4. Produce the same `RichDocument` projections for Markdown, rich UI, SVG, CodeBlock/WritingBlock, Unknown and escape. Maintain stable API/React behavior; change client data shape only when required and with synchronized C#/TS tests.
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
