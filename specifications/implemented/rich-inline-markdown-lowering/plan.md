# Preserve inline rich elements during Markdown lowering
ID: rich-inline-markdown-lowering

## Classification and owner direction
**Small / low risk (owner override, 2026-11-20):** Isolated Markdown display-lowering bug. No independent plan, code review, or testing stages required. Preserve existing Agent command boundaries and do not merge without separate owner approval.

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
- [x] Sanitized reproduction using a valid Cite followed by an unsupported tag in a Markdown fence parses without the cited error. The exact refs-array form belongs to separate PR #66.
- [x] Tested rich inline nodes in unclosed, split, and ordinary fenced Markdown survive once with exact source spans.
- [x] Existing root Agent command behavior and containment checks pass unchanged.
- [x] Invalid dynamic attributes remain inert diagnostics; resource budgets and command-validation code are unchanged.
- [x] Changes are limited to Markdown lowering, focused parser tests, and this plan.

## Review and verification
- Small-change self-review; owner explicitly waived independent plan review.
- Developer checks: Release .NET parser test application, Node rich-model UI tests, relevant Agent-protocol and real-browser regressions; run against the exact branch SHA, report exit codes.
- Small-change self-review of the final diff, then targeted developer tests.
- Record actual check results. No merge, version or changelog changes before owner approval.

## Planning evidence
- `src/Mezhs.Common/RichUi/RichDocumentParser.cs`: `AppendMarkdown` checks that all inline nodes are represented in the converted tree, throwing the observed error when a node remains.
- `src/Mezhs.Common/RichUi/RichMarkdownNodes.cs`: replaces inline elements with private-use placeholders, then maps the Markdig AST back to typed nodes, with separate handling for `code` and `literal` nodes.
- `specifications/current/agent-runtime.md` and `.agents/workflow.md`: command security and workflow contract.

## Implemented change and evidence
- Cause established by diagnostic Markdig spans: an unclosed or split fenced code block can report a span containing only its opener (0..3), while its code-line slice extends to the inline element placeholder (offset 8 onward). The lowering mapper bounded lookup to the short block span, so its registered inline element was absent from the reconstructed tree.
- Fix: in `RichMarkdownNodes.Map`, extend only fenced-code mapping end to the maximum end of its actual Markdig line slices. No changes to tag discovery, parser command collection, or execution policy.
- Regression coverage: two new C# test checks include incomplete and split fences, exact Cite spans, inert invalid siblings, and unchanged root command behavior.
- Developer verification (2026-11-20): `dotnet run --project tests/Mezhs.RichUi.TestApp -c Release` PASS (97/97 baseline checks plus two new checks); `node --test tests/rich-model-ui-rendering.test.cjs` PASS 44/44; `node --test tests/agent-web-rich-formatting.test.cjs tests/rich-model-ui-browser.test.cjs` PASS 20/20; `powershell -NoProfile -ExecutionPolicy Bypass -File tests/agent-protocol-result.ps1` PASS, exit 0; `git diff --check` PASS.
- The first protocol attempt was blocked by local PowerShell execution policy, and the successful rerun used a process-scoped override. `pwsh` is unavailable locally.
- Self-review: change is restricted to Markdig line-source spans; no new parser, command callbacks, or URL authority. PR #66 independently handles the literal Cite refs-array attribute; this task does not.
- Final GitHub CI and any browser/host acceptance beyond the listed checks remain to be reported at the final tested commit. Owner approval is required before merging.
