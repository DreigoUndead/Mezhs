# Canonical rich document model and nested Agent commands

ID: rich-ui-custom-controls
Branch: rich-ui-custom-controls
PR: https://github.com/DreigoUndead/Mezhs/pull/53
Level: **High-risk — cross-runtime parser/renderer redesign and host command authority**
State: **IMPLEMENTED — corrected post-review testing regressions on the task branch, pre-review regression-verified; fresh independent High-risk code review and separate independent final testing pending**
Dependency: existing Rich UI Custom Controls work on this branch; old incremental plan versions remain in Git history (last archived plan at 54686a1d591be4a806a1f3e17d559aa8175736b2).
Owner decision (2026-11-12): adopt one canonical **C#** document/parser/validation model; React renders typed validated data and owns only local interactive state; commands can occur inside valid containers/lists.
Owner clarification (2026-11-13): **Do not preserve legacy UI parsing/handling.** All supported old and new tags use the one canonical C# parser, DTO and typed React renderer. Remove the old frontend extraction, expression and markup paths instead of a compatibility fallback. Retire or migrate raw-text frontend tests to validate canonical DTOs and the inert provisional fallback; treat unsupported expression constructs as inert until explicitly added to the canonical grammar. Security/command boundary tests remain mandatory.
The expanded canonical architecture passed independent plan review before implementation; subsequent changes retain the same reviewed design, with unsupported model expressions intentionally inert.

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

## Implementation outcome and developer verification

- Replaced the duplicate JavaScript frontend grammar and Agent command scanner with shared `Mezhs.Common.RichUi.RichDocumentParser`, `RichTagRegistry` and versioned typed DTOs. Both API hosts serve canonical documents; typed React renders only known, validated presentation nodes and hosts the local radio/editor state. The Agent adapter consumes the same parsed command records and continues to enforce Agent Registry/policy.
- Nested command authorization is limited to the root, Markdown lists and validated structural layout containers (`box`, `row`, `col`, `grid`, `grid-item`); display-only ancestors, SVG, Markdown code, escapes and raw editors never authorize commands. Entire invalid documents return no candidate commands. The bounded radio grammar supports the original literal single-interpolation and finite strict-equality conditional choice fixtures; more complex expressions/templates remain inert.
- Developer checks: canonical C# parser 32/32; typed SSR 43/43; real Chromium integration PASS; Agent protocol/Executor integration including rejected-batch negative side effects PASS; Agent Web shared UI PASS; Agent architecture PASS; `Mezhs.Agent.Api` Release build PASS; `Mezhs.Agent.Web` production build PASS. Portable Codegraph regenerated (280 files, 726 edges). These are developer checks **not** an independent code review or independent testing.
- Updated `docs/supported-rich-ui.md`, `specifications/current/agent-runtime.md`, and generated `.agents/codegraph/graph.json`. The planned compatibility rule remains explicit: unsupported old expression syntax renders as inert source; no duplicate legacy frontend parser is retained.
- Proposed release category: **MINOR** for the coordinated UI/Agent architectural change. Do **not** update VERSION or CHANGELOG until separately approved merge/release.

## Independent code review FAIL and correction record (2026-11-13)

A separate High-risk reviewer assessed the full PR diff at `ae5da658a6d5769ae517b06db231f20ff206b77a` against main `179c0da50a17e7024d76eaa2571cb6ee2498348e` and returned **FAIL**: execution `exec_d739b825ab8a45959ae63614dbc7a359`, chat `chat_870a802521134703ad66b61300eda6c1`. The reviewer did not edit files or perform independent testing.

Findings and same-branch dispositions:

1. **P1 — quadratic repeated Markdown suffix scans.** Parsing every sibling's entire remaining suffix was replaced with a bounded matching rich-container closing scan, Markdig literal protection scoped to that container, and an explicit cumulative Markdown-work budget. Added C# wide/deep cases; **900 sibling containers parsed in 55 ms** in the developer verification run. The container lookup now also protects false closing tags inside fenced Markdown. Corrective commits include `1dadbe5`, `3904bfd`, `8311ea2`.
2. **P1 — lost Markdown image parity.** Markdig `LinkInline.IsImage` now lowers to typed `image` nodes (HTTP/HTTPS validated, URL length bounded); React renders inert, lazy `img` with escaped alt text, referrer suppression and no script URL sink. Added SSR regression for safe/unsafe sources. Commits `caafd84`, `f9e3254`, `70457de`.
3. **P2 — Markdown fences visible inside code blocks.** Code-node text comes from Markdig code lines rather than the source range containing fences/language info. Added fenced, indented, and CRLF rendering regressions. Commits `caafd84`, `70457de`.
4. **P2 — documentation example wrapped the radio grammar in unsupported `box`.** Corrected `docs/supported-rich-ui.md` to show the actual finite flat one-state grammar and its limitation; added a test that extracts and parses the documented example. This is a documentation correction, not a new UI grammar or change to the approved architecture. Commits `7c857d1`, `70457de`.
5. **P2 — missing GFM task checkboxes and table header semantics.** Added typed Markdig task-list checkbox nodes and header table-row/cell flags; React displays disabled checkboxes and semantic `thead`/`th scope="col"` with corresponding test coverage. Commits `caafd84`, `f9e3254`, `70457de`.

Developer verification of these corrections at `8311ea2fe1376341d23c809b365ea4d1c599006c`:

- C# parser/security: **35/35 PASS**, including wide/deep and code-fenced false closing tag.
- Typed React SSR and Chromium: **48/48 PASS**.
- Agent protocol/Executor integration: **PASS**, including real two-shell batches and negative filesystem/command side-effects.
- Agent Web shared UI integration: **PASS**, plus 19 tests.
- Agent architecture checks: **PASS**.
- Agent API .NET Release: **PASS (0 warnings/errors)**; Agent Web TypeScript/Vite production build: **PASS**.
- Regenerated portable Codegraph: **286 tracked files, 743 dependency edges**, including new GFM references.

The PR metadata has been updated from obsolete planning/failed-plan-review text to the implemented work. **These are developer checks only.** A fresh independent code-review verdict against the corrected HEAD is required; on PASS, hand off to a different independent tester. No owner approval to merge/release has been granted.

## Workflow, release and pending decisions

