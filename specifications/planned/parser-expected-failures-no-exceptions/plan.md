# Remove exception-driven expected parser validation
ID: parser-expected-failures-no-exceptions

## Classification and owner direction
**High-risk:** The canonical rich-document parser is also the Agent command security boundary. This is a parser-internal quality/performance change, not a syntax or policy redesign.

The owner requested **one new development agent for the whole process after this planning step** (2026-11-19). That is a process override of the normal High-risk multi-agent handoffs. The single agent must clearly label self-review/self-testing as **not independent**, avoid presenting them as mandatory independent PASS evidence, and request explicit owner permission before any implementation merge into `main`. Do not start other agents without asking the owner.

## Evidence and problem
The v0.8.3 parser (`src/Mezhs.Common/RichUi/RichDocumentParser.cs`) uses thrown `RichDocumentException`, `FormatException`, `JsonException` and internal catch/rethrow for common invalid model-authored attributes and SVG content. These are **first-chance exceptions even when safely caught**, producing Visual Studio debugger noise. `ChatService.ToApiHistoryMessage` and `MessageService.ToApi` parse completed assistant content on response conversion; repeated history retrieval repeats the same validations.

Read-only diagnostic probe of the running app's matching `Mezhs.Common.dll`:
- 419 recent completed assistant messages: 49 first-chance exceptions across 19 messages; all 419 documents valid.
- 6,618 stored completed assistant messages: **303** first-chance exceptions across 111 messages, **6,605 valid / 13 invalid** (12 rejected `DONE` order, 1 failed Markdown lowering).
- Typical pairs: invalid object expression raises `FormatException` then `RichDocumentException`; invalid SVG attribute can raise twice while its parent safely degrades.
- Repeating one invalid border object 250 times gave 500 first-chance exceptions in 28 ms. This illustrates exception volume, **not** a proven user-facing performance bottleneck.

Do not equate the exception counter with unhandled errors, and do not claim fixing exception noise fixes any unrelated runtime issue.

## Goal and scope
Replace **normal, anticipated user/model-input validation failures** with explicit, bounded parse status/error results. Preserve the exact existing public rich document and Agent command semantics, security guarantees, inert presentation fallback, and diagnostic information.

Target the actual throwing paths in the canonical parser:
- `ParseAttributes`: unsupported/duplicate/unsafe attribute names, invalid forms, malformed quotes/braces, invalid value, bounded size/count.
- `SafeLiteralReader`: invalid tokens/JSON strings, duplicate keys, invalid separators and object-only requirement, depth/item limits. Avoid catching ordinary invalid-syntax exceptions merely to rethrow a new exception.
- SVG validation and nested SVG rejection: keep all-or-nothing safe Unknown fallback and finite geometry/budgets, without exception-based control flow for normal malformed content.
- Terminal command validation (`DONE` count/order) and expected Markdown-lowering mismatch: fail the whole document with zero released commands, without expected first-chance exceptions.
- Resource-limit outcomes should stay fail-closed; prefer explicit status propagation for ordinary oversized input when doing so is simple and consistent.

An unexpected runtime fault may still use the top-level defensive `catch (Exception)` to fail closed; never mask defects by swallowing them or return partially validated commands.

## Approach
1. Work on **this task branch only**, using a separate worktree if the local `main` app is running. Inspect current parser/registry, `RichQuestionGrammar`, `RichMarkdownNodes`, command adapter, and existing tests first. Establish current output on a frozen fixture corpus before editing.
2. Within `RichDocumentParser`, introduce the smallest cohesive result-based validation mechanism (e.g. `TryParseAttributes(..., out attributes, out reason)` and `SafeLiteralReader.TryRead(...)`), then propagate parse errors at the same boundaries where existing catch blocks create Unknown controls or invalid whole documents. Prefer simple local return values to new architecture or duplicated grammars. No blanket suppression, general caching, or unrelated API work.
3. For nested SVG, preserve parent-level fallback; for invalid known controls, preserve registered safe children where the current parser does. Preserve exact source spans, node order, normalized Markdown, and output diagnostics. Model-provided attributes never flow unvalidated into host DOM.
4. Retain root-only `SH`/`ATTACH`/`DONE` authority, all resource budgets, opaque raw editor handling, unknown wrapper containment, and batch atomicity. A failed parse cannot leak executable candidates.
5. Add targeted regression tests that subscribe to `AppDomain.CurrentDomain.FirstChanceException` and assert **zero parser-originated first-chance exceptions** for expected invalid syntax cases, including invalid object literals, unsupported events, SVG children, duplicate/misplaced `DONE`, Markdown failure and limits where feasible. Tests must also assert unchanged typed AST/visible diagnostics and command output. Scope exception attribution carefully so unrelated runtime/framework exceptions do not cause false failures.
6. Replay the existing historical message corpus **read-only**, without printing/storing private content in reports. Compare pre/post validity, structural output and command spans, not just exception counts. Record counts, changed cases (if any), and accepted limitations. Do not alter live SQLite files.
7. Run full C# Rich UI tests, relevant Node/real-Chromium rendering and Agent protocol/policy/attachment tests, Release build and relevant CI. Preserve working login sessions, app processes, credentials and other worktrees.

