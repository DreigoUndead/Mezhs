# Canonical rich document model and nested Agent commands

ID: rich-ui-custom-controls
Branch: rich-ui-custom-controls
PR: https://github.com/DreigoUndead/Mezhs/pull/53
Level: **High-risk — cross-runtime parser/renderer redesign and host command authority**
State: **PLANNED — independent plan review required before further implementation**
Dependency: existing Rich UI Custom Controls work on this branch; old incremental plan versions remain in Git history (last archived plan at 54686a1d591be4a806a1f3e17d559aa8175736b2).
Owner decision (2026-11-12): adopt one canonical **C#** document/parser/validation model; React renders typed validated data and owns only local interactive state; commands can occur inside valid containers/lists.
Owner clarification (2026-11-13): **Do not preserve legacy UI parsing/handling.** All supported old and new tags use the one canonical C# parser, DTO and typed React renderer. Remove the old frontend extraction, expression and markup paths instead of a compatibility fallback. Retire or migrate raw-text frontend tests to validate canonical DTOs and the inert provisional fallback; treat unsupported expression constructs as inert until explicitly added to the canonical grammar. Security/command boundary tests remain mandatory.
Earlier owner waiver of independent plan review applied only to the previous bounded implementation, **not** this expanded architecture.

## Why this redesign is needed

- The current shared frontend `src/Mezhs.Web.Lib/src/MarkdownContent.tsx` parses raw model replies using react-markdown, remark-mdx, raw editor extraction, question extraction, expression/template expansion and its own static-element validation.
- The Agent API independently scans the same text in `src/Mezhs.Agent.Api/Commands/Parser.cs`, using separate Markdown literal and UI-container rules before forwarding command batches to `Interpreter`. Repeated fixes have exposed disagreement between these parsers, including a list-contained code fence that can appear to end a static box and expose a shell-shaped line.
- `MarkupRegistry.cs` is an initial non-behavior-changing tag-role seam, **not** the final architecture. Do not preserve parallel parsing as the end state or infer safety from the existence of a registry.
- `Mezhs.Api` powers ordinary chats and is composed in-process by `Mezhs.Agent.Api`. Canonical parsing therefore belongs in a reusable C# core/domain module accessible to both hosts, **not exclusively inside Agent API**. The Web Lib is shared by both Web frontends.
- The current PR includes existing radio-question, static layout, SVG, citations, CodeBlock and WritingBlock behavior. The refactor must preserve supported user-visible behavior or explicitly obtain owner approval for any incompatibility.

## Target invariant and responsibilities

**An input has one authoritative parse and validation result. Only a fully valid command node in an executable context may enter the Agent Interpreter. Literal or rejected source cannot acquire action authority. Rendering cannot create command authority.**

- **C# canonical document parser** (new small shared domain owner, choose existing `Mezhs.Common` or a focused shared assembly by dependency analysis): parse Markdown plus the bounded model-UI tag grammar, classify Markdown code and other literal regions, enforce nesting, lengths, depth and attribute contracts, and return a typed, versioned, serializable document tree with original source positions.
- **C# tag registry**: one fixed, host-owned definition per supported tag: name, role (container / leaf / raw / interactive / Agent command), attribute schema and allowed child/content semantics. Command definitions remain backed by the current Agent `Registry`; no second command list or model-selected executable delegate. UI component identifiers are symbolic, not serialized functions.
- **C# document contract/API**: both API hosts produce the same safe DTO for displayed responses. Define source-span offsets, deterministic IDs, normalized scalar attributes and protocol version; preserve the raw text as evidence/audit data and for bounded literal fallback. Parser diagnostics are data only. Do not persist redundant AST as a second source of truth unless proven necessary.
- **React**: map already-validated display node kinds/normalized props to known components; render text, Markdown, tables, layout, SVG, citations, raw editors and question controls. Retain React-only ephemeral selection, editing, focus, copy and host-approved send callbacks. It must never reparse model-authored raw JSX/Markdown to discover **new** actionable controls. URL, HTML, DOM and style sinks retain last-mile safe handling even with trusted DTOs.
- **Agent Interpreter**: receive document-derived command nodes in strict source order; validate command forms, policy, completion order and all actions **before** executing any. Existing `Shell`/`Executor`, attachment staging, policy, durable execution IDs and cancellation remain the sole execution path. UI render nodes and frontend callbacks cannot invoke Agent commands.
- **No general DIL runtime**: no evaluation of model-authored JavaScript, callbacks, imports, hooks, dynamic components, arbitrary CSS, external SVG references or arbitrary HTML.

