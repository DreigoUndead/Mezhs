# SH per-command parameters

ID: sh-command-parameters
Branch: feature/sh-command-parameters
Level: Standard

## What

Support five optional root-SH attributes: `timeout`, `limit`, `limitStart`, `limitEnd`, and `regex`. Plain `<SH>` remains compatible. A canonical rich-tag parser discovers root-only SH candidates and captures **all attributes as a generic name/value map**, preserving original command text and source spans; SH inside UI/unknown/escape/raw scopes remains inert. The rich UI layer owns syntactic and structural safety, not SH parameter semantics.

**Executor is the sole functional source of truth** for accepted SH attribute names, numeric and regex validation, default resolution, command timeout, output selection, previews, and recovery information. Agent does not implement or duplicate any SH-parameter-specific validation or output formatting. It passes raw attributes, policy defaults and the command body to an Executor-owned entry point. The Executor validates and computes effective options before launching that particular command. A bad parameter uses the normal Executor error-result path, without spawning a shell; it does not become an Agent-level command-batch syntax error. Earlier successful commands in the same reply are not rolled back. Malformed tag structure or forbidden nested commands still fail closed in the canonical parser.

Effective settings remain `SH attribute → policy default → Executor built-in`:

| Attribute | Built-in default | Effect |
| --- | --- | --- |
| `timeout` | 60 | Positive execution timeout in seconds |
| `limit` | 0 | Maximum selected output lines before abbreviation; 0 disables line preview |
| `limitStart` | 10 | Beginning lines of abbreviated output |
| `limitEnd` | 10 | Ending lines of abbreviated output |
| `regex` | null | Filter output lines before applying line limits |

Preserve compatibility with the existing `policy.limits.commandTimeoutSeconds` default (no separate maximum), and with optional policy defaults for output attributes. Policy supplies defaults but does not reimplement Executor parameter rules. An explicit empty regex clears an inherited regex. A regex evaluation timeout is bounded by Executor.

Executor retains **complete durable stdout/stderr/result/status/error evidence**, independently of returned previews. If selected lines exceed a nonzero `limit`, the Executor response identifies omitted lines and supplies first/last lines without overlap. Explicit incompatible preview sizes are errors; implicit defaults fit within a smaller limit; zero previews are valid. Executor supplies an executable recovery SH command for its stored `Get <id>` record, using the installed assembly and storage path; retrieving never reruns the original command. The separate Agent-wide 16,000-character model-prompt safeguard may still abbreviate any follow-up, and must identify that fact without pretending to bypass it.

## Why

Remove duplicated SH-specific semantics across Rich UI, Agent parser, Interpreter, and prompt builder. Keep one functional owner—Executor—for command execution **and** returned execution data, while Agent retains policy authorization, message orchestration and general prompt-budget protection.

## Acceptance criteria

- [ ] Canonical parsing extracts generic SH attribute name/value pairs, body and spans without consulting a functional list of SH parameter names. Root-only/inert UI boundaries and safe malformed-markup handling remain unchanged.
- [ ] Agent command types and adapter forward the same raw SH attributes and policy-provided defaults; no SH attribute name/type/regex/preview validation is duplicated outside Executor.
- [ ] Executor alone recognizes and validates the five keys, applies SH → policy → built-in defaults, and handles explicit timeout overrides, small limits, regex filtering, and bounded regex evaluation.
- [ ] Unsupported, malformed or incompatible **parameter values** return the Executor's normal error result before launching that command, not Agent parser/batch-correction errors. Structural syntax and policy violations remain Agent/renderer concerns. Preserve command ordering and never accidentally replay an earlier command.
- [ ] Executor owns presentation of completed results (including regex-before-limit filtering, omitted counts, nonoverlapping previews and durable `Get <id>` recovery), while maintaining complete durable evidence and exit/status information. No duplicate output formatter in Agent.
- [ ] The old Executor CLI remains compatible, including raw `Get <id>`; avoid introducing a second parser, separate storage/copy or database migration without demonstrated need.
- [ ] Agent retains only the separate, explicitly disclosed model-prompt character safeguard and forwards Executor-produced result/error/recovery fields without recomputing SH-specific previews.
- [ ] Adjust instructions and regression tests to reflect the single owner. Preserve the owner-approved Development policy and unrelated Low/Mid/High instructions. New architecture change invalidates prior review/test PASS as final verification of this revision.
- [ ] Verify all changed paths on Windows in an isolated worktree, then obtain fresh independent Code Review and Testing before Deploy. Do not merge to `main` without explicit owner approval.

## How

Continue using the existing canonical rich-tag traversal and host-side command candidates; do **not** execute commands from the UI renderer. Make the SH candidate's attribute bag generic. Keep structural/authorization validation in Rich UI and Agent, but relocate the SH option contract and result formatting to `Mezhs.Executor`. Its API must accept raw command attributes plus policy defaults, resolve/validate them once, run through existing durable execution ownership, and return a display-oriented result alongside or derived from the durable record. Keep existing Executor service/CLI clients compatible. `Shell` acts as the narrow adapter for execution environment, identity, policy defaults and forwarding of Executor outcomes. `AgentPromptBuilder` serializes those outcomes and imposes only its independent global prompt-size safety bound.

