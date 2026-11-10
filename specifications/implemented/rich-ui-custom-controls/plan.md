# Rich UI Custom Controls
ID: rich-ui-custom-controls
Level: Standard (owner override from initial High-risk classification; retain security regression coverage)
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
