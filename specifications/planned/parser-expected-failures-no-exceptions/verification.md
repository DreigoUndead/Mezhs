# Parser expected-failure refactor — developer verification

Task: `parser-expected-failures-no-exceptions`
Branch: `parser-expected-failures-no-exceptions`
Original base: `ed98bb5c3f9c0a07fbf6589b4b6819fdba0519ee` (v0.8.3)
Integrated base: `72eac750eb2e6109d2b50a0f571920c70b1b5301` (v0.9.1)
Tested code: parser and test harness as of 2026-11-20; exact committed SHA to be reported with PR.
Authority: owner requested a **single development agent**. All implementation, code review and verification here are **self-performed and not independent**. No independent review or tester was spawned.

## Behavior and compatibility

- Expected malformed rich UI, SVG, attribute and terminal Agent command conditions now return explicit parser failures; expected exceptions no longer drive validation. A defensive handler for unexpected faults remains.
- Recoverable invalid rich UI and SVG nesting degrade to inert Unknown presentation at the existing boundary; resource limits and invalid terminal command batches fail the complete parse without releasing commands.
- An additional Unicode regression caught that the initial implementation accepted isolated `\uD83D`/`\uDE00` escapes where System.Text.Json previously rejected them. Repaired to preserve the original rejection behavior and added four regression cases.
- Frozen read-only SQLite historical corpus: **6,666** completed assistant messages; old/new **6,652 valid / 14 invalid**, **0 AST differences**, **0 command differences** across **8,325** full command candidates, including source spans. Old parser: **310** first-chance exceptions across **113** messages. Updated parser: **0** parser first-chance exceptions across the same messages.
- Deterministic mixed malformed-markup differential: **3,006 cases, 0 output differences** (before isolated-surrogate fix; fix is deliberately restricted to preserving original behavior).
- Deterministic Unicode and malformed JSON escape differential after fix: **2,214 cases, 0 full-result differences**.
- Historical user message contents were never printed or committed; SQLite was opened in read-only mode.

## Builds and automated checks

| Check | Observed outcome |
| --- | --- |
| `dotnet build Mezhs.sln -c Release --nologo` | PASS; 0 errors, 1 pre-existing unrelated CS0114 warning in WhatsApp |
| `dotnet run --project tests/Mezhs.RichUi.TestApp/Mezhs.RichUi.TestApp.csproj -c Release --no-launch-profile` after v0.9.1 merge | **121/121 PASS**, 0 parser-origin first-chance exceptions |
| `node --test tests/agent-web-rich-formatting.test.cjs tests/agent-status-unread-browser.test.cjs tests/chat-ui-browser.test.cjs tests/rich-model-ui-rendering.test.cjs tests/rich-model-ui-browser.test.cjs` | **66 pass, 0 fail** |
| `node --test tests/*.test.js` | **120 pass, 0 fail** |
| `powershell -NoProfile -ExecutionPolicy Bypass -File tests/agent-protocol-result.ps1` | PASS, exit 0 |
| `powershell -NoProfile -ExecutionPolicy Bypass -File tests/policy-decoder.ps1` | PASS, exit 0 |
| `powershell -NoProfile -ExecutionPolicy Bypass -File tests/agent-attachment-staging.ps1` | PASS, 16 checks |
| `powershell -NoProfile -ExecutionPolicy Bypass -File tests/agent-attachment-endtoend.ps1` | PASS, 9 checks with isolated temporary storage |
| `git diff --check` | PASS |

## Known limitation — unrelated UI focus test

- Including `tests/group-delete-browser.test.cjs` in the browser Node batch yields one failed test: **“Focus not restored to group-delete opener”**. A separate run of the same file also fails; this is **not a PASS**.
- The test and affected group-delete browser implementation have no diff from the original parser task base or current `origin/main`. This establishes the failing code was not changed by this branch; it **does not establish the cause** or prove whether the test failed in another environment.
- No unrelated group-deletion fix was attempted. A clean baseline/integrated environment or independent follow-up may be needed before merge if the owner requires an all-green browser suite.
- No independent agent reviewed the code or ran the tests, as required by the owner's one-agent override. No actual live desktop login/session was used to avoid touching the running application.

## Security and remaining risk

Self-check reviewed root-only command authority, document validation before command release, Unknown fallback, SVG resource limits, first-chance attribution, and the bounded JSON literal decoder. The Unicode mismatch found during self-review was corrected and retested. Historical-corpus equality provides strong regression evidence; it is not a proof over all possible hostile inputs. The top-level catch for genuinely unexpected runtime faults still fails closed.

## Type-based first-chance attribution follow-up

- Replaced literal `Mezhs.Common.RichUi.RichDocumentParser` stack-trace matching with stack-frame declaring-type comparisons using `typeof(RichDocumentParser)`; nested parser helpers are included.
- Rebuilt the C# test harness after this test-only correction: **0 errors, 0 warnings**. Regression suite **121/121 PASS**, zero expected parser first-chance exceptions. This is a single-agent self-test and is not independent.
- The parser implementation, historical compatibility baselines and command behavior were not modified by this follow-up.

- **POSITIVE_CONTROL_OLD_PARSER:** An isolated copy of the updated harness with the original parser DLL detected **27 parser-originated first-chance exceptions**. The legacy run exited 1 as expected because the new zero-exception assertions failed. The updated parser detected **0** and passed **121/121**. This verifies that type-based attribution is active rather than silently matching nothing.

## Handoff

Proposed version change: **PATCH**, final version chosen at authorized Deploy only. Leave `VERSION` and `CHANGELOG.md` untouched. Task plan remains in `specifications/planned/` until owner-approved Deploy. Owner's explicit authorization is required for any merge into `main`.
