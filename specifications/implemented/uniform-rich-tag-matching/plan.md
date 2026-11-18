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