Do not move shell authority into the browser, invent a new UI-to-Executor mechanism, or store a second copy of command output. Do not convert invalid parameter values into an Agent parser rejection; structural malformed markup and policies still guard execution. Use the Executor's own error path to surface validation failures. Keep generated Codegraph updated using the repository generator.

## Steps

1. **Plan (owner-directed rework)** — Record the new Executor ownership and superseded Agent validation assumptions.
   - Done when: the plan clearly defines validation, defaults, output ownership, error behavior and boundaries.
2. **Development** — Refactor the existing implementation, tests, documentation and branch configuration according to the revised plan; build and verify on Windows.
   - Done when: Executor is the only functional SH parameter implementation and developer checks are recorded on a pushed feature revision.
3. **Code Review (independent)** — Independently review the revised complete feature branch and ownership boundaries.
   - Done when: PASS at exact revision, or confirmed findings corrected with a fresh independent review.
4. **Testing (independent)** — Test Executor parameter validation/presentation and isolated live Agent round trips, including malformed syntax, Executor errors and recovery.
   - Done when: exact-SHA independent results recorded.
5. **Deploy** — Merge latest `main` into feature branch and verify integration **before** requesting explicit owner approval for release; do not modify `main` before approval.
   - Done when: owner authorizes release and obligations are completed.

## Verification

Run Windows Agent API Release build, Rich UI TestApp, `tests/policy-decoder.ps1`, and `tests/agent-protocol-result.ps1` in an isolated worktree. Add focused Executor tests for unknown keys, invalid numeric/regex values, timeout overrides, default precedence, `limit=0`, exact/exceeded thresholds, zero/small preview limits, filtering zero/some/many lines, long lines, bounded regex timeout, and preservation of durable output/exit/error evidence. Confirm an invalid parameter fails via Executor without shell side effects while earlier completed commands remain properly evidenced. Exercise actual Executor CLI `Get <id>` and generated recovery. Check preexisting structural root-only boundaries and Developer policy. Run Codegraph generation and diff checks. Record tests actually run, not assumed.

## Changes

### 2026-11-20 — Initial interpretation

**Change:** Treat `timeout` as seconds and `limit` as follow-up prompt output characters, with policy and global caps.
**Reason:** This keeps the example useful without granting additional execution authority or deleting durable output.
**Authority:** Implementation assumptions for the owner's feature request; subject to owner correction.

### 2026-11-20 — Development implementation

**Change:** The canonical rich parser carries SH timeout/limit attribute values to Agent commands; Interpreter checks the policy timeout cap and Shell passes an accepted timeout to Executor; AgentPromptBuilder truncates only follow-up output. Updated default instruction and shell-context messages and added parser, render-grammar and prompt-result regressions, plus a live rejection case for over-policy timeouts.
**Reason:** Keep a single syntax/authority path and retain full durable Executor results.
**Verification:** Source and patch checks completed via GitHub connector. Tests were added but not executed: this session has no Windows/.NET repository test host. Independent Code Review and live Executor verification are pending.

### 2026-11-20 - Local developer verification and integration coverage

**Change:** Corrected a local variable shadowing compile error in RichDocumentParser and added live Agent protocol checks for accepted per-command timeout and prompt output limit.
**Reason:** The first Release compilation found CS0136. Added positive-path integration coverage for previously unverified shell execution, Executor timeout and prompt-only truncation.
**Verification:** .NET 10 Agent API Release build passed with 0 warnings and 0 errors; Rich UI parser 101/101 passed; tests/policy-decoder.ps1 passed; tests/agent-protocol-result.ps1 passed including rejected over-policy batches with no side effects, timeout=2 limit=5 preserving full durable output while truncating model prompt, and timeout=1 yielding a TimedOut Executor record. Ran on Windows PowerShell 5.1 in an isolated worktree.
**Remaining:** Independent Code Review, subsequent Testing role and explicit owner merge approval are pending.

### 2026-11-20 — Independent Code Review (step 3)

**Outcome:** PASS for implementation revision `0c8ec2c8f7ef49f2324dc31bc93e536a6ca83fcc`; no implementation changes required.
**Review:** Checked the complete PR diff, parser and rich-tag traversal, policy validation, Shell/Executor result handling, AgentWorker recovery path, and regression coverage. SH attributes are parsed by the shared parser; invalid options fail the batch before execution; timeouts are bounded by the effective policy before shell dispatch; prompt-only output truncation does not alter Executor history. No confirmed blocking defect or unjustified architecture was found.
**Verification:** Independent static source/diff review. The prior developer's Windows/.NET test results are recorded above; this reviewer did not independently rerun those tests.
**Remaining:** Independent Testing (step 4), followed by owner-approved Deploy (step 5). Do not merge without owner approval.

