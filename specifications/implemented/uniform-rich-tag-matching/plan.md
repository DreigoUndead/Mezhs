# Uniform tag matching

ID: uniform-rich-tag-matching
Branch: `uniform-rich-tag-matching`
Level: **Standard (owner override, 2026-11-17)** — fix an existing inconsistent parsing rule, not a new feature.
State: **Implemented on task branch - independent code review pending**

## Requirement

- Each supported paired tag uses the same position-independent recognition: find its matching closing delimiter anywhere later in the source. Newline, indentation, prose, Markdown, other UI elements, code examples and opaque editors do not independently exempt a paired command from recognition.
- An opening paired tag without a matching close is ordinary text, not a rejected batch or actionable command. No special fallback rules.
- Keep tag forms distinct: `SH`/`ATTACH` are opaque blocks (first matching closer); UI containers match balanced nested tags; `DONE` remains a bodyless marker; self-closing UI leaves stay self-closing.
- Strip only matched command spans from Agent visible text, retaining the exact surrounding content. Preserve the exact command body bytes.
- Owner clarification (2026-11-18): unregistered tag names are ordinary text, not a document validation error; nested registered tags and Agent command pairs are still discovered. Registered tags retain their own validation.

## Approach and coding sanity

- **Owner:** Keep one canonical C# RichDocumentParser and its existing tag registry. Do not create an Agent-only parser or an alternate frontend grammar.
- **Simplify:** Remove the redundant standalone-line, Markdown-context and UI-container command gating instead of layering yet another exception. A single bounded command-span discovery step in the canonical parser can handle globally recognized commands; UI matching still uses the same `TryTag` tokenizer and source spans. Avoid duplicated command collection and state; preserve `Registry`, policy, Executor and whole-batch validation.
- **Scope:** Change the parser, Agent visible-text projection, precise regression tests, model protocol instructions and current user-facing specifications. No new services, async logic, HTML/DOM handling, version or changelog changes.
- **What could break:** Permitted matched commands in examples, Markdown code fences, `escape`, raw editors, SVG or presentational tags gain command authority. The owner explicitly requested no context exceptions; document this consequence. User-authored input is not an Agent reply and existing policy checks still apply. Keep parsing resource budgets and avoid partial execution on a *matched but invalid* batch.
- **Reason to avoid extra complexity:** The goal is fewer special cases and shorter code, not compensating for them with a second validation framework. Evaluate net line change and necessity of each remaining branch.

## Acceptance

- Same-line `before <SH>echo OK</SH> after`, multiline `SH`, `ATTACH`, paired rich UI, and adjacent tags work with whitespace-independent delimiters.
- `<SH> cd /d D:\\Projects\\eflex-framework && git status --short --branch && git branch --show-current && type specifications\\planned\\debug-log-files\\plan.md && if exist AGENTS.md type AGENTS.md </SH>` is parsed, preserving spaces in the shell body.
- Missing closers are inert and do not prevent unrelated complete tags later from being recognized.
- Nested UI tags match correctly; command bodies are not recursively parsed.
- Agent visible text preserves characters outside command source spans. `DONE` still cannot precede other executable commands.
- No command execution from non-Agent messages, frontend rendering, or disallowed Agent policy. Existing build/tests and Chromium UI smoke run without regressions.

## Workflow / evidence

Standard development on this branch; targeted developer checks, independent code reviewer, then relevant final verification (separate tester if useful). Plan archived to `specifications/implemented/` on task branch. GitHub Actions runs Windows .NET/Node/Chromium suites. No merge before separate owner approval. Release proposal: patch; do not bump until release.

## Implementation and preliminary verification (2026-11-17)

- Canonical parser simplified from 903 to 561 lines, removing context-dependent line/Markdown permission masks and separate closing-delimiter heuristics. Existing `RichDocumentParser`/`RichTagRegistry` remain the owner; no duplicate Agent parsing mechanism was added.
- `ScanCommands` discovers matched `SH`/`ATTACH`/`DONE` in original source order; `ReadChildren` and Agent `Parser` consume exact source spans without removing neighboring prose. Opaque command bodies end at the first close; unmatched openers remain ordinary text.
- Protocol instructions, docs, current specification, C# parser/Agent policy/Executor and JS/Chromium fixtures updated. Significant semantic change: complete command tags in Markdown/escaped/raw example content are now executable under Agent policy. This follows the owner directive; frontend rendering never executes.
- GitHub Actions on SHA `198f7cb3` confirmed full .NET solution build, provider tests and SQLite/API smoke passed; rich UI tests failed on outdated context exemptions. CI on `a8b57483` confirmed build passed again; rich tests had two failures (unrelated group-delete focus/escape and a now-overstrict pre-code count), both recorded rather than called PASS. Corrections on branch continue through normal CI; exact final pass SHA must be added when available.
- Sanity: fewer parser lines and decisions; no new async, service, abstraction, duplicated tokenizer, DOM automation or state persistence. One globally recognized command-source scan plus the existing bounded UI tree/parser.
- Release suggestion: patch; `VERSION` and `CHANGELOG.md` unchanged. Independent Standard code review and final verification still required; no merge without owner approval.

