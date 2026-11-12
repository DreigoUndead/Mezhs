# Rich UI Custom Controls
ID: rich-ui-custom-controls
Level: High-risk for 2026-11-11 architecture/Agent-command-parser redesign (prior Standard override applied to earlier feature scope)
Base: v0.3.0, dfb6053daf75fbffab7d35e00331b0a2dd2213dd

## Goal and scope
Render common model-authored interactive DIL-style content as safe declarative controls, without evaluating arbitrary JavaScript or giving model markup independent action authority. The motivating case is a four-choice Grill Me radio group with nested secondary text, a button icon, and a conditional choice-dependent message. Preserve the existing simple radio question and normal Markdown rendering.

## Approach
- Reuse RichQuestion for parsing and local one-state ownership, RichMarkup for validated static presentation, RichExpression for bounded expression semantics where suitable, and RichActionContext for host-owned submission. Avoid a second action or Markdown parser.
- Represent validated rich label/button children as bounded, inert render data, using the same safe presentational element and attribute rules as static rich markup. Support the motivating nested text and icon while retaining Markdown emphasis and line breaks.
- Recognize a finite expression grammar for the single GenUI.issueNewTurn argument: existing template literals plus choice comparisons, nested conditional expressions, and safe literal/string combination. Parse syntax, evaluate only approved operations against local choice, and bound expression depth and output length. Never invoke model-authored functions.
- Keep explicit component schemas and validation reusable for subsequent controls without implementing a general DIL runtime, arbitrary hooks, arbitrary HTML, CSS, scripts, network calls, or an extensible model-provided registry.
- Maintain parser-provenanced slots, Markdown code/escape shielding, finite state and choice limits, and existing chat/connection/busy/offline authorization. Reject unsafe or unsupported actionable structures atomically and render inert fallback content.
- Keep shared browser chat actions enabled only for completed eligible assistant messages; Agent transcript actions remain read-only.

## Acceptance criteria
- [ ] The original four-option Grill Me example renders four selectable radio options with the initial A selected, bold headings, secondary-size text, and the button arrow icon.
- [ ] Confirm produces the exact intended response for A, B, C, and D using conditional messages; no send occurs without a deliberate click.
- [ ] Existing one-state radio fixtures and nested static markup/SVG/citations behave unchanged.
- [ ] Safe styling/labels render without leaking raw JSX; unexpected attributes, unsupported expressions and nodes cannot create action authority.
- [ ] Keyboard/screen-reader radio semantics, focus, disabled/read-only states, and narrow layouts remain usable.
- [ ] Invalid code fences, Markdown code examples, escapes, raw HTML comments, fake slots, malicious JSX/actions, expression abuse, and oversized or nested input remain inert.
- [ ] Chat/connection switches, busy/offline state, repeated clicks, loaded conversations, composer drafts, uploads and Agent read-only views preserve existing host safeguards.

## Verification
- Add the exact motivating source as a fixture and automated parser/SSR assertions for safe labels, icon, and each choice-conditioned payload.
- Extend tests/rich-model-ui-rendering.test.cjs and tests/rich-model-ui-browser.test.cjs with real Chromium interactions, keyboard navigation, disabled/read-only cases and security/failure-path checks.
- Run the relevant rich UI and chat/provider regression suites, both frontend production builds, .NET Release build where affected, and architecture checks. Refresh Codegraph only if dependencies change.
- Independently review the completed implementation before final targeted verification; document the exact SHA and remaining limitations. Manual visual checks must not be claimed from blank screenshots.

## Workflow and release
- Keep plan, code, tests and documentation changes on rich-ui-custom-controls. Move the final plan to specifications/implemented on the same branch before review.
- No VERSION or CHANGELOG edits during development. Suggest MINOR for the compatible UI feature; final version is determined at release time.
- Owner approval is mandatory before merging to main.

## Owner-added scope (2026-11-10): WritingBlock

Owner requested `WritingBlock` support before PR #53 independent review. Implement this as a separate inert document-editing surface in the shared renderer, not as an extension of the radio action grammar. Level remains Standard; no script execution, arbitrary MDX components, new host endpoints or chat send authority.

- Recognize a bounded `<WritingBlock variant="..." subject="..." recipient="..." cc="..." bcc="...">…</WritingBlock>` with literal static metadata and plain Markdown source content. Variants: `email`, `chat_message`, `social_post`, `document`, `standard`. No model-authored handlers.
- Render a usable accessible editor with local draft state and a copy action, preserving Markdown source as editable text. Editing/copying does **not** send a chat message or overwrite the host composer. Explain any clipboard failure.
- Reject malformed/unsafe metadata atomically into inert source; never run expressions, inline HTML, event handlers or scripts. Maintain bounded body/metadata sizes and parser provenance so code fences remain literal.
- Add parser/SSR and Chromium editing/copy/read-only/security regression tests, document the supported subset and run the same developer checks.
- `options` variations and host-specific send actions are explicitly deferred until defined by a separate contract.