### 2026-11-20 — Owner-approved development policy addition

**Change:** Add a separate `development` policy combining the existing `mezhs_default` and new `mezhs_development` startup guidance. Permit ordered reference-only lines in policy instruction expansion without changing the existing Low/Mid/High policies.
**Reason:** Workflow debugging found that the original development agent received a workflow pointer but began repository changes before reading the workflow. The dedicated policy requires workflow retrieval as the first development prerequisite.
**Authority:** Owner approved the new policy, reviewed the full development-instructions text, and requested applying it on this feature branch.
**Verification:** Additional policy decoder/bootstrap tests added; new policy revision requires build/testing and independent assessment. The prior Code Review PASS covers the earlier SH parameters implementation, not this subsequent policy change.

### 2026-11-21 — Owner-approved revised SH requirements and renewed Planning

**Change:** Replace character-based `limit` with line-based output selection, optional regex-before-limit filtering, first/last abbreviated previews, explicit full-record recovery via a generated Executor CLI `Get` command, and optional policy defaults (timeout 60 seconds and limit 0 when absent). Preserve one policy timeout setting rather than a separate maximum/default pair; update the implementation, Code Review and independent Testing steps accordingly.
**Reason:** The owner clarified the ambiguous `limit` requirement and preferred the existing Executor CLI over a new retrieval API.
**Authority:** Owner's follow-up decisions on `limit`, `limitStart`, `limitEnd`, `regex`, default values, and subsequent instruction to proceed with the agreed design.
**Trade-off:** The existing separate Agent prompt-character safeguard can still abbreviate a large retrieved record; command guidance must not promise it bypasses that safeguard.
**Supersedes:** The initial character-count interpretation, timeout-above-policy-max rule, related implementation acceptance criteria, and the earlier Code Review PASS as evidence for the **new** behavior. Keep the historical PASS recorded below as a review of its exact previous SHA.


### 2026-11-21 — Revised SH implementation and developer verification (step 2)

**Change:** Implemented five root-SH attributes in the canonical rich parser and Agent adapter. The single execution timeout resolves SH attribute → existing policy default → 60 seconds; optional line-count preview and regex settings resolve SH attribute → policy → built-in defaults. Regex filtering precedes selected-line previews, explicit incompatible preview counts reject the whole command batch before shell dispatch, and full Executor records are unaffected. Abbreviations include a generated executable `<SH limit=0>... Get <id></SH>` command using the actual Executor assembly and storage path. The existing separate 16,000-character model-prompt safeguard remains described explicitly. Updated branch-only instruction examples; preserved Low/Mid/High policy instruction bodies and the approved Development policy.

**Implementation revision:** `5241dd353d6ed551ce4e70533967732335278bff` (remote and clean local feature worktree). Earlier intermediate commits included a fixed C# compile error and a repaired PowerShell test-script regression; report success only against this final revision.

**Verified on isolated Windows worktree** `D:\\Projects\\Mezhs-sh-parameters-verify`:
- `dotnet build src/Mezhs.Agent.Api/Mezhs.Agent.Api.csproj -c Release --nologo` — PASS, 0 warnings, 0 errors.
- `dotnet run --project tests/Mezhs.RichUi.TestApp -c Release` — PASS, 103/103.
- `powershell -NoProfile -ExecutionPolicy Bypass -File tests/policy-decoder.ps1` — PASS (policy, development policy bootstrap, durable shell identity, Unicode, timeout, output formatting/default assertions).
- `powershell -NoProfile -ExecutionPolicy Bypass -File tests/agent-protocol-result.ps1` — PASS on final revision: invalid attributes/regex and incompatible previews rejected without side effects; inline shell execution; regex + line preview with full durable output; generated Executor CLI `Get <id>` executed and retrieved original content; explicit timeout greater than policy default succeeded; shorter timeout enforced by Executor; two-shell ordering, partial failure, and Agent completion/continuation checked.
- `codegraph review` from a clean HEAD worktree — no_changes; this is a working-tree status observation, **not** an independent branch code-review PASS.

**Remaining:** Independent Code Review (step 3) and independent Testing (step 4), then owner-approved Deploy only. No merge into `main`.

### 2026-11-21 — Independent Code Review step 3: recovery correction and verification

**Outcome:** FAIL / correction committed at `e5a03322538dc0a6b929f34ba852e92011b738b4`; the author of this correction must hand off to a **fresh independent Code Reviewer**. This is not a review PASS.

**Finding:** The generated Executor `Get <id>` recovery SH block used `limit=0` but inherited a nonempty policy-level `regex`. Consequently, retrieval under a policy regex could silently filter the returned durable record despite disabling the line limit.

**Correction:** Generate `<SH limit=0 regex="">` so both line abbreviation and policy regex filtering are overridden. Update the recovery explanation and regression expectations; add a `ShellOutputOptions.Resolve` test verifying that an explicitly empty regex supersedes a policy regex default. The underlying Executor history and command execution path remain unchanged.

