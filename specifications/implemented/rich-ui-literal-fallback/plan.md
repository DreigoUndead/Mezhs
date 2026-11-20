# Rich UI literal fallback and extensible syntax layers

ID: rich-ui-literal-fallback
Branch: `uniform-rich-tag-matching` (consolidated from `rich-ui-literal-fallback` on 2026-11-19)
Base dependency: all commits fast-forwarded into `uniform-rich-tag-matching` / PR #64; redundant PR #65 automatically closed
Level: **High-risk** — canonical command-boundary semantics, UI parser, renderer, and attribute validation change.
State: Implemented on consolidated PR #64, independent code review and high-risk independent testing still required before merging. Owner explicitly requested one combined branch and a new reviewer on 2026-11-19.

## Requirement
1. Accept bounded inert object-literal attributes, e.g. `border={{size:2,color:"danger"}}`; execute no arbitrary JS, property expressions, functions, callbacks, or DOM event handlers.
2. Unknown paired/self-closing tags produce visible safe red-bordered Unknown presentation rather than invalidating the response or exposing nested root-only commands. Preserve the raw name and safe visible contents; do not instantiate arbitrary HTML/SVG.
3. Invalid attributes/contexts on registered presentation tags should degrade to Unknown presentation, not reject the whole reply. Known opaque editor boundaries retain their protection.
4. Generic source syntax transformations are extensible; Markdown pipe tables use the existing Markdig GFM table grammar and typed renderer rather than a duplicate parser. Cover mixed UI/table responses.
5. Update user-facing instructions/current specifications and explicit C#/Node/Chromium/Agent regressions.
6. Inspect `D:\Downloads\capture.png`, resolve the nested UI code-block rendering bug, and add C#/SSR/Chromium regression coverage. Screenshot was supplied and inspected; source-based tests passed on the implementation commit.

## Proposed architecture
- Retain `RichDocumentParser` as the sole structural tag scanner and `RichTagRegistry` as authority. Recognize tags structurally before attribute validation. For complete unknown pairs, use balanced matching and recursively inspect children within an Unknown parent (so `SH`/`ATTACH`/`DONE` cannot execute, as `AllowAsChild=false`). For known malformed opaque elements, preserve opaque contents rather than traversing.
- Represent fallback as a typed `RichNode("unknown")` with bounded, display-only tag name/reason and text/children. React renders only its own fixed safe element and CSS, never arbitrary names/attributes.
- Parse a bounded JSON-like literal subset into canonical `json:` attributes already consumed by `Style.structured`. Permit nested object/array literals, finite numbers, quoted strings, booleans/null, unquoted property names; reject evaluation or dynamic expressions by degrading to Unknown.
- Preserve hard parser size/depth/node budgets and policy/host command checks. Invalid document-wide protocol sequences like duplicate `DONE` remain host batch errors. No unsafe UI attributes reach DOM.
- Keep the Markdown lowering separate, operating on literal content. GFM pipe table syntax is already parsed and rendered, so add table regressions and documentation, not a second table grammar. For future syntax cases, extend the bounded canonical Markdown transformation stage without compromising tag boundaries.

## Acceptance examples
- `<box border={{size:2,color:"danger"}}>OK</box>` renders a red border; no JS is evaluated.
- `<Widget>text <SH>NEVER</SH></Widget><SH>OK</SH>` displays an Unknown card and collects only the final root command.
- `<CodeBlock bad=1><SH>NEVER</SH></CodeBlock><SH>OK</SH>` preserves the malformed editor as inert Unknown and collects only final root command.
- An unknown self-closing tag produces a warning card; unclosed opener remains plain text under the previous explicit policy.
- Pipe table `|A|B|\n|---|---|\n|1|2|` renders a typed table, including when mixed with rich UI.
- No DOM event handlers, active HTML/SVG, dynamic interpolation, or script execution become active.
- Existing C# Rich UI / Agent policy+protocol / Node+Chromium regressions and CI stay green.

## Risks / questions
- User intent "no rejection on any tag" applies to malformed presentation syntax, **not** to violated command policy, budgets, invalid protocol batches, or unsafe execution; security remains fail-closed for these.
- Known tag case sensitivity and unknown opaque contents: maintain bounded tree traversal and show literal content rather than granting shell authority. A matched unknown opener must be considered a container rather than unrelated text, intentionally superseding the earlier PR #64 unknown-wrapper rule.
- Box border syntax should reuse the existing bounded Style helpers. Do not create general JSX execution.
- **Screenshot received and inspected** on 2026-11-19 (`capture.png`, 1168x658). It shows the preceding nested `box/row/grid/grid-item/text/icon` diagram erroneously rendered as black `pre/code` blocks, literal tag text, clipped overflow. The issue is consistent with Markdig interpreting four-space source indentation as code while parsing each nested rich scope. Regression fixture `tests/fixtures/rich-model-ui/indented-layout-capture.md` reproduces the source shape. Correct by using a scoped Markdown pipeline without implicit indented-code block parsing inside a rich container, while retaining regular root Markdown indented code and explicit fenced code. Add C# AST + SSR + real Chromium tests to require actual layout, no accidental `pre`, no literal tags. Exact-head Windows CI PASS on `3fec7ab22ed2e8e89d56bd7657330642cec27be2`, run [#44441217139](https://github.com/DreigoUndead/Mezhs/actions/runs/44441217139). This verifies the tested code and source-based Chromium regression, not a deployed-version pixel comparison.
- An independent plan review is required by `.agents/workflow.md`. This file does not count as approval. Owner explicitly requested code and docs now; preserve review gates and keep branch draft/unmerged.

## Workflow / release
Plan archived on the consolidated `uniform-rich-tag-matching` task branch before independent code review. Check source/tests, update current specification, record actual verification and findings. Request a fresh independent code reviewer and independent tester in MEŽS. Review the **whole combined PR #64** against `main`, not just the former PR #65 delta. No version/changelog changes and no merge without separate owner approval. Proposed release: minor (new fallback UI + expression syntax and explicit table documentation).

## Consolidation evidence (2026-11-19)

- Owner explicitly requested both feature branches be combined with a fresh independent code reviewer.
- `rich-ui-literal-fallback` was 38 commits ahead of `uniform-rich-tag-matching`, 0 behind; GitHub fast-forwarded PR #64 head from `595a985841e49e9d14147cf13fa4074eb7843203` to `3fec7ab22ed2e8e89d56bd7657330642cec27be2` without force. PR #65 closed automatically.
- Independent code review must assess the full combined change relative to `main`, its command authority boundary and rich UI rendering. Earlier reviewer failures are history, not a current-head PASS.
- Latest pre-archive Windows CI: [#44441217139](https://github.com/DreigoUndead/Mezhs/actions/runs/44441217139), PASS at implementation SHA `3fec7ab22ed2e8e89d56bd7657330642cec27be2`. Follow-on archive-only commit requires final exact-head comparison/checks.
