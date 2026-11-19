# Rich UI literal fallback and extensible syntax layers

ID: rich-ui-literal-fallback
Branch: `rich-ui-literal-fallback`
Base dependency: `uniform-rich-tag-matching` / PR #64 (unmerged)
Level: **High-risk** — canonical command-boundary semantics, UI parser, renderer, and attribute validation change.
State: Planned; owner requested implementation on 2026-11-19. Independent plan/code review and testing gates remain mandatory before merging.

## Requirement
1. Accept bounded inert object-literal attributes, e.g. `border={{size:2,color:"danger"}}`; execute no arbitrary JS, property expressions, functions, callbacks, or DOM event handlers.
2. Unknown paired/self-closing tags produce visible safe red-bordered Unknown presentation rather than invalidating the response or exposing nested root-only commands. Preserve the raw name and safe visible contents; do not instantiate arbitrary HTML/SVG.
3. Invalid attributes/contexts on registered presentation tags should degrade to Unknown presentation, not reject the whole reply. Known opaque editor boundaries retain their protection.
4. Generic source syntax transformations are extensible; Markdown pipe tables use the existing Markdig GFM table grammar and typed renderer rather than a duplicate parser. Cover mixed UI/table responses.
5. Update user-facing instructions/current specifications and explicit C#/Node/Chromium/Agent regressions.
6. Inspect `D:\Downloads\capture.png` when available in Windows MEŽS runtime; unavailable in current GitHub/container tool environment. Do not claim inspection.

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
- Screenshot unavailable from this environment; dedicated Windows visual regression remains pending.
- An independent plan review is required by `.agents/workflow.md`. This file does not count as approval. Owner explicitly requested code and docs now; preserve review gates and keep branch draft/unmerged.

## Workflow / release
Write this plan only on the task branch. Check source/tests, update current spec, record actual verification and findings. Request fresh independent plan/code review and independent testing in MEŽS. Reconcile dependent PR #64 before release. No version/changelog changes and no merge without separate owner approval. Proposed release: minor (new fallback UI + expression syntax and explicit table documentation).