**Verification (isolated Windows worktree at `e5a0332`, runtime command results 9146/9147/9149):** Agent API Release build PASS (0 warnings/0 errors); Rich UI TestApp PASS (103/103); `tests/policy-decoder.ps1` PASS; `tests/agent-protocol-result.ps1` PASS, including live Executor `Get` recovery, timeout override and enforcement, invalid-batch no-side-effects, ordered shells, partial failure and continuation. These checks confirm the current scenarios, not a fresh independent review.

**Next:** New independent Code Review for the corrected branch and this plan entry; then independent Testing, followed by Deploy only after owner approval. No merge to `main`.

### 2026-11-21 — Independent Code Review step 3: malformed paired SH correction

**Outcome:** FAIL / corrected. A fresh independent Code Reviewer must review the corrected implementation; this entry is not a PASS.

**Finding:** When an SH opener contained an unterminated quoted attribute but still had a matching closing SH delimiter, the canonical tokenizer treated the opener as unrecognized text. A preceding valid SH could remain executable, violating the plan's whole-batch malformed-attribute rejection requirement. A minimal parser probe confirmed this at the reviewed revision `ee1608d8ed9db9dc342fc23fcfc963a24be9c180`. Invalid recognized numeric attributes and regex already rejected correctly.

**Correction:** Commit `5f15cfae41ffe98eb5bdf537f38c88cf356b5a23` rejects malformed paired root SH opening syntax as an invalid rich document before command dispatch. Genuinely unmatched openers stay literal; nested SH markup within UI scopes remains inert. Added parser, Rich UI, and isolated Agent no-side-effect regression cases. The existing Executor/regex/recovery implementation was not changed.

**Independent reviewer's verification on isolated Windows worktree `D:\\Projects\\Mezhs-sh-parameters-verify`:** Agent API Release build PASS (0 warnings, 0 errors); Rich UI TestApp PASS (105/105); `tests/policy-decoder.ps1` PASS; `tests/agent-protocol-result.ps1` PASS, including the new malformed-paired-SH no-execution test, regex preview/recovery via Executor `Get`, timeout overrides/enforcement, ordered shells, and error continuation. `codegraph review` compared merge-base `72eac750` against `ee1608d8` and enumerated changed files and API-compatibility leads; its risk score is not an independent PASS.

**Remaining:** Fresh independent Code Review (step 3) at the latest branch revision, then independent Testing (step 4). Deploy/merge into `main` requires owner approval.


### 2026-11-21 — Independent Code Review step 3: PASS at 74a496a78d18c3f7661d04844cd08c400ea8c4a5

**Outcome:** PASS. An independent reviewer examined the complete current feature branch against the accepted plan, its merge base, the canonical rich-tag parser and Agent adapter, policy defaults and Development policy expansion, shell/Executor execution and durable result handling, result previews/regex, and generated `Get <id>` recovery. No confirmed blocking finding or implementation correction was identified.

**Verification (isolated Windows worktree `D:\\Projects\\Mezhs-sh-parameters-verify` at 74a496a78d18c3f7661d04844cd08c400ea8c4a5):** `dotnet build src/Mezhs.Agent.Api/Mezhs.Agent.Api.csproj -c Release --nologo` PASS with 0 warnings/errors (runtime command 9250); `dotnet run --project tests/Mezhs.RichUi.TestApp -c Release` PASS 105/105 (9251); `powershell -NoProfile -ExecutionPolicy Bypass -File tests/policy-decoder.ps1` PASS (9252); `powershell -NoProfile -ExecutionPolicy Bypass -File tests/agent-protocol-result.ps1` PASS (9253). Live integration output confirms malformed/invalid batches have no shell side effects, single execution for inline SH, regex/line preview preserving durable output, generated Executor `Get <id>` retrieval, timeout override/enforcement, ordered shell execution, and error continuation.

**Limitations:** This Code Review does not replace the independent Testing role. The existing 16000-character model-prompt safeguard can still abbreviate retrieved records, as accepted in the plan. No changes were made to the implementation.

**Next:** Independent Testing (step 4) at the exact reviewed implementation SHA, followed by owner-approved Deploy only. No merge into `main` without explicit owner approval.


### 2026-11-21 - Independent Testing (step 4): PASS

**Outcome:** PASS. Independent Windows verification of implementation `74a496a78d18c3f7661d04844cd08c400ea8c4a5`. Test execution used branch HEAD `20faed716789f1b396496402640a8c90539ca404`, whose only change since the reviewed implementation was this plan file. No implementation changes were needed.

**Environment:** Windows cmd.exe, PowerShell 5.1, .NET SDK 10.0.401, dedicated `D:\Projects\Mezhs-sh-parameters-verify` worktree. The live Agent integration harness allocated a loopback port dynamically, used temporary isolated databases and storage, and did not use the owner active instance.