## Acceptance criteria
- [x] No predictable first-chance `RichDocumentException`/`FormatException`/`JsonException` from invalid model syntax in focused parser tests (and ideally none across the historical corpus). Unexpected runtime faults remain visible and fail closed.
- [x] Existing valid source produces the same `RichDocument` and command candidates (including source spans); invalid source keeps equivalent safe Unknown/literal diagnostics or fail-closed document result.
- [x] Invalid command-bearing batches produce no executable candidates or side effects, including nested known/Unknown/SVG/raw wrappers, duplicate/early `DONE`, malformed tags, and resource-limit cases.
- [x] Unsupported attributes/JS expressions/SVG remain non-executable and do not become DOM props, embedded scripts or host actions.
- [x] Error reporting is useful without leaking sensitive model text.
- [x] A before/after read-only historical replay reports the counts, differences, and exception volume; no unexplained semantic changes.
- [ ] Targeted regression tests and required Windows/Chromium/Agent suites pass at a recorded SHA. No changes to `VERSION`/`CHANGELOG.md` before owner-authorized release.

## Verification and rollout
Capture exact commands, exit codes, branch/base/test SHA, C# test counts, browser/API policy outcomes, first-chance counts, and historical corpus comparison in `verification.md` or the PR. Include a small reproducible negative-input fixture and compare security/functional results, not only speed.

The existing suite and historic replay are safety checks; this task must not change expected behavior just to make exception counters zero. Unexpectedly broad refactoring or behavior drift triggers the repository's mandatory complexity-escalation rule: stop and ask the owner, listing simpler alternatives and cost.

After development, keep this plan in `specifications/planned/parser-expected-failures-no-exceptions/plan.md` until the owner-approved Deploy stage, when it is archived under `specifications/implemented/`. Open a draft PR and provide the owner with a complete High-risk report that **honestly notes the one-agent independence override**. Proposed release type: **PATCH** (number chosen only at release from current main). Do not merge or tag before the owner's separate explicit authorization.

## Out of scope
- Syntax extensions, UI redesign, parser authority changes or different fallback semantics.
- Changing how often ChatService/MessageService parses or introducing caches without evidence and owner approval.
- Mutating production chat/Agent SQLite, live provider credentials or a running desktop session.
- Version bump, changelog, merge and release without renewed explicit owner approval.

## Changes

### 2026-11-20 — Implementation and compatibility review

**Change:** Expected parser validation now reports bounded failure states rather than raising routine first-chance exceptions. New regression cases check parser-originated exception noise, fail-closed command behavior, resource budgets, and inert UI/SVG fallback. A test-only full-result replay mode compares complete document and command records.

**Reason:** Frozen read-only history contained 310 first-chance exceptions in 113 of 6,666 messages. The refactor produced zero with unchanged documents and 8,325 command candidates. A later Unicode differential test exposed a compatibility discrepancy for unpaired surrogate escapes; the parser was corrected to reject them like the original. All 2,214 Unicode differential cases now match.

**Authority:** The task's approved goal and the owner's pre-existing one-agent process override. This agent performed the implementation, self-review, and tests; neither review nor testing is independent.

**Trade-off:** The group-deletion Chromium focus-restoration test fails in isolation even though its JavaScript dependencies and test are unchanged on this branch. The affected code is outside the parser; the failure remains recorded, not silently treated as a PASS. No unrelated UI fix is included.

### 2026-11-20 — Reconcile v0.9.1 workflow

**Change:** Merged `origin/main` at v0.9.1 into the task branch without modifying local `main`. Keep this plan in `specifications/planned/` until owner-approved Deploy, per the current workflow; final self-test evidence is in `verification.md`.

**Reason:** Current workflow explicitly reserves plan archival for Deploy and makes the task branch the unit of review. Incoming main changes did not touch the parser or its test harness.

**Authority:** Repository workflow update in `origin/main` (v0.9.1).

### 2026-11-20 - Avoid hardcoded test exception discovery

**Change:** The first-chance regression test now checks stack-frame declaring types against `typeof(RichDocumentParser)` and its nested helpers, instead of searching for a fully-qualified parser name in formatted stack-trace text.

**Reason:** The owner identified hardcoded discovery as a maintenance burden; a parser class or namespace rename could silently disable exception attribution. The new test remains resilient to renaming and retains the same parser-origin filtering.

**Authority:** Owner follow-up on PR #72; developer correction and non-independent self-test.