## Owner feasibility decision (2026-11-12)

Independent plan review passed. Following the Markdig spike and complexity discussion, the owner explicitly approved architecture A: one authoritative C# document parser for UI and Agent commands.

- Opaque bodies: escape, SH and ATTACH do not recursively parse their content. escape is inert; SH and ATTACH are executable only in validated command contexts. Preserve exact command body text and original line endings. An unmatched opener fails closed.
- DONE is a bodyless marker and must be the final Agent command globally.
- Normal static containers and Markdown lists parse recursively. Markdown fences, indented code, inline code and comments remain inert automatically without requiring escape.
- Validate all scopes and the entire command sequence before dispatch. Invalid structures must never expose internal command-shaped text.
- Preserve existing supported UI behavior without executing model-authored JavaScript. The finite radio choice grammar must be interpreted as data, with an owner escalation if compatibility requires disproportionate complexity.
- The observed accidental execution of example commands (execution IDs 6429-6431) requires negative-side-effect regression coverage; the observation does not establish the specific defect mechanism.

The Stage 1 spike established that Markdig 1.6.0 provides UTF-16 source spans and Markdown literal contexts, but its Markdown AST alone does not build rich-tag nesting or radio expression values. Extend its grammar in bounded C# rather than retaining two independent parsers.

## Parsing contract

1. Use an established .NET Markdown parser as a candidate (e.g. Markdig), **after a feasibility spike** verifies access to AST/source spans, Markdown lists, nested fences, inline code and extension points. Do not implement or hand-maintain a nearly-complete Markdown parser with regex. Select library/version only after dependency/security/compatibility assessment.
2. Build a bounded extension/tokenization layer for **recognized** rich tags and Agent protocol markers, taking Markdown literal context from the canonical Markdown AST. Support quoted attributes containing `>`, self-closing tags, nested same-name tags, ordinary Markdown inside containers, and mixed Markdown/JSX-like constructs only where explicitly allowed by the grammar. Parser resources (bytes, nodes, depth, attribute count/length, token count and computation time) must be bounded.
3. Treat `CodeBlock`, `WritingBlock`, Markdown fenced/indented/inline code, comments and `escape` as **opaque literal nodes**. HTML comments, escaped markup and blockquote/list code examples cannot leak an executable tag. An unmatched literal opener must fail closed. Raw block bodies remain exact text; no Markdown processing or nested tag activation inside.
4. Valid `box` / `row` / `col` and other approved container nodes are traversed recursively; a list can occur inside a box, and an Agent command can occur within an executable list item/container. Commands are never nested **inside another command body**: `SH` and `ATTACH` retain opaque bodies including command-shaped examples and their existing standalone closing-delimiter protocol. Exact command bytes/newlines and source order are preserved.
5. `DONE` is a marker; it must be the last **Agent command** anywhere in the full document, including nested scopes, and may appear only once. Existing known command syntax/unknown ALL_CAPS rejection semantics continue unless the owner approves a specific compatibility change. `ABORT` is not currently in `Registry.All`; don't silently add unsupported commands.
6. Validate **the entire document and candidate command sequence before dispatch**. Invalid/mismatched/unknown/unsupported UI scopes enclosing commands must fail closed with **zero** execution or attachment side effects. Never resume executing interior text by guessing a closing tag. Outside complete validated scopes, preserve legitimate standalone commands.
7. Presentation-only validity and action validity must be explicit, not confused: an unsupported presentational fragment can be shown literally, but it may not contain an executable descendant. The DTO keeps literal fallback as escaped text, never trusted HTML or fabricated host slots.
8. Rendering pure content remains allowed in Agent transcripts, but React-local send actions remain disabled there. Ordinary completed assistant messages can invoke the existing host-gated `issueNewTurn` only after a click and existing busy/offline/conversation-ownership checks.