**Independent execution evidence:**
- 9262: `dotnet build src/Mezhs.Agent.Api/Mezhs.Agent.Api.csproj -c Release --nologo` - PASS, zero warnings and zero errors.
- 9263: `dotnet run --project tests/Mezhs.RichUi.TestApp -c Release` - PASS, 105/105; including all five root SH attributes, malformed paired tags, duplicate/unknown attributes, source spans and inert nested UI scopes.
- 9264 and 9271/9275: `powershell -NoProfile -ExecutionPolicy Bypass -File tests/policy-decoder.ps1` - PASS; policy/defaults/precedence, preview threshold and limit=0, regex-before-limit, small limits, prompt-character safeguard, durable command identity, timeout and Development policy.
- 9265: `powershell -NoProfile -ExecutionPolicy Bypass -File tests/agent-protocol-result.ps1` - PASS; live rejected invalid/malformed/regex/preview batches had no shell side effects; inline SH executed once; regex and limited previews preserved durable Executor output; generated CLI Get retrieved the original record; explicit timeout above policy default succeeded, shorter timeout was enforced; ordered multi-SH and partial failure evidence passed.
- 9267: Actual installed Executor CLI reported `Get <id>` with required Int32 ID using `Help Get`; the live generated recovery command was executed successfully by the integration test (9265). `Get --help` is unsupported (9266); the supported Help Get invocation succeeded.
- 9276: Supplemental temporary harness PASS for regex zero matches, one match exactly at limit, and limitStart=0/limitEnd=0 with omitted-line count and CLI recovery. The original test file bytes were restored.
- 9270/9272/9274: Initial supplementary harness attempts failed before added assertions because writing the original PowerShell script without its UTF-8 BOM caused PowerShell 5.1 to misread Latvian policy text. The original baseline passed after restoring its tracked bytes (9275), and the corrected BOM-preserving supplemental run passed (9276). These harness failures are not evidence of a product defect.

**Limits:** No independent forced pathological-regex timeout or production-instance test was performed; code uses a bounded regex match timeout, and isolated test coverage includes invalid regex and nonmatching/matching filters. The separate 16000-character model prompt safeguard is covered by regression assertions rather than an unbounded delivery guarantee. No merge or deployment was attempted.

**Next:** Deploy role may present the verified branch for explicit owner approval; do not merge to `main` without that approval.

### 2026-11-22 — Code Review step 3: documentation and regression correction

**Outcome:** FAIL / corrected and verified at `e351d4561c74f3b65c6e27d897c665859a5410ec`. Under the Code Review workflow, the reviewer who made corrections must hand off to a fresh independent reviewer rather than declare PASS. This entry is not an independent review PASS.

**Findings:** `docs/agent-tools.md` still described the SH timeout as policy-only and omitted the per-command output settings, conflicting with the accepted plan and current code. Regex zero-match, one-match-at-limit, and zero-length preview cases had been exercised by prior independent testing but were not retained as committed regression assertions.

**Corrections:** `422877f65db5ac48b6271016ebf60a9755aa5f66` updates agent-tools guidance for per-command timeout, regex/line-preview settings, durable result and prompt safeguard. `e351d4561c74f3b65c6e27d897c665859a5410ec` retains the three regression cases in `tests/policy-decoder.ps1` (preserving the UTF-8 BOM). Production C# implementation is unchanged from `74a496a78d18c3f7661d04844cd08c400ea8c4a5`.

**Verification:** Clean isolated Windows worktree `D:\\Projects\\Mezhs-sh-parameters-verify` was fast-forwarded to `e351d456`. MEŽS execution 9364: Agent API Release build PASS (0 warnings/errors). 9365: Rich UI TestApp PASS 105/105. 9366: policy decoder and shell tests PASS, including the new regression assertions. 9368: live Agent protocol suite PASS, including invalid-batch no-side-effects, timeout precedence and enforcement, regex previews and durable CLI `Get` retrieval, ordered shells, partial failures, and continuation. The live test used its isolated test configuration, not the owner's running environment.

**Remaining:** Fresh independent Code Review step 3 for the corrected branch, then follow plan role transition requirements. No merge to `main` without owner approval.

### 2026-11-22 — Code Review step 3: reject bare/non-string SH regex

**Outcome:** FAIL / corrected and verified at `52faa549108d1637e3e3883014840bab977e4fed`. This reviewer's correction requires a fresh independent Code Review; this entry is not a PASS.

**Finding:** The canonical `RichDocumentParser.ParseAttributes` applied its presentation-attribute boolean fallback to root SH: a bare `regex` attribute was silently interpreted as the valid pattern `"true"`, while `regex={true}` was also accepted after expression conversion. Those malformed SH regex attributes were accepted rather than rejecting the whole batch before shell execution. The accepted SH syntax requires a quoted regex string; `regex=""` is needed for Executor recovery and must remain accepted.

**Correction:** Commit `f27550717ce63c19a7ead4603274fef7546e3d62` requires SH `regex` to use a quoted value, without changing presentation tags or other SH attributes. Commit `52faa549108d1637e3e3883014840bab977e4fed` adds Rich UI regression cases for bare regex, expression regex, and empty quoted regex. Production changes are limited to this validation path.

