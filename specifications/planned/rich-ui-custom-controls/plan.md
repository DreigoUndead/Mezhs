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