## Canonical DTO and compatibility

The exact DTO shape is a **design artifact to prove during the feasibility spike**, not an accepted API schema yet. Minimum needs: `version`; node `kind`; normalized `attributes`; child nodes or opaque `literal`; deterministic `sourceSpan`; host-only command records (not as React event handlers); and a diagnostic outcome. Use discriminated node types, not a loosely typed bag of functions.

- Source spans must be unambiguous across C# / JavaScript (UTF-16 code-unit offsets or byte offsets chosen and documented), line endings, Unicode, source fragments and recovered streaming text.
- Determine whether all messages are server-originated. If local-only/streaming previews bypass the C# API, route them through the same canonical parsing contract or show **inert provisional text**, never create client-side action authority. Only a completed, validated response may expose actionable UI controls or Agent commands.
- Keep provider-neutral behavior for ChatGPT, Gemini and Grok, ordinary Web and Agent Web, histories/reloads, Markdown errors and partial streams. Do not make ordinary Core chat depend on Agent-only assemblies or shell permissions.
- Inventory existing supported syntax in `docs/supported-rich-ui.md` and real fixtures: Markdown/GFM, nested static layout, SVG/templates, citations, links, radio question (including four conditional answers), CodeBlock, WritingBlock. Migrate these to typed nodes. In particular, don't casually port the legacy Acorn-based `{@body}`/ternary question grammar by introducing JavaScript execution in C#. Prefer finite data interpretation; if exact compatibility is disproportionately costly, **escalate to the owner** with an explicit compatibility choice rather than silently dropping controls or retaining two action parsers.
- Compare normalized render trees and interactions against existing SSR/Chromium fixtures; semantic parity matters more than identical internal markup. Existing host-approved actions and local editor state must remain safe.

## Feasibility and coding sanity gate — before implementation

The development agent must present a concrete feasibility result and stop for owner choice if any item is disproportionate:

| Question | Evidence required / decision |
| --- | --- |
| Right owner? | C# reusable parsing library can be referenced by Core and Agent without circular dependencies or React imports; Agent-specific policy stays outside Core. |
| Existing parser? | Prove a .NET Markdown parser exposes enough structure/source spans to distinguish list-contained fences, inline code, comments, and validated custom blocks. |
| Shared contract? | Define one C# tag/attribute schema and normalized DTO. React maps known kinds to components; no second raw-text validity grammar. |
| Streaming? | Show path for live partial output and loaded messages; provisional chunks do not create host authority. Avoid reparsing entire text per keystroke or token without budget. |
| Regrettable complexity? | Compare (A) shared C# AST refactor, (B) smaller Agent-only structural parser, and (C) separate structured Agent command channel. Recommend A only if it reduces long-term concepts and duplicated decisions; otherwise return for explicit owner decision. |
| Security? | Source positions and literal boundaries cannot drift between backend extraction and frontend rendering. Any parse error forbids Agent execution rather than guessing. |
| Migration? | Demonstrate a vertical slice ordinary chat + Agent chat + real rich fixture + nested-list code fence before wholesale replacement. |
| Maintenance? | Retire old `RichQuestion` extraction/markup shielding and Agent scanner when replaced; no permanent dual parsing/action owners, speculative caching, duplicate DTO stores or framework for hypothetical tags. |

## Implementation stages (after independent plan PASS)

1. **Inventory and spike:** locate Core message serialization, streaming and all Web Lib entry points; select .NET Markdown library after running targeted real fixture parses; define the versioned DTO and C# attribute schemas; record incompatibilities/owner decisions. Use the existing `MarkupRegistry` as a temporary seam or replace it; don't blindly expand it.
2. **Non-authoritative vertical slice:** parse one static nested box+Markdown-list+literal-fenced-example plus one rich question in C#, serialize typed nodes through a Core endpoint or existing message DTO with a versioned field, render them using known React components in both Web surfaces. Retain raw text for audit and inert fallback. No newly executable nested command yet.
3. **Full renderer migration:** adapt Markdown/GFM, static controls, SVG/citations, raw CodeBlock/WritingBlock, radio state/messages, link/URL sanitization, accessibility, histories, streaming and recovery. Test equivalence. Remove replaced TS source extractors/duplicated attribute grammars once coverage passes; no unsafe runtime compatibility fallback.
4. **Agent command integration:** recursively collect only validated executable command nodes from the canonical C# tree; feed existing Interpreter/policy/Executor without adding another action layer. Preserve opaque bodies, command index/idempotency, ordering and final completion. Remove old `Parser.cs` special-case scanning and shielding **only after** negative-side-effect integration coverage. Review privilege implications of nested commands explicitly.
5. **Documentation and cleanup:** update `specifications/current/` only for implemented behavior and `docs/supported-rich-ui.md` for the released tag grammar, shared DTO and nested command behavior. Regenerate Codegraph from its supported tool, not by hand; prepare release note/bump recommendation without touching VERSION/CHANGELOG.

