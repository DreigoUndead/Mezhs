# Preserve inline rich elements during Markdown lowering
ID: rich-inline-markdown-lowering

## Classification and owner direction
**High-risk**: the canonical C# parser also produces host-only Agent command candidates. Its failure and containment behavior is a security boundary. No code until an independent plan review has passed. Work on this separate task branch, not on `main` or PR #66.

## Observed failure and scope
- A real MEŽS agent reply was rejected with `Inline rich element did not survive Markdown lowering.` The reply included ordinary prose and an explicit Markdown fenced `jsx` snippet containing two self-closing `Cite` elements (one with `ref`, one with a literal `refs` array).
- The exception originates in `RichDocumentParser.AppendMarkdown` when at least one parsed inline rich node is absent from the lowered `RichMarkdownNodes.Convert` tree. That code location proves *where* the failure was detected, not why Markdig lost the node.
- Existing tests cover simpler inline-rich and fenced-tag cases but do not establish the exact reported combination. PR #66 separately handles a literal `Cite.refs` array and has successful CI; it does not establish a fix for this exception.
- Concurrent task branch `parser-expected-failures-no-exceptions` currently holds a plan to change parser exception handling. Inspect its final implementation before conflict resolution; do not conflate the two tasks.

## Goal and scope
Preserve valid, bounded inline rich elements across Markdown lowering without rejecting the entire reply; retain surrounding prose and code formatting. Keep parser/renderer semantics consistent with the current explicitly documented behavior, including the repository's position-independent discovery of complete UI tags inside ordinary Markdown fences.

**Out of scope:** Changing Agent SH/ATTACH/DONE authority, accepting arbitrary JS expressions, weakening validation, implementing citation provenance, or refactoring all Markdown parsing.

## Investigation and proposed approach
1. Reproduce the exact rejected reply or a sanitized fixture through `tests/Mezhs.RichUi.TestApp` (`--parse-stdin`) and the existing Node rich-UI SSR test helper. Compare individual `Cite` tags, the `jsx` fence with metadata and adjacent prose, multiple tags in one fence, and the same tags outside a fence. Record expected/actual typed nodes and diagnostics. Do not presume which element is responsible.
2. Trace source spans, `inline` placeholders, Markdig `FencedCodeBlock`/literal nodes and node reconstruction. Compare first source character positions, end-exclusive spans, normalized code `Lines`, and any hidden command spans; confirm exactly where a placeholder is lost. Seek the smallest falsifiable cause.
3. Repair the existing `RichDocumentParser` / `RichMarkdownNodes` lowering mechanism only after evidence. Preserve a single canonical C# parser; no second JS parser, blanket exception swallowing or command fallback. If a simpler approach requires relaxing accepted semantics, stop for owner decision before editing code.
4. Add positive regression fixtures for the observed reply (with both citation forms once the #66 dependency is available), inline Markdown/list cases, fenced examples and adjacent siblings. Add adversarial command nesting tests showing complete commands remain root-authorized only and remain inert inside any complete known/unknown rich container, SVG, escape or editor. Verify complete invalid batches never execute partial commands.
5. Keep #66 independent: use existing `Cite ref` forms to reproduce on `main` where possible; only stack on #66 if the failing case demonstrably depends on its unmerged literal-array support.

## Acceptance criteria
- [ ] Exact rejected reply or its deterministically equivalent sanitized reproduction is parsed without the cited error.
- [ ] Every valid registered inline rich node survives Markdown lowering exactly once, with correct source spans and visible neighboring Markdown content.
- [ ] No root Agent command is invented, promoted from protected children, or silently dropped relative to the current authorized command grammar.
- [ ] Invalid dynamic attributes remain inert Unknown diagnostics; document resource budgets and fail-closed command validation are unchanged.
- [ ] No changes outside the responsible parser/lowering code, targeted tests and any actually affected current specs.

## Review and verification
- Independent **plan review** on this branch before implementation (mandatory for High-risk).
- Developer checks: Release .NET parser test application, Node rich-model UI tests, relevant Agent-protocol and real-browser regressions; run against the exact branch SHA, report exit codes.
- Independent **code review** after implemented plan is moved to `specifications/implemented/`; independent **final testing** after review PASS.
- CI is supplemental and cannot be substituted for an independent reviewer/tester. No merge, version, or changelog changes until explicit owner approval of the completed tested SHA.

## Planning evidence
- `src/Mezhs.Common/RichUi/RichDocumentParser.cs`: `AppendMarkdown` checks that all inline nodes are represented in the converted tree, throwing the observed error when a node remains.
- `src/Mezhs.Common/RichUi/RichMarkdownNodes.cs`: replaces inline elements with private-use placeholders, then maps the Markdig AST back to typed nodes, with separate handling for `code` and `literal` nodes.
- `specifications/current/agent-runtime.md` and `.agents/workflow.md`: command security and workflow contract.