- This is the **same existing task branch** / PR #53. The final canonical plan is now `specifications/implemented/rich-ui-custom-controls/plan.md`; earlier plan iterations remain in Git history. Independent high-risk code review must PASS before separate independent final testing and owner approval.
- The owner approved architectural direction (single canonical C# parse, React mostly declarative, support valid nested command nodes). That is **not** approval of an unreviewed concrete DTO, parser library or breaking syntax changes.
- Independent plan review is mandatory. Prior waiver does not apply to this expansion. Document reviewer findings and obtain owner decisions for meaningful plan corrections.
- Core and Agent remain separate alternative API hosts, sharing safe parsing data; no new remotely reachable endpoint or model-defined executable callback.
- Do not edit version/changelog, tag or merge into `main` during review/testing. Main remains release-only; completed merge requires separate explicit owner approval.
- Release proposal: **MINOR** if compatible additive UI/Agent behavior is delivered; reconsider if breaking DTO/API or supported rich syntax is explicitly approved.

## Follow-up independent review and complexity gate (2026-11-13)

Independent High-risk re-review against feature `b5dde35cb7d2f83ff07d183172d70a081bc33b2d` and main `179c0da50a17e7024d76eaa2571cb6ee2498348e` returned **FAIL**. Three new findings were raised:

1. **P1 — originally supported radio question with one surrounding `box` stopped rendering.** The prior frontend explicitly recognized this structure, but initial canonical grammar rejected it. Same-branch commits `0ad3d8f`, `55a9ad0`, `c18317b`, `f93654a`, `9c58fd9`, `d734e79` restore a one-box finite radio grammar and add a fixture and SSR regression assertions. These commits exist on the branch; developer checks after these commits have **not yet been recorded** here.
2. **P2 — supported `textAlign` / `tabularNums` attributes were omitted from the canonical tag registry.** Same-branch commit `505a44f` adds them to the registry, with SSR assertions at `d734e79`. These new changes have **not yet been verified** in a recorded developer test run.
3. **P2 — inline rich tags fragment Markdown block/inline semantics.** At `b5dde35`, `RichDocumentParser.ReadChildren` splits the source before each recognized tag and `RichMarkdownNodes.Convert` independently parses each fragment. This can produce paragraphs inside inline `text` elements, split a paragraph across sibling nodes, and lose Markdown grouping in lists/headings/links. This issue remains **unresolved** at `d734e79500830c1917d83862d7e1f70800395e82`.

**Mandatory complexity escalation:** Repeated fixes now stem from the same structural split-and-reparse strategy. The repository workflow requires an owner decision before introducing further complexity. Options: **(A)** refactor within the single canonical C# parser so Markdig retains Markdown block/inline context while validated rich tokens become typed children (higher implementation/test cost, retains supported semantics); **(B)** narrowly unwrap/merge paragraph fragments in postprocessing (smaller diff but incomplete for headings, links, lists and cross-tag markup; not recommended as a durable fix); or **(C)** explicitly narrow the supported syntax to rich elements that do not interrupt Markdown inline structure (simpler but a compatibility break requiring explicit owner approval). Recommendation: **A**, with bounded source spans, all-or-nothing command validation, and real-fixture regression coverage, or **C** only if the owner deliberately accepts reduced compatibility. **Await owner choice before further parser changes.**

No independent test or fresh independent review has been performed for the seven follow-up commits. Do not merge or start final independent testing until the remaining issue is resolved or explicitly accepted, developer checks are recorded, and a fresh independent High-risk reviewer passes the final SHA. Version and changelog remain release-only.

## Owner-confirmed recursive parsing and second review corrections (2026-11-14)

The owner clarified that parsing must remain **recursive**: a `box` parses its children; its inner `text` node receives static attributes such as `strong` and recursively parses its text. This is confirmation of the existing **one authoritative C# parser** design, not approval of a legacy frontend fallback or a new parser engine. The previous complexity-gate decision above is **resolved**: preserve recursive rich tag discovery while retaining Markdown context across inline child tags.

Changes on `rich-ui-custom-controls`:

- Restored previously supported **single-box radio questions** using `RichQuestionGrammar` and the canonical C# validator for wrapper attributes, preserving the host-owned action callback and bounded option grammar. Restored the original supported sample and added an end-to-end fixture and unsafe-wrapper rejection cases.
- Restored static `textAlign` and `tabularNums` validation for `text` and `title`.
- In `RichDocumentParser`, inline presentation nodes are still parsed recursively, but their validated source spans are passed with surrounding text to the same C# Markdown converter. `RichMarkdownNodes` treats those spans as inert, source-preserving placeholder text during Markdig AST construction, then substitutes the already validated typed nodes. Thus `Before <text strong>inside</text> after` becomes a single Markdown paragraph containing the rich child; Markdown emphasis can span the child and list-item structure is retained. React continues to render typed data only. Agent command placement and all-or-nothing parse failure semantics are unchanged.
- Regression cases cover mixed paragraphs, recursive boxes, emphasis across inline tags, list items, previously accepted static attributes, boxed questions, fenced closing-tag decoys, and forbidden Agent command placement.

**Developer verification** at source/test commit `29c5f1d24e8c78b17278a3ad15599753ddf2a2ab` in dedicated verification worktree:

| Gate | Result |
| --- | --- |
| Canonical C# parser/security | **43/43 PASS**; 900 sibling containers bounded in 66 ms |
| Typed React SSR rendering | **32/32 PASS** |
| Real Chromium interactive DIL/host state | **PASS** |
| Agent protocol + Executor security/side-effect checks | **PASS** |
| Shared Agent Web UI/SSR | **19/19 PASS**, shared wiring PASS |
| Agent architecture guard | **PASS** |
| `Mezhs.Agent.Api` Release build | **PASS**, 0 warnings, 0 errors |
| `Mezhs.Agent.Web` production build | **PASS**, TypeScript and Vite |
| Portable Codegraph | Regenerated to **287 files, 743 edges** |

These are **developer** checks. A *new* independent High-risk code reviewer must evaluate the corrected PR SHA, followed by a **different independent tester** only on review PASS. Neither gate is yet satisfied for the revised implementation. The PR must remain unmerged; owner approval is required for merge/release; do not edit VERSION or CHANGELOG here.

## Independent High-risk third review FAIL and security corrections (2026-11-14)

Independent reviewer `exec_aafa62ca466748279aac8252bf3460ab` (chat `chat_75b6b5fba8bd4d17965630175fb0b8b5`) reviewed exact SHA `d6487bc3f53aa984b95d3be64d7e00b43952365a` against release-only main base `179c0da50a17e7024d76eaa2571cb6ee2498348e` and returned **FAIL**. The reviewer reported two static code-path findings; it did **not** execute an unauthorized shell command:

1. **P1 — Markdown blockquotes could grant command authority through lazy continuation lines.** `RichDocumentParser.MarkdownLiteralMask` now masks canonical Markdig `QuoteBlock` spans, including lazy continuation lines without a `>` marker. The same recursive C# parser remains the sole authority; commands are not admitted inside protected quoted Markdown. C# tests cover standalone and nested quote examples, a following valid command, and quoted `DONE`. Real Agent integration asserts that quoted SH bodies create zero Shell records and zero filesystem side effects.
2. **P2 — opening rich tags inside fenced code affected container matching.** `FindContainerClose` now ignores literal-protected opener and closer tokens during nesting scan; regressions include both fenced `<box>` opening and `</box>` closing decoys, with the surrounding structural box accepted. A Markdown lazy quote that absorbs a closing `</box>` fails closed; a separated quote and true structural close are accepted.

Corrective branch commits include `996ff5b`, `9e91b67`, `a3d2d15`, `516189c`.

**Developer verification** at `516189c45379d4e49b0e1f1da711bd2c0adba98d`:

- Canonical C# parser/security: **47/47 PASS**, including the above quote/fence cases.
- Agent protocol/Executor integration: **PASS**. Negative side-effect checks confirm quoted commands inside and outside structural boxes created no shell execution records or filesystem side effects. Valid ordered shell batches and error reporting also passed.
- Explicit parsed-document boundary checks: terminated nested quote valid; ambiguous quoted `</box>` invalid with `Unclosed box container`.
- Remaining renderer/build suites were **rerun successfully on the revised SHA**, as recorded below.

The independent review status for this corrected head is **pending**, and separate independent *final testing* must not start before a new reviewer PASS. No merge/release/owner approval has occurred; version and changelog remain unchanged.

### Final developer verification for third-review corrections (2026-11-14)

Following the third review's P1/P2 corrections, all gates were rerun in the isolated `D:\Projects\Mezhs-rich-ui-verify53` worktree at code commit `516189c45379d4e49b0e1f1da711bd2c0adba98d` and the subsequent plan-only commit `7583095fccf74fde192b2a24ce4073f680cc1f5e`:

| Verification | Result |
| --- | --- |
| Canonical C# parser/security | **47/47 PASS**, including lazy blockquotes and fenced rich openers/closers |
| Agent protocol + real Executor | **PASS**, including quoted examples with **zero Shell records and filesystem side effects** |
| Typed React SSR | **32/32 PASS** |
| Real Chromium UI/host-bound actions | **PASS** |
| Shared Agent Web tests + structural wiring | **19/19 PASS** and wiring PASS |
| Architecture guard | **PASS** |
| Agent API .NET Release build | **PASS (0 warnings, 0 errors)** |
| Agent Web TypeScript/Vite production build | **PASS** |
| Generated Codegraph | **287 files, 743 edges**; worktree/committed Git blob hashes both `acf35af805ebe416e2187177e741006ddf0eb376` |
| Nested quote boundary | Separated quote + structural `</box>` valid; ambiguous quote that swallows closing delimiter **fails closed** |

The above remain **developer-only verification**. Request another independent review on the final exact branch SHA. Only if it passes, request separate independent final testing by a different agent. Never merge without explicit owner approval; do not edit version or changelog.

## Fourth independent High-risk review FAIL and corrective verification (2026-11-14)

Independent reviewer `exec_05882f077d9f40ae8c708c86155bfc83` (chat `chat_1dcf96db6a9349a8ac99afc2dfb97cb1`) reviewed SHA `ee68192a529f84dc69cf225d816676c57460c927` against release-only base `179c0da50a17e7024d76eaa2571cb6ee2498348e` and returned **FAIL** on five static source findings. No unauthorized execution was reproduced by that reviewer.

1. **P1 — multiline Markdown emphasis and link labels could expose Agent commands.** The canonical C# parser now derives `EmphasisInline`/`LinkInline` context from Markdig and does not promote commands in these contexts. For multiline emphasis interrupted by a source-level HTML-like command line, a conservative paired-delimiter authorization guard additionally blocks command promotion without introducing a second rendering grammar. Security tests include midline, paragraph-ended, standalone multiline bold/italic and link-label examples, adjacent valid command candidates, and real Agent/Executor runs with zero prohibited Shell records and filesystem side effects.
2. **P2 — deeply nested Markdown list command indentation rejected.** `Standalone` now permits more than three leading spaces only when the canonical Markdig `ListItemBlock` AST contains the candidate source offset. Root-level four-space indented code remains inert. Parser and real Agent Executor tests verify a correctly indented nested-list shell executes once.
3. **P2 — repeated inline replacement scanning was quadratic.** `RichMarkdownNodes` now sorts replacement source offsets once and binary-searches for replacements overlapping each literal node; the 600 alternating rich/Markdown element regression passed in 23–26 ms with approximately 2,568 KiB of managed allocations during developer verification.
4. **P2 — provisional frontend Markdown fallback parsed Markdown a second time.** `MarkdownContent` no longer imports `react-markdown` or `remark-gfm`; it displays escaped source text for provisional/streamed content until the canonical server document is available. Shared-UI/architecture assertions and React tests now enforce single-ownership behavior.
5. **P2 — autolinks lost navigability.** `RichMarkdownNodes` now extracts `AutolinkInline` URLs, including mailto email autolinks, applying the existing scheme/length allowlist. React regressions verify safe URLs and visible labels while unsafe destinations remain inert.

**Developer verification and evidence:**

| Check | Result |
| --- | --- |
| Canonical C# parser/security at `ee57dbaac17b1e3f01730c1bafbbacd666afe08e` | **57/57 PASS** (including the four additional emphasis edge cases) |
| Agent protocol and real Executor at `ee57dba` | **PASS** (no Shell/filesystem effects for Markdown quotes, emphasis and links; only authorized shell runs; nested-list shell runs; durable order and continuation PASS) |
| React SSR rendering at `1551160` (no production code modified afterward) | **34/34 PASS**, including autolinks and inert provisional rendering |
| Real Chromium at `1551160` | **PASS** |
| Shared Agent Web UI/SSR at `5c962cc` (test guard corrected) | **19/19 PASS**, wiring PASS |
| Agent architecture at `ee57dba` | **PASS** |
| Agent API .NET Release build at `1551160` | **PASS**, 0 warnings and 0 errors |
| Agent Web TypeScript/Vite build at `1551160` | **PASS** |
| Codegraph export at `a6e711b` | **287 files, 741 edges**, generated Git blob exactly matches committed `ca10c6df366583eebcbbaae26960c3b382760b0c` |

These are **developer** checks, not independent review or testing. Obtain a new independent High-risk code review against the exact updated plan commit; only a **PASS** unblocks a **different independent final tester**. No merge, release, version or changelog action without explicit owner approval. Main remains release-only.

## Fifth independent High-risk review FAIL and scoped Markdown-context fix (2026-11-14)

Independent reviewer `exec_18d206ce82e54185a2a09b3364625ed8` (chat `chat_59044c095c034c8195f44e259494b8cb`) reviewed exact SHA `7b7226ce5e323761eb32cf1e0db26e9afcb84850` against release-only base `179c0da50a17e7024d76eaa2571cb6ee2498348e` and returned **FAIL** on two static code-path issues; the reviewer did not demonstrate unauthorized shell execution.

1. **P1 — missing Markdown inline authorization context inside rich layout containers.** The full-document Markdig AST may treat `<box>` as an HTML block, hiding inner emphasis and link-label ranges. `RichDocumentParser.MarkdownLiteralMask` now gathers and merges local `EmphasisInline` and `LinkInline` authorization scopes, plus the existing protective multiline delimiter guard, into the original UTF-16 source-position masks during each `FindContainerClose` scoped validation. Commands inside emphasized or linked examples in nested `box`/`row` are now inert; valid adjacent commands remain authorized.
2. **P2 — nested Markdown list commands rejected inside rich layout.** Per-container scoped Markdig validation also merges `ListItemBlock` spans into the same global source positions. A four-space continuation indent inside a two-level list within `box` or `row` now qualifies as legitimate list indentation; four-space root code remains inert.

Corrective commits: `a0d8d9d` (scoped context propagation), `ea8500d` (six combined parser security/correctness regressions), `edce368` (real Agent/Executor negative and positive side-effect checks). No legacy frontend parser has been reintroduced.

### Developer verification after fifth-review corrections

All below were run from the dedicated verification checkout at exact source/test HEAD `edce3680827482baadfb3256dd005d10cc4f07a3`:

| Gate | Evidence |
| --- | --- |
| Canonical C# parser/security | **63/63 PASS**, including Markdown emphasis/link-label and nested lists inside `box`/`row` |
| Agent protocol + real Executor | **PASS**; nested layout emphasized/link-label shell examples created **no Shell records or filesystem side effects**; valid neighboring shell and nested-list shell each ran **exactly once**; rejected batch, ordering, and continuation regressions PASS |
| Typed React SSR | **34/34 PASS** |
| Real Chromium | **PASS** |
| Shared Agent Web UI/SSR and wiring | **19/19 PASS** and wiring PASS |
| Architecture guard | **PASS** |
| Agent API Release build | **PASS**, **0 warnings, 0 errors** |
| Agent Web TypeScript/Vite build | **PASS** |
| Codegraph | **287 files, 741 edges**; generated and committed blob hashes both `ca10c6df366583eebcbbaae26960c3b382760b0c` |
| `git diff --check` | **PASS** |

This remains **developer verification only**. Request a fresh independent High-risk code review of the exact branch SHA after this plan update. Only independent reviewer **PASS** permits a **separate, different independent final tester**. Owner approval remains mandatory before merge. Main is release-only and VERSION/CHANGELOG are untouched.

## Sixth independent High-risk review FAIL and opaque scope/performance correction (2026-11-14)

Independent High-risk reviewer `exec_7dec1eda81a74e359626489317117cb2` (chat `chat_782f40ba2e8743428c339ebce54b8a27`) inspected exact source SHA `78a705a2c2f385c97a803051c9581cee7ab6d7d9` against pinned release-only base `179c0da50a17e7024d76eaa2571cb6ee2498348e`. **Verdict: FAIL**; two code-path findings, not demonstrated runtime failures:

1. **P1 — quadratic Markdown scans over consecutive raw editors.** The previous `FindOpaqueClosing` for `WritingBlock` parsed the remaining parent suffix for every editor, letting thousands of siblings trigger O(N²) work. The corrected parser checks only the body prefix through prospective closing candidates, shares an **8,000,000-character cumulative Markdown validation budget**, and uses source-position-preserving opaque-body shielding. A **1,500 WritingBlock sibling** adversarial regression passed in **124 ms**, approximately **8,531 KiB** allocated, within explicit time/allocation limits. If ambiguous candidate closers exceed the cumulative budget, the parser fails closed rather than granting authority.
2. **P2 — Markdown markers inside a completed opaque block could suppress later authorized commands.** The parser now constructs a same-length masked Markdown view that replaces contents of validated `CodeBlock`, `WritingBlock`, `escape`, `SH` and `ATTACH` bodies with whitespace while keeping source offsets and line boundaries. Markdown literal masking uses this protected view so unmatched fences/backticks do not conceal real `SH`/`DONE` after a valid closing delimiter. Original-source Markdown emphasis and list contexts are preserved separately, and rich layout scopes (`box`, `row`) are recognized during opaque discovery; malformed/ambiguous closers remain fail-closed. Existing source text and executor command bodies are never rewritten.

Corrective commits: `6ff7bb8` (opaque shield and bounded Markdown work), `0ce0b0c` (boundary test cases), `d11a1ad` (raw sibling performance/allocations), `366bd87` (preserve original inline context), `ce22472` (fix suite count), `5bbb690` (real Agent positive and negative opaque side-effect checks), `efae6a4` (rich layout scope during discovery), `660a87b` (quoted-shell/layout regressions).

An earlier Agent integration run at `5bbb690` **failed** with `Unexpected closing tag: SH` in a quoted-block-in-box fixture, although 72/72 parser tests passed. The scope correction `efae6a4` and new nested quote regressions `660a87b` addressed that observed failure; the Agent integration was rerun and **passed**, so the unsuccessful prior attempt is not counted as verification.

### Complete developer verification after sixth-review corrections

All checks below were executed from isolated worktree `D:\\Projects\\Mezhs-rich-ui-verify53` at source/test SHA **`660a87b8d13d72860205a4e8f3874393abbe144d`**:

| Verification | Observed result |
| --- | --- |
| Canonical C# parser/security | **76/76 PASS**, including opaque marker boundaries, Markdown/rich box quotes and 1,500 WritingBlock siblings |
| Agent protocol and real Executor | **PASS**; six new completed opaque-boundary cases confirmed subsequent authorized SH/DONE ran once, shell-shaped content inside opaque scopes caused **zero Shell records and filesystem side effects** |
| Rich React typed SSR | **34/34 PASS** |
| Actual Chromium host-bound UI | **PASS (1/1)** |
| Shared Agent Web UI/SSR and wiring | **19/19 PASS**; wiring PASS |
| Agent architecture guard | **PASS** |
| `Mezhs.Agent.Api` Release build | **PASS**, **0 warnings, 0 errors** |
| Agent Web TypeScript/Vite production build | **PASS** |
| Codegraph | **287 files, 741 edges**; generated and committed blob hashes both `ca10c6df366583eebcbbaae26960c3b382760b0c` |
| `git diff --check` | **PASS** |

These are **developer checks only** and do not satisfy the independent review gate. Repeated static review findings in parser boundaries warrant the mandated independent scrutiny of compositional security and work budgets. Obtain a seventh independent High-risk code review of the *new exact SHA after this plan update*; if and only if **PASS**, assign a **different independent final tester** on that same SHA. Never merge/release without explicit owner approval; main remains release-only; do not change VERSION or CHANGELOG in this PR.

## Seventh independent High-risk review FAIL — mandatory complexity escalation (2026-11-14)

Reviewer `exec_16f324478f9941949dd96e4cd9685441` (chat `chat_b03084117bc241a9bfadc3dd44dc5795`) reviewed exact source SHA `52e04cc711592ea30809918b37dd6c962f8e4bb6` against pinned base `179c0da50a17e7024d76eaa2571cb6ee2498348e` and returned **FAIL** with three source-level findings:

- **P1 security — compositional opaque shielding risk.** `ShieldOpaqueBodies` can treat an apparent `SH` opener inside Markdown emphasis/link text as an opaque block even though later `ReadChildren` treats that opener as inert. Shielding its body can erase Markdown fences that would otherwise protect another command-shaped opening. This is a **static code-path finding**, not a reproduced exploit on the reviewed feature SHA.
- **P2 source-map integrity — private-use Unicode placeholder collisions.** `RichMarkdownNodes` injects `U+E000`/`U+E001` placeholders and locates them using `IndexOf`; model-authored identical characters may misassociate literals, node IDs or UTF-16 source spans.
- **P2 radio correctness — Markdown code delimiters.** `RichQuestionGrammar` uses unconditional `IndexOf("</radio>")` and `IndexOf("</button>")`, and label validation rejects tag-shaped content even when Markdig would treat it as code. Supported literal code labels can fail canonical question parsing.

**Separate observed runtime incident:** During the review execution, MEŽS runtime evidence for host shell execution `7128` reported an unintended `SH` dispatch of text intended as an inert fenced example, with cmd.exe errors on Markdown markers and exit code **255**. The runtime's exact deployed parser SHA and complete command/side-effect history were not established. This incident is **not proof** of the proposed P1 defect in feature SHA `52e04cc`, and absence of prior side effects has **not** been established. No further live adversarial command payloads should be sent through an SH-authorized review agent.

### Complexity escalation decision — OWNER ACTION REQUIRED

Per `.agents/workflow.md`, after repeated fixes driven by the same architecture **stop affected work and obtain an explicit owner decision before adding complexity**. The approved goals include a single canonical recursive C# rich UI parser, mixed Markdown/rich layout, inert presentation source, and executable Agent command blocks inside the same stream. Their compositional source-context and authorization guarantee now requires multiple overlapping masking/prepass/scoping mechanisms, each introducing new correctness/security edge cases.

Potential directions to decide:
1. **Redesign command transport (recommended):** keep the single canonical C# parser and typed React UI for display, but make executable Agent commands a distinct structured, explicitly authorized channel instead of discovering authority from mixed Markdown/display text. This changes the Agent protocol/API boundary and needs an updated plan and independent review.
2. **Retain the existing mixed command syntax and guarantee:** authorize a formal canonical source-token/scope design with explicit work budgets rather than incremental special-case masking; larger implementation and long-term maintenance cost, requiring an approved architecture revision.
3. **Relax supported syntax/behavior:** reject commands for ambiguous or Markdown-rich responses, disable command extraction in risky mixed contexts, or constrain where commands may appear. Lower parser complexity but reduced functionality/compatibility.
4. **Stop or defer PR #53** until the command channel architecture is resolved.

**No further source changes, seventh-review corrective tests, or independent final testing have been undertaken following this escalation.** Document owner decision before resuming. The PR remains unmerged and release-only main is unchanged; no VERSION/CHANGELOG edits.

## Owner architecture decision: two independent tag discovery controls (2026-11-14)

Owner explicitly approved continuing PR #53 using a **single recursive C# canonical parser with independent per-tag controls**:
1. `SearchChildren`: whether a recognized tag searches/parses its body for nested controls, or treats its body as completely opaque.
2. `AllowAsChild`: whether a tag may be recognized when another tag searches its children, independently of whether the tag itself searches its contents.

No blanket top-level-only Agent command restriction has been approved. Preserve previously valid nested SH/ATTACH/DONE under structural layout when the registry permits it and keep policy validation and fail-closed semantics. `SH`, `ATTACH`, `DONE`, `escape`, `CodeBlock`, `WritingBlock` must not parse their bodies as children; richly nested layout tags do. Make the two behaviors explicit in tag metadata and use them throughout the canonical traversal. Provisional rich markup does not execute on the frontend.

The owner decision resolves the workflow complexity escalation by retaining the mixed source grammar but demanding a simpler per-tag traversal/child-recognition contract. Before independent code review: fix the seventh static findings (opaque prepass versus inert Markdown, collision-safe inline placeholders, radio delimiters inside Markdown code), add adversarial non-execution and legitimate sibling/nested-command coverage with real Executor side-effect assertions, and run all developer checks. A different independent code reviewer then assesses the *exact final SHA*. Only a reviewer PASS permits a separate independent final tester. No merge, version, changelog or release without explicit owner approval.

## Developer correction and verification after seventh review (2026-11-14)

Owner-approved independent per-tag controls are in canonical C# `RichTagDefinition`/`RichTagRegistry`:
- `SearchChildren` determines whether the tag searches its own body for subcontrols.
- `AllowAsChild` determines whether another searchable element can discover the tag as a child, **independent** of its own recursive search behavior.
- Container tags such as `box`, `row`, `col`, `grid` allow recursive display parsing; `SH`, `ATTACH`, `CodeBlock`, `WritingBlock`, `escape`, `DONE` do not search their bodies. `SH`/`ATTACH` may still be discovered inside authorized structural layout; `DONE` is root-only. Policy validation and fail-closed document behavior remain separate from both controls.
- The parser aligns early opaque shielding with the original-source Markdown emphasis/link authorization context, avoiding promotion of command-looking text from Markdown examples.

The seventh review's additional correctness findings were corrected:
- `RichMarkdownNodes` selects a pair of source-absent BMP private-use markers before substituting inline rich nodes, preventing collisions with authored literal U+E000/U+E001 data and maintaining UTF-16 spans/IDs.
- `RichQuestionGrammar` checks candidate radio/button closing delimiters against canonical Markdown literal context after normalizing label indentation and restores original positions. Inline code containing delimiter-looking characters remains display data.

Regression additions assert the independent properties, Markdown emphasized/fenced commands at root and in nested `box`/`row`, UTF-16 node positions/IDs after a model-authored placeholder collision, and code-literal radio/button delimiters. Real Agent/Executor integration verifies that three new root/box/nested-box-row adversarial Markdown examples cause **no unauthorized Shell records or filesystem side effects** while the legitimate adjacent shell executes **exactly once**.

A previous verification at `23b0acaf` was **FAIL**, with 81/83 parser cases (radio label indentation) and a PowerShell syntax error preventing Agent integration. The errors were corrected in commits `a1174456` and `9d810c1`. The subsequent exact source/test SHA `9d810c11eb1133535229771b578e895f0541c8ae` passed all developer checks below.

| Verification | Observed result |
| --- | --- |
| Canonical C# parser/security/performance | **83/83 PASS**; 1,500 WritingBlock siblings in 120ms, ~8,531 KiB allocated; 900 sibling containers 120ms; 600 alternating inline nodes 77ms |
| Real Agent/Executor | **PASS**, including negative side-effect and authorized exactly-once assertions |
| Typed React SSR | **34/34 PASS** |
| Real Chromium | **1/1 PASS** |
| Agent Web shared UI | **19/19 PASS**, including direct formatting regression suite; wiring PASS |
| Agent architecture guard | **PASS** |
| Agent API Release build | **PASS**, 0 warnings, 0 errors |
| Agent Web TypeScript/Vite production build | **PASS** |
| Codegraph generated/committed | **287 files, 741 edges**; blob hashes both `ca10c6df366583eebcbbaae26960c3b382760b0c` |
| `git diff --check` | **PASS** |

Codegraph exporter left a locally modified `.agents/codegraph/graph.json` due to the observed LF-to-CRLF working-tree conversion warning; `git hash-object` nevertheless agreed with the committed tree blob. No Codegraph content update or main-checkout change was required.

**Gate:** These checks are developer evidence only. Request a new independent High-risk code review of the exact SHA after this documentation update, against pinned base `179c0da50a17e7024d76eaa2571cb6ee2498348e`. Only reviewer PASS permits a different independent final tester on the same exact SHA. Never merge/release without explicit owner approval; main is release-only; VERSION and CHANGELOG stay untouched.

## Independent tester FAIL, same-branch corrections, and review reset (2026-11-15)

**Context and workflow stage:** A separate High-risk Code Reviewer recorded a **PASS for exact SHA `70f5358b5d90c41c2bf80191c79ec014f9ba0726`** against pinned base `179c0da50a17e7024d76eaa2571cb6ee2498348e`. That review applied **only to that old SHA**, not to later corrections. The ensuing independent Final Tester found concrete FAIL results on the reviewed old candidate: canonical C# parser **92/95**, typed React SSR **30/36**, shared Agent Web formatting **17/19**, headless Chromium **0/1**, and `git diff --check` (trailing blank line in `Style.ts`). The real Executor side-effect suite and backend build passed on the old candidate, but do not certify the corrected SHA.

**Observed faults:** The canonical phrasing validator rejected GFM `taskCheckbox` children in Markdown paragraph/list contexts, and radio `questionButton` labels had a single Markdig paragraph wrapper incompatible with the native button's phrasing-content requirement. The Chromium fixture subsequently failed with an absent Agent transcript question. These were observed failures and source-level correlations; the correction passing tests alone does not establish a unique root cause of unrelated Electron/provider failures.

**Corrective commits on the existing `rich-ui-custom-controls` branch** (no branch switch or main commit):
- `5aa10e48f19ffb2d792557906401a792eae10ac3`: permit GFM `taskCheckbox` in the canonical phrasing-only content model while preserving the rejection of nested interactive descendants in link/button contexts.
- `0943bf75c67fe8ef7eb2f9663026630c95ac23b9`: unwrap *only a single* Markdig `paragraph` for canonical `questionButton` label children, retain source spans, and leave multi-block invalid labels to canonical validation. This keeps the native button's children phrasing-only.
- `7bf02e5f56916455292f4e3eafe794805cfdbf0e`: remove the blank EOF line in `src/Mezhs.Web.Lib/src/rich-elements/Style.ts`.
- `e7dda440d3365e7fac27fe8b95e0d7e45ef9473b`: add C# parser regressions for checked/unchecked GFM task checkboxes and button phrasing children.

**Pre-review regression/developer checks against exact corrected source SHA `e7dda440d3365e7fac27fe8b95e0d7e45ef9473b`:**

| Area | Observed result and execution ID |
| --- | --- |
| Canonical C# parser, negative security, radio and GFM fixtures | **95/95 PASS**; MEŽS host execution **7243** |
| Typed React server rendering | **36/36 PASS**; **7255** |
| Agent Web rich formatting / shared wiring | **19/19 PASS** plus shared static checks; **7256** |
| Headless Chromium, real DOM interaction, owner-only new-turn dispatch | **1/1 PASS**; **7260** |
| Real Agent API/Executor and shell side effects | **PASS**; **7297**. The integration harness ran from a temporary copy with the configured Electron directory redirected to a verified nonexistent path so accidental browser startup would fail safely. The test used the scripted mock integration and real Executor; rejected/inert examples created no Shell records or filesystem effects, and authorized shells ran exactly once with durable records. The temporary script was removed; no tracked source changed |
| Architecture guard and Agent API Release | **PASS**, build **0 warnings/0 errors**; **7261** |
| Agent Web TypeScript + Vite production build | **PASS**; **7257** |
| Pinned-base `git diff --check`, detached testing worktree and `main` cleanliness | **PASS**; **7242**, **7262** |
| Temporary testing worktree cleanup | Fully removed without touching other worktrees; **7325** |

**Important stage classification:** These are real observed passing regression checks, but the Final Tester itself authored the corrections. They therefore count as **pre-review corrective/developer evidence**, **not** a completed post-review independent High-risk final-testing gate.

**New review attempt and blocker:** A distinct High-risk reviewer was started via the MEŽS Agent API for corrected SHA `e7dda440...` (`exec_6afb81640dbb4851a3734c2ffcf91cb4`); execution **FAILED before reviewing code** with `Electron provider operation failed: ChatGPT native requirements provider was not observed in this document (resourceEntries=92, cachedProvider=false)`. This is an *infrastructure/provider blocker*, **not** a review FAIL or PASS. The current available low/mid/high policies all use `chatgpt-sub` on the same Electron transport; avoid repeatedly launching this failing provider until its availability is restored.

**Current High-risk gates:** Independent Code Reviewer on *the next exact branch HEAD* **BLOCKED/PENDING**; independent Final Tester **NOT STARTED** after a new reviewer PASS. The corrected-SHA pre-review regression suite **PASS**. The source changes above do not introduce a new approved architecture or invalidate the recorded owner decision on independent `SearchChildren` and `AllowAsChild`. Plan bookkeeping is kept here as the source of truth, without restarting plan review.

**Next handoffs:** Verify the new exact SHA after this plan-only update and pin base `179c0da50a17e7024d76eaa2571cb6ee2498348e`; obtain a **fresh independent Code Reviewer PASS** on that exact revision using a working provider. Only then create a **different independent Final Tester** to rerun appropriate acceptance, security/side-effect, browser, build and regression checks. Record independent evidence, then ask the owner explicitly before merging. No merge/release/VERSION/CHANGELOG changes.

**Proposed release summary (owner approval pending):** Introduce canonical C# rich UI/Markdown/Agent command parsing and validation, typed React controls (including radio questions, raw editors and bounded SVG), and fail-closed host-command authority with regression coverage. Proposed change type: **MINOR** subject to compatibility assessment during review; finalize version/changelog only at an explicitly approved release.

## Owner-directed follow-up simplification (2026-11-15)

The owner requested reducing model-facing rich UI instructions, removing redundant static presentation attribute lists from C#, replacing Markdown type-name strings with typed patterns, consolidating React controls under `rich-elements/`, and simplifying the SVG bridge. This supersedes the earlier plan's per-presentation-tag C# attribute schema: the canonical C# parser still validates bounded, literal attribute syntax and blocks event/DOM injection; presentation components interpret only their own inert namespaced data and ignore unknown values. Strict C# attribute contracts remain for SVG, raw editors, `escape`, and Agent commands. SVG retains a last-mile React tag/property allowlist to protect the DOM sink. The one C# canonical parser and Agent fail-closed command authority are unchanged. Regression checks cover unknown inert attributes alongside forbidden injection, SVG/raw namespaces, command extraction, real Executor and Chromium rendering.

## Independent review FAIL and Chromium test correction (2026-11-15)

- Independent High-risk reviewer on `578797536c191b30d1b0e2b0b70609e87bacb9cb` returned **FAIL**, review `PRR_kwDOT4SYRc8AAAABYF145g`: GitHub Actions run `43734303180` failed the unrelated-looking inline `<title>` assertion because `tests/chat-ui-browser.test.cjs` passed no typed `document` to `MarkdownContent`. The canonical renderer deliberately keeps document-less provisional markup inert; no actual Title renderer block-layout defect was established.
- Corrected browser fixture now builds its completed rich title document through the shared C# test helper, injects only its serialized typed DTO into the Chromium test, asserts rendered inline/tabular title behavior, and separately proves provisional document-less source remains literal and does not mount a control. This is a test contract correction, not a reintroduction of a JavaScript parser or a production behavior change.
- Pending gates: rerun Chrome regression and all relevant CI stages on the corrected exact SHA; fresh independent High-risk code reviewer must PASS on any new commit, followed by a distinct independent final tester and explicit owner approval before any merge into main.

## Eighth independent High-risk review FAIL and policy smoke correction (2026-11-15)

- Independent reviewer examined PR #53 at exact HEAD `61f8b1293e235b60a13934919605f2c3c2197386` against base `cf7cd360e0dd7b2d835f9315a77e1b5c92ea47a9` and recorded **FAIL** in [review 5912373579](https://github.com/DreigoUndead/Mezhs/pull/53#pullrequestreview-5912373579). GitHub Actions [run 43736150527](https://github.com/DreigoUndead/Mezhs/actions/runs/43736150527) passed the rich UI/real Chromium regression and Agent command/Executor smoke, then **failed** at `tests/policy-decoder.ps1` with `CommandParseException: SH must begin on its own line`. Subsequent CI jobs were **skipped**, not passed.
- The smoke had treated `normal.ModelInstructions` (plain instructional prose including inline protocol examples) as an Agent reply by calling `Parser.Parse(...)` and expecting an empty command list. The canonical Agent reply parser intentionally rejects malformed/non-standalone protocol tags, so this test contradicted the fail-closed grammar. No unauthorized shell execution was demonstrated.
- Corrective commit `f226dc37b7e9d1736882c11397caa01fe0c95f5a` updates **only the policy smoke**, without changing parser behavior: use the canonical `RichDocumentParser` to assert model instructions yield **zero executable command candidates**, and add a regression where inline instructional protocol prose precedes a standalone-looking `SH` block, requiring a globally invalid document with **zero commands**. The existing positive standalone `SH`/`DONE` and Agent policy/Executor checks remain intact.
- This correction is **developer/pre-review work**, not independent reviewer PASS or independent final testing. Rerun the corrected policy smoke and complete CI on the final exact SHA; review any failures before requesting a **fresh independent High-risk Code Reviewer**, followed by a **different independent Final Tester** only after reviewer PASS. Explicit owner approval is still required to merge. No version/changelog/release changes are authorized.

## CI merge-base reconciliation and actual policy smoke failure (2026-11-15)

- Reruns [#656](https://github.com/DreigoUndead/Mezhs/actions/runs/43751700456) at `8b9c5cddd65eb0053981d424609cf42818d5af44` and [#657](https://github.com/DreigoUndead/Mezhs/actions/runs/43752357212) at `b4c9017cbd150e3547cb25782bcd93579e478ce6` both passed the solution build, real Chromium and Agent protocol/Executor smoke but failed at the policy test; downstream jobs were skipped. The diagnostic-only commit `b4c9017` did not resolve the failure.
- The actual CI checkout for run #657 was synthetic merge `250c2fe293bf21924fb75bd0419b6fb1e74b8ef7`: feature `b4c9017` merged with newer `main` `b3c60fb00bfb842213aa0c5fe615a9f5c2a14032`. This newer `main` had added a **different** policy-smoke assertion at generated `Program.cs:56`, `new Parser().Parse(policy.ModelInstructions)`, which did not exist in the original feature-only file. Thus the review's source-level location was incomplete for the actual tested merge; earlier corrections to `normal.ModelInstructions` could not fix the extra default-policy assertion.
- Verified that `250c2fe` is a fast-forward descendant of both the feature head and newer main. Advanced **only** `rich-ui-custom-controls` to that merge commit; no update to `main` occurred. Corrective commit `cc5917b547543d198684e8e14e3af40a414757b9` changes the newly integrated default-policy smoke to use `RichDocumentParser` and assert **zero executable command candidates**, matching the fail-closed policy for arbitrary explanatory instructions. No production parser behavior was weakened.
- The merged feature branch incorporates current released-main dependencies, so the updated policy test is now meaningful both in the feature worktree and pull-request CI merge. The exact post-correction CI result, independent High-risk code review and independent final testing remain **pending**; do not represent old developer CI or prior reviewer PASS as approval. Continue under the existing approved architecture, then seek explicit owner permission before any implementation merge/release.

## Subsequent Agent API smoke regression and test-scope correction (2026-11-15)

- [CI run #660](https://github.com/DreigoUndead/Mezhs/actions/runs/43753190142) on `b5ce11908a09ab31478ccee928a3842827d1cdea` **passed** the corrected `tests/policy-decoder.ps1` test (`PASS: typed policy rules...`) and prior .NET/Chromium/real Executor stages, then **failed** in `tests/agent-api-smoke.ps1:190` with `Agent API changed prompt numbering or whitespace`. Downstream CI checks were skipped, so #660 is not a green run.
- The API smoke used the **last** user message in the Agent chat as a proxy for the original request. That is not guaranteed after an Agent correction/continuation turn. `AgentPromptBuilder.BuildInitial` embeds the raw task in the first prompt, whereas later user messages can be correction or result prompts. The mock integration echoes its input; a source-level possibility is that echoed instructional protocol samples trigger canonical reply correction, but #660 did **not** establish the exact conversation sequence or prove whitespace was mutated.
- Corrective test commit `689478835919a1d08ee40d6d9adf3100f2e50f16` retains strict `execution.request` equality with the original numbered/trailing-whitespace input, and now requires **exactly one** user message to contain the raw original input, independently of later correction turns. No production message storage, prompt builder, canonical parser, or Agent API authority contract was altered.
- The current CI and independent review gates remain pending on the exact subsequent documentation HEAD. If the new test fails, inspect actual returned request/message values before further changes; do not infer a cause solely because a correction passes. High-risk review, distinct independent final testing, and explicit owner approval remain mandatory.

## Environment shell audit smoke fixture correction (2026-11-15)

- [CI run #662](https://github.com/DreigoUndead/Mezhs/actions/runs/43753948517) on `d73bd4999639831d228d7c25fc3ae10d06550ece` passed canonical policy tests, solution build, real Chromium and Executor smoke, and advanced past the earlier exact-prompt assertion. It then **failed** in `tests/agent-api-smoke.ps1` at `Environment execution produced no shell audit row`; later stages were skipped.
- Source inspection found that this environment command fixture used connection `test` with ordinary `mock` integration, whose response is `Echo: ` followed by the **entire input prompt**, including inline explanatory Agent protocol tags in its bootstrap. Such echoed instructions are not a reliable valid assistant command response under the newly approved fail-closed grammar. This is a plausible explanation for missing shell evidence, not independently proven from #662's truncated execution evidence.
- Existing `tests/agent-protocol-result.ps1` independently demonstrates the intended test isolation: it selects `mock-agent-scripted` to emit only command-shaped assistant replies rather than an echo of bootstrap instructions. On the same feature branch, `ac71156c8bb55688e3a588337f62db75c4fd7699` adds a dedicated `test-scripted` connection to `tests/mezhs.agent.core.test.yaml`, and `6ded9ff99a0f88e4c1627692706a4206db1752b2` directs **only** the environment-shell fixture to this scripted connection. Other ordinary mock interactions and production parsers are unchanged.
- The smoke still requires a real Shell execution audit row, the `ENVIRONMENT_OK` result and canonical SH protocol metadata/display stripping; the expected evidence was **not** weakened. Rerun the complete required CI pipeline, confirm the exact final SHA, and require a new independent High-risk code review, a distinct independent final tester, and owner approval before merge/release.

## Parallel test-helper compilation race in CI (2026-11-15)

- [CI run #665](https://github.com/DreigoUndead/Mezhs/actions/runs/43754442171) for `7f9419f565fb9a274e48a7f926df465714189797` passed JavaScript/provider checks, the .NET solution build and migrations, but failed in the **Rich formatting and real Chromium regression** step. Most browser and SSR cases passed; one `WritingBlock` test failed during C# fixture setup with `MSB3101`/`CS2012` because another process held `tests/Mezhs.RichUi.TestApp/obj/Release/net10.0/Mezhs.RichUi.TestApp.dll` and its assembly-reference cache. Later stages, including the newly corrected environment-shell smoke, were **skipped**; their result on #665 is unknown.
- Existing `tests/helpers/canonical-rich-document.cjs` lazily builds the shared .NET test helper when its DLL is absent. The CI workflow starts several Node test workers in parallel, so two workers can invoke `dotnet build` in the same output directory. This is direct log evidence of concurrent compiler output lock contention; it is not a demonstrated production parser fault.
- Corrective CI commit `1621c8dee687973dd32aa575befa44e4bda7f9b2` adds one explicit `dotnet build tests/Mezhs.RichUi.TestApp/Mezhs.RichUi.TestApp.csproj -c Release --nologo` before the parallel `node --test` command. This preserves all test cases, keeps the existing helper, and avoids introducing a new lock/scheduler mechanism.
- Rerun complete CI on final exact SHA; only independent High-risk code review PASS permits the distinct final testing stage. Never merge/release without explicit owner approval.

## Scripted-mock pre-cancellation contract correction (2026-11-15)

- [CI run #667](https://github.com/DreigoUndead/Mezhs/actions/runs/43754845060) at `114191f6874043eb775ba42463cff17097112a48` passed JavaScript/provider, .NET solution, storage, real Chromium, real Agent protocol/Executor, policy and Agent API smoke (including environment shell), Agent Web/proxy, architecture, and embedded-module regressions. It **failed** in the Browser limits/cancellation step at `tests/integration-cancellation-contract.ps1`; later packaging/concurrency/frontend artifact stages were skipped.
- Exact runtime evidence: `System.OperationCanceledException` escaped synchronously from `MockAgentScriptedIntegration.SendMessageAsync` line 11 when the cancellation token was already cancelled; the integration contract's `VerifyPreCancelledAsync` calls `var send = fixture.Integration.SendMessageAsync(...)` then awaits its cancelled task, and therefore could not catch the synchronous exception. This is a demonstrated Task contract mismatch in the PR-added deterministic test integration, not an Agent runtime command-execution defect.
- Corrective commit `2fe841858ed7f8f7840a2c84067d0a3225698cde` changes the pre-cancelled case to return `Task.FromCanceled<IntegrationSendResult>(cancellationToken)` rather than throwing before returning a task. The non-cancelled scripted result and production parser/Executor behavior are unchanged.
- Verify the full CI pipeline against the **new exact branch HEAD**. Developer CI does not replace independent High-risk code review and different independent final testing; explicit owner approval remains required before merging/releasing.
