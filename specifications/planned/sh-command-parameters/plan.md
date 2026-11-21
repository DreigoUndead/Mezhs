# SH per-command parameters

ID: sh-command-parameters
Branch: feature/sh-command-parameters
Level: Standard

## What

Support five optional attributes on root SH blocks: `timeout`, `limit`, `limitStart`, `limitEnd`, and `regex`. For example, `<SH timeout=60 limit=500 limitStart=10 limitEnd=10 regex="error|warning">command</SH>`. Existing plain `<SH>` works unchanged.

Each SH block has one effective execution timeout (seconds): explicit `timeout` → policy value → built-in 60 seconds. The policy timeout is a **default**, not a second maximum or a separate timeout. `timeout` must be positive.

Result presentation is separate from execution. All shell output and exit/status evidence remain intact in durable Executor history. Apply optional regex matching to output lines first, then count the selected lines against `limit`. Effective values use SH attribute → optional policy setting → built-in default:

| Attribute | Default | Semantics |
| --- | --- | --- |
| `timeout` | 60 | Seconds for this SH execution |
| `limit` | 0 | Maximum selected output lines before abbreviation; 0 disables line-based abbreviation |
| `limitStart` | 10 | Beginning lines in the abbreviated result |
| `limitEnd` | 10 | Ending lines in the abbreviated result |
| `regex` | null | Return only lines matching the regex; null leaves lines unfiltered |

All five values may be specified per SH block; policy fields are optional. Preserve the existing policy timeout field (`commandTimeoutSeconds`) as the **single** policy timeout setting, now with a 60-second fallback when absent. Do not add a second default/maximum timeout field. Add optional policy settings for the other four parameters, with names corresponding to the SH attributes.

If `limit > 0` and the selected line count exceeds `limit`, show first `limitStart` and last `limitEnd` selected lines, an omitted-line count, and identification of the stored Executor result. Show all selected lines when their count is at most `limit`; `limit=0` leaves them unrestricted by line count. Avoid duplicated lines in overlapping previews. For `limit` smaller than the implicit 10+10 preview, fit the defaults within the limit. Reject *explicit* incompatible preview sizes instead of silently exceeding the requested line limit. A zero preview count is allowed.

A separate existing model-prompt character safeguard may still abbreviate very large results, including `limit=0` or unusually long lines. Its activation and recovery guidance must be explicit; do not mislabel this as the line limit.

For any abbreviated result, MEŽS should supply a **fully formed executable SH retrieval command**, not merely tell the agent to discover the CLI. Include the actual integer Executor execution ID, the executable/assembly path valid for the running installation, and appropriate parameters (such as `limit=0`) so a repeated line-based abbreviation is not the default. The existing Executor CLI command is `Get <id>`; it returns the durable execution record, not a new shell execution of the original command. The record's `Result` property is serialized as an escaped string; distinguish full durable retrieval from the separate Agent prompt-size safeguard. Do not invent unsupported Executor CLI flags, add an Executor retrieval API, or promise an unbounded model prompt.

## Why

Give agents predictable per-block timeouts, succinct previews for long output, and optional regex filtering while preserving the complete Executor evidence and providing an immediate copyable recovery command.

## Acceptance criteria

- [ ] The canonical root-SH parser accepts the five attributes, including quoted regex values, while keeping command bodies/source spans and inert UI scopes unchanged.
- [ ] Duplicate, unsupported, malformed or invalid numeric attributes and invalid regex reject the whole command batch before executing any shell command. A regex evaluation timeout prevents unbounded pattern evaluation.
- [ ] One effective timeout uses SH → policy → 60 seconds; `timeout=0` is invalid. An explicit timeout does not encounter a *second* policy maximum. Existing policy/local config semantics are migrated compatibly.
- [ ] Policy output defaults are optional; `limit=0` means no line-based truncation. `limitStart` and `limitEnd` may be zero; implicit previews fit within small limits and explicit incompatible values are rejected.
- [ ] Apply regex line matching before counting lines. For selected count at or below `limit`, return all selected lines; for count above `limit`, show selected first/last lines, counts and truncation metadata without duplicates.
- [ ] Preserve complete stdout/stderr, exit code, errors, durable Executor records and restart/recovery command identity independent of follow-up presentation.
- [ ] On abbreviation provide an accurate ready-to-execute SH command using the installed Executor CLI `Get <id>`; verify its exact invocation against the CLI, avoid rerunning the original command, and disclose the separate prompt-character safeguard.
- [ ] Update relevant guidance, built-in example/default policy configuration, canonical parser and Agent regression tests. Do not change unrelated Low/Mid/High policy instruction content.
- [ ] The owner-approved separate MEŽS Development policy already present on this branch continues to compose both instruction blocks and passes its focused tests.

## How

Keep the existing canonical rich-tag parser → Agent command Interpreter → Shell → Executor execution path. Revise prior character-based `limit` behavior to line-based filtering and preview formatting in the Agent result follow-up path only. Prevalidate attributes/regex before dispatch; avoid re-running commands to obtain previews. Resolve the existing CLI path and execution ID from runtime context when constructing recovery guidance. Reuse the Executor durable `Get` command and existing storage; do not introduce a second parser, persistent output copy, separate retrieval API or database migration.

The current branch contains an earlier, superseded character-limit implementation and a subsequent owner-approved MEŽS Development policy. Development must reconcile existing code and tests with this **current** specification rather than treating the prior independent review PASS as approval of the revised behavior.

## Steps

1. **Plan** — Revise the authoritative plan and preserve prior implementation/review history in Changes.
   - Done when: scope, policy fallback, preview/regex semantics and recovery behavior are documented.
2. **Development** — Correct the implementation and tests against this plan, verify the real Executor CLI invocation, update branch-only guidance, and commit/push.
   - Done when: relevant build/parser/Agent integration tests pass and revised behavior is evidenced.
3. **Code Review (independent)** — Review the complete current feature branch, including the policy addition and revised SH behavior.
   - Done when: independent PASS at an exact branch revision, or resolved FAIL/BLOCKED.
4. **Testing (independent)** — Verify CLI retrieval, timeout and output scenarios on Windows and test regressions without disturbing the owner's running environment.
   - Done when: independent results are recorded against the implementation SHA.
5. **Deploy** — Present verification and obtain owner approval before any merge to `main`.
   - Done when: owner authorizes release and deploy obligations are fulfilled.

## Verification

Use Windows PowerShell 5.1 where `pwsh` is absent. Run `dotnet build src/Mezhs.Agent.Api/Mezhs.Agent.Api.csproj -c Release`, `dotnet run --project tests/Mezhs.RichUi.TestApp -c Release`, `powershell -NoProfile -ExecutionPolicy Bypass -File tests/policy-decoder.ps1`, and `powershell -NoProfile -ExecutionPolicy Bypass -File tests/agent-protocol-result.ps1`. Also verify the actual `Mezhs.Executor` CLI `Get` syntax and the emitted recovery command in an isolated environment. Include tests for missing defaults, override precedence, `limit=0`, exact threshold, exceeded threshold, small preview limits, regex zero/some/many matches, invalid regex, long lines, prompt safeguard, preserved durable output and exit codes. Record actual tests and evidence; do not infer end-to-end success from compilation.

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