## Review corrections (2026-11-10)
- Owner requires each supported rich render/control element in its own file.
  `rich-elements/{Row,Col,Box,Grid,GridItem,Text,Title,Caption,Divider,Badge,Icon,Link,Chart,Cite}.tsx`
  now own their renderers or citation validation; `RichQuestion.tsx`,
  `RichSvg.tsx` and `RichWritingBlock.tsx` remain specialized element owners.
- `RichMarkup.tsx` retains only a fixed host-owned dispatch table invocation and
  source-provenance checks for generated question/writing slots. Each static element
  declares its own literal attribute allowlist. `MarkdownContent.tsx` retains
  one Markdown parser pipeline; no arbitrary JavaScript/MDX execution is introduced.
- `extractWritingBlocks` shields validated Markdown source before the template
  and MDX passes. It recognizes a standalone opening tag at line start with
  bounded literal metadata and a matching closing tag, preserving body text
  containing otherwise invalid MDX. Fences, inline code, comments and forged
  slots remain inert. Only a fixed map of block data reaches local editing/copy.
- Regression tests cover malformed MDX in editable source, fake/comment/fenced
  blocks, rejected metadata, and the existing interactions. Complete independent
  code review and targeted verification at the corrected commit before release.

## Developer verification limitation (2026-11-10)
The required Codegraph regeneration was attempted in a temporary read-only
GitHub Actions job. The package pinned as 3.0.0 was unavailable on npm;
the published CLI installed as 1.6.2 and did not implement the required
`codegraph graph` command. The temporary workflow was removed; the generated
graph was not hand-edited. A developer with the repository-compatible
Codegraph CLI must run `node .agents/codegraph/export.cjs`, commit the
generated graph, and repeat affected verification before independent review.

## Owner-approved simplification and CodeBlock/box scope (2026-11-11)

**Status:** On 2026-11-12 the owner explicitly authorized continuing development without the separate independent plan review. This override applies to the plan-review stage only; High-risk independent code review, independent testing, and separate merge approval remain required. The owner chose a simpler key/control registry and requested that both `CodeBlock` and `box` work. The existing DIL radio-question parser is retained for backward compatibility in the current development increment; replacing it with a declarative literal-choice contract is not yet complete.

### Scope and responsibility
- Shared Markdown renderer remains the **one** model-response renderer. It finds trusted, known presentational tags and dispatches to fixed host-owned control definitions. Each control owns its attribute validation, local UI state, child-content semantics, and React rendering. No raw HTML insertion or model-authored callback/function execution.
- Static `box`, `row`, `col`, `text`, `icon` and similar controls recursively render validated static children through the same renderer. Limit depth/size and render unsupported markup inertly. No recursive conditions or nested interactive controls in this phase.
- `WritingBlock` consumes **literal enclosed source** rather than evaluating child markup; its existing local edit/copy behavior is preserved.
- Implement `CodeBlock` as a **raw-source** control recognized by its literal opening and closing tags; support bounded literal `language` and boolean `editable` as an initial contract. Static blocks display escaped selectable code; editable blocks can edit locally without host action, script execution, or chat submission. Preserve code content literally, including JSX-looking text, braces, entities, Markdown fences, and command-shaped examples. Decide whether extra existing DIL attributes are needed from actual fixtures rather than inventing support.
- `Question` owns its radio selection and sends only through the existing host-granted `issueNewTurn` callback, with a literal response per option. Do not interpret model-authored DIL hooks, `onClick` handlers, ternaries or template expressions when the same user-visible behavior can be expressed declaratively. Document any intentional incompatibility with previous authoring syntax.
- Remove replaced parsing and fallback mechanisms rather than maintaining two parallel interpreters. Preserve existing provenanced control activation, escaping, Markdown/MDX ordinary content and no-authority-in-code behavior.

### Critical Agent-command-parser boundary (observed source)
- `src/Mezhs.Agent.Api/Commands/Parser.cs` currently examines every standalone uppercase-leading tag. Hence `<CodeBlock language="typescript" editable>` is rejected as a malformed command, while lowercase `box` is already ignored by that rule. **The problem is not only the rich renderer.**
- Only actual Agent protocol command tags (e.g. `SH`, `ATTACH`, `DONE`, `ABORT`) can be executable; preserve strict malformed known-command rejection, unknown uppercase-command rejection, command ordering, policy checks and opaque valid command bodies. PascalCase presentational tags with attributes must remain visible content, not errors.
- Most importantly, command-looking lines **inside** literal CodeBlock bodies and Markdown code fences must not be promoted to Agent commands. Establish a simple reliable protected-region rule that honors code fence/markup context before command extraction, without making the Agent interpreter execute or depend on the UI control registry. Do not merely ignore a CodeBlock opening tag and continue scanning its body for `<SH>`.
- Preserve existing `<escape>` handling, and verify that malformed/incomplete presentational regions do not accidentally enable commands or suppress legitimate top-level commands. Avoid broad ignores that weaken command strictness.