**Verification (isolated Windows worktree `D:\\Projects\\Mezhs-sh-parameters-verify`, exact commit `52faa549`):** 9382 fast-forwarded a clean branch; 9383 Agent API Release build PASS (0 warnings, 0 errors); 9384 Rich UI TestApp PASS (108/108, including the new rejects); 9386 `tests/policy-decoder.ps1` PASS; 9387 `tests/agent-protocol-result.ps1` PASS, including rejected-batch no-side-effects and live regex/line preview, durable `Get` recovery, timeout precedence/enforcement, ordered shells, and error continuation. The live integration suite did not add a separate filesystem side-effect case for the newly rejected bare regex; canonical parser regression directly verifies its batch rejection.

**Remaining:** Fresh independent Code Review (step 3), then follow the role transition defined in this plan. No merge to `main` without owner approval.

### 2026-11-22 - Independent Code Review 3: PASS at 52faa54

**Outcome:** PASS for implementation revision `52faa549108d1637e3e3883014840bab977e4fed`. No confirmed blocking defect or corrective implementation change was identified.

**Scope:** Reviewed the complete feature change against the current plan and base, including canonical rich-tag SH parsing and inert UI boundaries, pre-dispatch validation, command timeout resolution, optional policy defaults, Development policy instruction composition, regex-before-line-limit previews, durable Executor results, executable `Get` recovery, guidance and regression coverage. The newer regex validation rejects missing and non-string values while accepting an explicitly empty quoted regex. No previously settled architecture decision was reopened.

**Independent verification on Windows at the reviewed SHA:** MEZS runtime execution 9402: Agent API Release build PASS (0 warnings, 0 errors); 9403: Rich UI suite PASS 108/108; 9404: policy-decoder and shell suite PASS; 9405: isolated live Agent protocol suite PASS, including rejected batches without side effects, timeout override and enforcement, preview/recovery using Executor `Get`, multi-command ordering, errors and continuation. Execution 9411: Codegraph impact analysis completed, no broken Markdown links; `git diff --check` PASS. Local and remote feature HEAD matched and the worktree was clean before this documentation entry (9409, 9414).

**Limitations:** No independent forced pathological-regex timeout or production-instance test was performed. The accepted separate 16000-character model-prompt safeguard remains in effect for retrieved records. This Code Review does not replace independent Testing.

**Next:** Independent Testing (plan step 4) at the reviewed implementation SHA, followed by Deploy only with explicit owner merge approval. No merge into `main` was performed.

### 2026-11-22 - Independent Testing (step 4): PASS at 0322150

**Outcome:** PASS for branch revision 032215041cb996c7538e146bcb930b61683b6e55. No implementation changes or confirmed defects.

**Environment:** Isolated Windows feature worktree D:\Projects\Mezhs-sh-parameters-verify; Windows PowerShell 5.1.19041.6456; .NET SDK 10.0.401. Local and remote HEAD matched before testing; the worktree was clean.

**Independent verification:**
- MEZS execution 9455: Agent API Release build PASS, zero warnings and errors; git diff --check PASS.
- MEZS execution 9456: Rich UI regression suite PASS, 108/108; includes five SH parameters, malformed and duplicate attributes, regex validation, source spans and inert UI scopes.
- MEZS execution 9457: policy-decoder.ps1 PASS; policy resolution, shell and Executor evidence, Unicode and timeout regressions.
- MEZS execution 9458: isolated agent-protocol-result.ps1 PASS; invalid batches had no execution or filesystem side effects; inline commands ran once; regex/line previews retained durable output; generated Get command retrieved the original record; timeout overrides and enforcement, ordered commands, partial failures and continuation passed.
- MEZS execution 9459: Executor Help Get PASS, confirming Get requires one Int32 ID; clean worktree and matching local/remote feature revision confirmed.

**Limits:** No production-instance test or independently forced pathological-regex timeout. The separate 16000-character prompt safeguard remains. The currently running MEZS runtime rejected a root SH timeout attribute in this testing conversation, but this does not establish a feature-branch defect or the cause of the runtime difference.

**Next:** Deploy may seek explicit owner approval; do not merge into main without approval.


### 2026-11-22 - Deploy pre-approval main integration

**Change:** Integrated main revision cce2d8236d63254f726f325ff566d7bf1b98331b into the feature branch before seeking owner approval, resolving Rich UI parser and generated Codegraph conflicts. Updated .agents/deploy.md to require main synchronization, conflict resolution and integrated verification before the owner approval request.

**Verification:** Isolated Windows Agent API Release build PASS (0 warnings/errors); merged Rich UI regressions PASS 132/132; policy-decoder.ps1 PASS; isolated live agent-protocol-result.ps1 PASS; generated Codegraph native backend with zero fallbacks; staged diff validation PASS. These are integration checks, not a new independent review. Owner merge approval remains pending.