## Local regression follow-up (2026-11-17)

- Earlier GitHub Actions run 44088974401 passed on ea7c366. A direct local C# parser run exposed one regression (61/62): a radio label containing an inline-code closing delimiter was recognized too early because the candidate prefix ended before its closing backtick.
- The bounded radio compatibility grammar now evaluates the complete current line, preserving source offsets. The canonical SH/ATTACH command discovery rules are unchanged.
- Developer verification after the fix: C# parser 62/62 PASS; Node/real Chromium 61/61 PASS; Release solution build PASS; git diff --check PASS. The machine lacks pwsh, so the Agent protocol PowerShell script could not run locally.
- The Windows CI workflow now runs the C# test application assertions rather than only building it. Full CI on the amended commit and independent Standard code review remain pending; no merge authorization.


## Independent review and corrective follow-up (2026-11-18)

- Independent code review of PR #64 at edfbe96 returned FAIL: P1 existing personalized instructions.local.yaml could contradict upgraded command matching and suppress the new guidance; P2 removing an inline command split the rendered Markdown AST into separate paragraphs. The reviewer also identified a previously unreachable command-body test assertion.
- P1 correction: PolicyDecoder always appends authoritative SH and ATTACH runtime guidance after older personalized instructions when those commands are permitted. The upgrade regression asserts the new guidance takes precedence in the assembled prompt while the local sidecar remains unchanged. PowerShell policy regression PASS on Windows PowerShell 5.1; the test script retains a UTF-8 BOM for correct Unicode decoding.
- P2 correction: command spans remain hidden without flushing the surrounding Markdown scope. Source-length placeholders preserve original positions; generated placeholders are removed from the resulting text and code nodes. Empty paragraphs containing only an omitted command are discarded. The corrected multiline command-body test assertion is exercised.
- Developer evidence: C# rich-UI suite 68/68 PASS, including inline paragraphs, emphasis, lists, inline/fenced code, blank-line command, source spans and command body bytes; policy-decoder PowerShell regression PASS; targeted Agent API Release build PASS; git diff --check PASS. Chromium/Node browser tests 61/61 PASS with test concurrency 1. Two default-parallel runs each produced 60/61 with a group-deletion focus restoration failure; that test passed in isolation and serially, but its concurrency sensitivity is not yet conclusively diagnosed. No group-deletion code was modified for this task.
- Previous GitHub CI was green on edfbe96. The new corrective commit requires its own CI and a new independent code review before completion. Standard scope and owner merge-approval requirement are unchanged; VERSION and CHANGELOG remain unchanged.

## Independent review follow-up — unknown tags (2026-11-18)

- Independent review of PR #64 at 09b0566 returned FAIL (P2): unregistered opening tags were rejected by `ReadChildren`, invalidating ordinary XML/HTML examples and otherwise valid Agent replies.
- Owner explicitly clarified that an unregistered tag must be ignored as rich syntax and remain ordinary text. This applies even when a registered command or UI tag occurs inside the unknown wrapper; registered SVG names out of scope and invalid registered attributes still fail validation.
- Correction on the existing task branch: skip unregistered tag openers and malformed unregistered closers without suppressing nested registered tokens. Extend C# and Agent parser regressions for unknown wrappers, nested commands, XML fences, exact visible text, invalid registered SVG, and malformed unknown closing markup. New independent code review remains required after CI; do not merge without owner approval.


## Owner-clarification follow-up validation (2026-11-18)

- The owner-approved unregistered-tag correction was already pushed independently on this branch through a16ae02; the earlier narrower local Markdown-mask patch is preserved only on backup/uniform-rich-tag-markdown-mask-08879ec and was not pushed.
- Local validation of a16ae02 found a regression: registered rich UI nested inside SVG was incorrectly treated as unknown text. Restore failure for registered non-SVG presentation tags in SVG while allowing genuinely unknown wrappers and globally discoverable SH commands.
- Existing browser security regressions also require rejecting active-content HTML/SVG tag names (script, external image/use and related tags), while ordinary unknown names remain inert text. Markdown-literal examples remain display-only. Refresh the old Agent protocol rejected-batch fixture to use an invalid registered tag instead of an unregistered name, preserving its no-shell-side-effects assertion.
- Verified locally after corrections: C# rich-UI 78/78 PASS; Agent protocol integration PASS including rejected-batch isolation and durable shell execution; Chromium 58/58 rich UI plus 3/3 additional browser tests PASS; Agent policy regression PASS; codegraph unchanged; git diff --check PASS. A fresh CI run and independent code review are required on the final pushed SHA. No merge authorization.