### `box` behavior to verify/repair
- `src/Mezhs.Web.Lib/src/rich-elements/Box.tsx` already registers `box`, and the telemetry fixture asserts a rendered `rich-box`. Do not add a duplicate implementation.
- Reproduce the user-facing failure with full Agent transcript integration and representative tags such as `<box border radius="lg" padding={3} gap={2}>…</box>`, nested layout, spacing, and text. Differentiate a stale deployed UI, markdown/MDX parse failure, unsupported attribute, and command-parser rejection based on evidence before changing it.
- Assert no visible raw `box` tags when valid, no unexpected code-block fallback, and preserved safe rendering of nested Markdown and rich children.

### Acceptance and verification
- [ ] Agent accepts and preserves a valid top-level `CodeBlock` response with literal language/editable attributes; no malformed command error.
- [ ] `CodeBlock` renders safely in both normal chat and Agent transcript; fenced examples remain literal; editable draft is local and does not submit a message.
- [ ] `CodeBlock` with embedded `<SH>`, `<DONE>`, `<ATTACH>` or malformed command-looking text **never executes or claims completion**, including inside Markdown fences; legitimate top-level commands continue working exactly once and with unchanged policy checks.
- [ ] Valid nested `box` and other static controls render recursively with the expected layout in normal and Agent chat; failing real examples are reproduced and fixed at the actual owner.
- [ ] Existing WritingBlock source integrity, conditional question behavior (or clearly documented replacement syntax), SVG/citations, standard Markdown, raw-code escaping, and host authorization remain safe.
- [ ] Regression tests include malformed tags, unmatched delimiters, backticks inside raw blocks, code fences, escaped tags, nested controls, large content, repeated tags, and non-command uppercase markup; no arbitrary model JavaScript runs.
- [ ] Run targeted Agent protocol tests, rich renderer/SSR and real Chromium suites, both frontend builds, impacted .NET builds, and CI on the exact resulting SHA. Regenerate Codegraph using the compatible CLI, never by hand.
- [ ] Complete independent plan review **before** architecture implementation, independent code review **before** independent final testing, and obtain explicit owner merge approval. Leave VERSION/CHANGELOG and main unchanged. Proposed release: MINOR if backward-compatible; reassess if authoring syntax breaks.

## Developer implementation progress (2026-11-12)
- Extracted CodeBlock and WritingBlock source through one bounded source scanner, with separate host-owned control components and generated-slot provenance in the existing rich renderer. Added literal CodeBlock viewer/editor and local copy; nested static boxes still use the existing renderer.
- Updated the Agent command parser so PascalCase presentation tags and raw CodeBlock/WritingBlock bodies are visible, not executable commands. Added Markdown-fence and indented-code exclusions while retaining strict uppercase command validation.
- Added CodeBlock/box SSR and Chromium regression scenarios, Agent command-protocol regression cases, and documentation updates. Developer CI must be evaluated at the final SHA; manual source inspection is not a substitute for tests.
- Remaining design debt: legacy RichQuestion Acorn expression grammar is still supported, so the complete declarative-question simplification described above is **not finished**. Reassess its compatibility and replacement in development; do not claim this increment completed the architectural redesign.
- Codegraph regeneration was outstanding at this earlier candidate. The generated file was later regenerated using the compatible native CLI; see review-corrections evidence below.


## Independent code-review corrections (2026-11-12)

- **Agent command boundary:** raw CodeBlock delimiters now follow the standalone 0–3-space closing contract. WritingBlock also ignores closing-looking lines inside Markdown fences, HTML comments and inline-code spans, remaining fail-closed when a raw region is incomplete. WritingBlock's renderer now requires a standalone closing delimiter; the supported syntax documentation states the exact rule. Command execution remains exclusively owned by the Agent protocol/Interpreter.
- **Regression coverage:** the Agent parser exercises nested rich output containing faux closers in a fenced Markdown example, HTML comment, multiline inline-code span and indented CodeBlock example, and asserts that only an actual later top-level SH and DONE are parsed. Renderer SSR tests cover escaped literal content and nested CodeBlock/WritingBlock; Chromium tests exercise the shared ChatTranscript with both editors, static sibling layout, and no chat send.
- **Codegraph:** regenerated without hand edits using the scoped @lzehrung/codegraph@3.2.1 native CLI in temporary read-only GitHub Actions run #43200405157; 279 tracked files, 731 edges, native semantic parsing of 190 files, no fallback or degraded files. The temporary workflow was removed after the graph was committed. The earlier unscoped package name was not the correct CLI.
- **Workflow:** the High-risk plan-review owner override remains limited to the independent plan review stage. Independent code review at the final commit and subsequent independent testing remain required. No VERSION, CHANGELOG, tag, release, or main merge was performed.