### 2026-11-22 — Owner-directed Executor-only SH parameter ownership

**Change:** Executor becomes the sole functional authority for all five SH parameters, including validation, defaults, execution timeout, regex filtering, line previews and recovery output. Rich UI and Agent only transport a generic attributes map and preserve structural and policy authorization boundaries. Invalid parameter values use the normal Executor error-result path, not Agent command-batch prevalidation. The separate Agent model-prompt character safeguard remains. Earlier valid commands are not retroactively rolled back.

**Reason:** The owner rejected splitting SH parameter responsibilities between Executor and Agent and rejected multiple functional definitions/validators of the same parameters. UI syntax remains an unavoidable structural overlap, not a second functional parameter registry.

**Authority:** Explicit owner direction in Deploy follow-up on 2026-11-22.

**Supersedes:** Earlier requirements and acceptance criteria placing SH numeric/regex validation or output formatting in Agent and rejecting a whole command batch for any invalid parameter value. Historical review and testing PASS remain evidence for their earlier implementation SHAs, not for this rework.

**Next:** Revised Development, then fresh independent Code Review and Testing; no merge to `main` without explicit owner approval.


### 2026-11-22 — Revised Executor ownership, Development step 2 verification

**Change:** Refactored the current feature branch to make Executor the only functional owner of SH option names, numeric/regex checks, default precedence, timeout, selected-line previews and durable recovery guidance. The shared rich parser captures generic command attributes with structural limits; Agent supplies policy defaults, orchestration and its independent 16000-character model prompt safeguard. An invalid SH parameter yields a failed Executor result without launching that command, leaving earlier successful shell results evidenced. The previous Executor CLI is unchanged. Added an Executor-owned API and focused parameter/presentation regressions; updated live protocol cases to distinguish structural batch rejection from Executor per-command errors. Corrected a compile-time ambiguous LINQ predicate and a regression fixture that originally contained only one line despite expecting an omission.

**Development verification at implementation SHA:** `209e7ce84a900b04b3464f3a40b33c5b7b7e96ef`, isolated Windows worktree `D:\\Projects\\Mezhs-sh-parameters-verify`.
- MEŽS execution 9492: Agent API Release build **PASS**, 0 warnings/errors; Rich UI TestApp **PASS, 132/132**.
- MEŽS execution 9494: `tests/policy-decoder.ps1` **PASS**, including Executor defaults, regex-before-line-limit output, zero/exact thresholds, recovery metadata, invalid values, policy wiring and Unicode.
- MEŽS execution 9494: isolated `tests/agent-protocol-result.ps1` **PASS**, including invalid key/regex/timeout/preview options returning Executor failures while preserving earlier executed shell evidence, structural rejection without side effects, timeout override/enforcement, regex-selected previews with full durable output, generated CLI `Get <id>` retrieval, shell ordering and partial failure.
- Generated Codegraph `.agents/codegraph/graph.json` is present on the feature branch (native backend, zero native fallbacks, ExecutorShellCommands indexed). Diff and implementation still require fresh independent scrutiny.

**Prior failures and scope:** MEŽS execution 9491 found a single C# overload ambiguity, corrected by `110fb42`; 9493 found an erroneous long-line regression expectation (one line did not exceed `limit=1`), corrected by `209e7ce`. Neither is evidence of a runtime regression after the respective fixes. No production-instance test or independent forced pathological-regex timeout case was performed. This Developer verification is not Code Review or Testing PASS.

**Next:** Fresh independent Code Review (step 3) against the pushed branch revision, then independent Testing (step 4), then Deploy review/owner approval only. No merge to `main`.

### 2026-11-22 — Independent Code Review 3: escaped quoted SH attribute correction

**Outcome:** FAIL / corrected on the feature branch. Because this reviewer changed implementation and tests, a fresh independent Code Reviewer must assess the new revision. This is not a review PASS.

**Finding:** The canonical tag tokenizer treated a backslash-escaped quote inside an SH attribute as part of the quoted value, but the command attribute reader used a plain first-quote search. A syntactically paired root SH regex containing an escaped delimiter could therefore be split into extra tokens or rejected as malformed markup before Executor received the raw attributes.

**Correction:** Commit c2339cc628e0cd6179c4f08430471a15f62b0fc0 makes SH quoted-attribute scanning use the same escaped-delimiter boundary as the tokenizer, forwarding the unchanged lexical value to Executor. Commits 3f129b19909b5801fecce97aa1ed4aed18e88880 and 2a01c05f977e1378a506fb2346ae988ee82a62dc add Rich UI and Agent parser regression assertions respectively. No Executor semantics, policy defaults, shell invocation or unrelated UI parsing were changed.

**Verification:** Source was re-fetched and inspected at the corrected commits. GitHub MEŽS Core CI for implementation/test commit 2a01c05 was in progress when first checked; no completed Windows test or independent Testing PASS is claimed here. This reviewer had no local Windows/.NET execution environment. Earlier developer tests predate the correction and do not verify it.