**Gates:** independent High-risk plan reviewer before Stage 1; engineering feasibility/owner escalation before broad migration if needed; independent code reviewer before final independent tester; explicit separate owner approval before any merge/release. If the vertical slice cannot prove one authoritative parse, **stop rather than keep permanent parallel mechanisms**.

## Acceptance and security tests

- **Structure:** valid root and nested containers/list commands produce deterministic typed nodes, original source positions and command order. Valid `SH`/`ATTACH` bodies survive CRLF/LF, quotes, Unicode and embedded command-looking examples. `DONE` is final globally. No command runs during document parsing, serialization or rendering.
- **Inertness:** code fences inside Markdown lists (including list in box), nested list indentation, blockquote/code, inline backticks, four-space code, escape, raw editors, comments, quoted tag attributes, malformed JSX, unsupported attrs and unclosed/mismatched wrappers yield zero Shell/attachment effects. Later standalone commands only regain authority after an unambiguous valid boundary.
- **Errors:** reject unknown or malformed ALL_CAPS commands, nesting abuse, oversized messages/tags, recursive depth/node budget, invalid expressions, duplicate attributes and forged client slots; fail closed without invoking a previously parsed command.
- **Parity:** existing representative and adversarial rich UI fixtures: four choices produce exact A/B/C/D messages; accessible radio keyboard flow, icon, editable and readonly blocks, SVG/tables/citations, links and visual layout work in both Web surfaces. No model-authored JS executes; Agent transcript action buttons remain inert.
- **Transport and state:** new/loaded chats, live streaming, provider variants, chat switching, busy/offline states, duplicate clicks, conversation drafts, attachments, persistence/restart, cancel/retry and durable command IDs remain correct. Provisional streams have no action authority.
- **Proof:** parser unit/property/fuzz tests, .NET Agent API integration tests that inspect Executor history and filesystem side effects, differential golden fixtures (C# typed nodes versus old display before retirement), SSR + Chromium tests, .NET Release builds, both frontend builds, existing provider/chat regressions, CI at pinned SHA. Collect timings/memory for long replies and pathological input.
- **Review:** independent High-risk code review returns PASS/FAIL/BLOCKED against full diff and plan; separate independent tester runs final security/functional verification after PASS. Developer checks and green CI do not substitute for either.

## Workflow, release and pending decisions

- This is the **same existing task branch** / PR #53. Because the scope was reopened for a fundamental redesign, the canonical plan is returned to `specifications/planned/rich-ui-custom-controls/plan.md`; earlier implemented-plan content remains in Git history. Move this final plan to `implemented/` only when development completes, **before independent code review**.
- The owner approved architectural direction (single canonical C# parse, React mostly declarative, support valid nested command nodes). That is **not** approval of an unreviewed concrete DTO, parser library or breaking syntax changes.
- Independent plan review is mandatory. Prior waiver does not apply to this expansion. Document reviewer findings and obtain owner decisions for meaningful plan corrections.
- Core and Agent remain separate alternative API hosts, sharing safe parsing data; no new remotely reachable endpoint or model-defined executable callback.
- Do not edit version/changelog, tag, merge into `main`, or claim independent testing during this planning phase. Main remains release-only; completed merge requires separate explicit owner approval.
- Release proposal: **MINOR** if compatible additive UI/Agent behavior is delivered; reconsider if breaking DTO/API or supported rich syntax is explicitly approved.