## SVG command display review correction (2026-11-19)

- Independent code reviewer exec_642874e3718448e994846da602ac6ab1 reviewed a6b5f06 and returned FAIL (P2). Matched commands were recognized in SVG but retained verbatim in display text nodes. This was reproduced with SH inside SVG.
- Corrected SVG text emission to omit matched command spans while retaining separate visible nodes and exact original source positions. Added SVG regression tests for SH and ATTACH and for SH nested inside an unregistered wrapper; C# rich-UI suite 80/80 PASS and Chromium rich UI/browser suite 61/61 PASS. Correction committed as 8dd4ea2.
- PR #64 was blocked by a conflict with main in instructions.yaml; merged origin/main into the feature branch only as 74172d8. The resolution retains owner-approved position-independent SH/ATTACH matching and adds the current main clarification that execution works without a listed shell tool. The merge is not a merge of PR #64 into main.
- The prior exact-head a6b5f06 had no GitHub Actions runs or check runs while the PR was conflicted. Fresh CI and a new independent code review are required for the final pushed SHA; keep the PR in draft and do not merge without separate owner approval.

## Owner architectural correction (2026-11-19)

- Owner explicitly requires `SH.AllowAsChild=false` and `ATTACH.AllowAsChild=false`, both retaining `SearchChildren=false`. This supersedes the former requirement to discover commands inside registered rich UI, SVG, escape or raw editors. Root-level inline commands and complete pairs in ordinary Markdown still match, including code fences; unknown tag wrappers remain ordinary text.
- Independent review of `a6b5f06` returned FAIL (P2): source-wide `ScanCommands` bypassed nesting eligibility and retained command text in the SVG display AST. Earlier SVG-specific stripping was superseded by the architectural correction: `ReadChildren` now owns command discovery and enforces registry child eligibility; non-searchable child bodies remain inert.
- Source correction committed as `d949a96` (canonical parser and registry); C# and Executor assertions updated in `16f2f0b`; Chromium/Node assertions updated in `ccf1698`. Full exact-head CI and independent re-review remain required; no merge/version/changelog updates before separate owner approval.
- Developer CI run #763 (44415696004) on `fca083b`: .NET solution build, provider/JavaScript, Core API and SQLite checks passed. Rich UI C# suite was 83/84; the sole failure was the former test expectation that `SH` embedded in Markdown inside 20 nested registered boxes still executes. The approved root-only policy requires zero commands there. Corrected the regression expectation; new exact-head CI required.


## Review correction — Markdown code rich tags and same-line CodeBlock (2026-11-19)

- Independent code review of `976ce338` found a P2: a root inline rich tag (`text`) inside Markdown inline/fenced code was discovered by the canonical parser but silently removed by Markdown lowering, causing `AppendMarkdown` to reject the entire message and discard unrelated root commands. Review: [5967323293](https://github.com/DreigoUndead/Mezhs/pull/64#pullrequestreview-5967323293).
- Repair on the existing task branch: `RichMarkdownNodes.Map` restores discovered inline rich elements inside Markdig code nodes as ordered code-fragment/rich-element siblings, keeping original source spans and the existing single rich-document parser. It leaves code containing only hidden commands on its previous projection path.
- Added C#/Agent/renderer regressions for inline and fenced code, a later SH after the fence, and same-line `CodeBlock` with opaque nested SH and an independent following SH. The latter guards the previously observed `CodeBlock must begin on its own line` rejection. Current branch parser has no standalone-line restriction; the live deployment that rejected a response must still be checked after updating to a build containing this fix.
- Development commits: `2862a4b`, `4f39d18`, `eb013cdd`, `a61c2ae`. Required follow-up: exact-head CI, fresh independent code-review PASS, then final verification and separate owner merge approval. No version/changelog or main changes.

- Follow-up clarification: the line-independent contract is **not CodeBlock-specific**. Extended the same regressions across `WritingBlock`, `escape`, inline `text`, structural `box`, and root commands in C#/Agent/Chromium fixtures. The existing shared parser already owns these tag roles; no extra parsing mechanism or special-case validation was added.