**Next:** Complete Windows verification of the corrected head, then obtain a fresh independent Code Review (step 3) followed by independent Testing (step 4). Do not merge into main without explicit owner approval.

### 2026-11-22 — Code Review 3 correction verification: CI timeout smoke

**Observation:** GitHub Windows MEŽS Core workflow run 45024989270 for plan revision 22043e2 failed at the Agent command protocol and real Executor smoke step. The log shows the new escaped-quote Rich UI test passing, structural/invalid-parameter cases passing, and durable CLI Get recovery passing. The next existing timeout-override smoke failed with a generic assertion; it did not log the actual shell status. Thus neither a production regression nor scheduling delay is established as the cause.

**Correction:** Commit 733c48129b4e87c40e623bb699ca862c2cd94335 gives the explicit override smoke a 15-second timeout instead of six seconds while retaining its three-second delay beyond the two-second policy default, and emits actual shell evidence if it fails again. This changes a test fixture only and is not evidence that the original failing run was caused by insufficient startup margin.

**Next:** Verify the updated branch CI and repeat independent Code Review step 3 on the complete corrected revision. Subsequent independent Testing and owner-authorized Deploy remain pending. No main merge.

### 2026-11-22 — Independent Code Review step 3: architecture regression assertion correction

**Outcome:** FAIL / corrected at implementation-and-test revision `2afb5f088536f6f8affbf42f9c1892e2b7579b0b`. The correcting reviewer cannot issue an independent PASS; start a fresh Code Reviewer before independent Testing.

**Finding:** Windows MEŽS Core CI run 45028230724 at `3fb661ef44eed339a700182b24c645140e87660f` passed the SH protocol and real Executor smoke checks, then failed `tests/agent-architecture.ps1:74` with “Agent shell commands are not routed through Executor with the configured workspace.” That static assertion still required direct `executor.Execute` and `executor.WaitAsync` invocations in Agent `Shell`, which the approved refactor intentionally replaced with `executor.ExecuteShellAsync`. Source inspection confirmed the new Agent Shell forwards the workspace, raw attributes and policy defaults into Executor's single SH entry point; the failure was an obsolete architectural test expectation, not evidence of a broken shell route.

**Correction:** Commit `2afb5f088536f6f8affbf42f9c1892e2b7579b0b` updates the assertion to check forwarding and the Executor-owned `ExecuteShellAsync` → `ExecutorShellOptions.Resolve` → `WaitAsync` contract. No production or Executor logic changed.

**Verification:** Windows CI MEŽS Core run 45028622179 passed the .NET build, rich/Chromium, Agent command protocol and real Executor smoke tests and continued past the previously failing architecture step. Codegraph workflow run 45028617477 completed successfully. The full Core workflow had not yet completed when this evidence was recorded; consult its final conclusion separately. The reviewer also inspected the canonical rich parser, Shell/Interpreter adapter, Executor option resolver/presenter and durable service, Agent prompt safeguard, policy defaults, and relevant focused/live tests. This review did not run a separate isolated local Windows test host.

**Next:** Fresh independent Code Review at current feature HEAD, followed by independent Testing and then owner-authorized Deploy. Do not merge to `main` without explicit approval.

### 2026-11-22 — Independent Code Review step 3: timeout-smoke reliability correction

**Outcome:** FAIL / test correction committed at `caa4a8345e906c507f5544b806d3f4f8afe433d4`. This correcting reviewer does not issue an independent PASS; a fresh Code Reviewer must assess the corrected branch before independent Testing.

**Observed evidence:** GitHub Windows MEŽS Core run [45029023595](https://github.com/DreigoUndead/Mezhs/actions/runs/45029023595) at `ef332510f914d05b41f8465c8152d5f128d4d049` failed `tests/agent-protocol-result.ps1:407`. The durable Executor record for the explicit `timeout=15` override was `TimedOut` with `Shell command timed out after 15 seconds` and no output, while the test launched nested `powershell -NoProfile -Command "Start-Sleep -Seconds 3; echo OVERRIDE_OK"`. The earlier parser, invalid-parameter, and durable Get checks in that run passed. This proves the 15-second budget was enforced and the specific test command did not finish; it does **not** establish whether process startup, CI scheduling, or another cause delayed the nested child. The earlier six-second failure likewise did not establish its cause.

**Correction:** Replaced the positive override test's nested PowerShell sleep with native Windows `ping -n 5 127.0.0.1 >nul && echo OVERRIDE_OK`, normally running for approximately four seconds (beyond the two-second policy default) while retaining the explicit 15-second timeout. The negative one-second timeout-enforcement test and production logic remain unchanged. This removes a separately started PowerShell process from the positive fixture but does not prove the original cause.

**Verification:** Reviewed the corrected source on GitHub. Windows CI for this correction must finish and its result be recorded before any PASS claim. No local Windows execution is claimed. Next: fresh independent Code Review, independent Testing, then owner-authorized Deploy. No change to `main`.
