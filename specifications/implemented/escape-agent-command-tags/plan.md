# Agent command tags inside escape

ID: escape-agent-command-tags  
Branch: `escape-agent-command-tags`  
Level: **Small — explicit owner override (2026-11-20)**  
State: **Code Review #3 correction committed; fresh independent Code Review and Testing still required**

## What

At document root, `escape` discovers only *complete* registered Agent commands/markers whose definitions set `AllowAsChild=false` (currently SH, ATTACH and DONE). Incomplete delimiters, including an opener inside one escape and a closer inside another, remain literal. Never reconstruct commands across wrappers. Remaining escape content is inert and may be displayed in gray. Nested escape within a complete rich container remains inert.

The tag ownership boundary is:
- `Mezhs.Api` owns base presentation UI tag definitions.
- `Mezhs.Agent.Api` owns Agent protocol tag definitions and the Agent-specific `escape` override.
- `Mezhs.Common` owns reusable tag/handler contracts and parsing/traversal mechanics, but no Agent command names or Core presentation-tag catalog.

## Why

Only the Agent API has the concepts of Agent execution and executable `SH` commands. Core UI tag definitions therefore belong to the API that owns the base UI surface, while Agent protocol additions and overrides belong to Agent API. The shared parser should consume an injected immutable catalog and reuse one traversal rather than owning Agent-specific behavior or maintaining a second tag-search implementation. Generic command coverage should derive from the Agent command registry.

## Acceptance criteria

- [ ] Core UI definitions are supplied by `Mezhs.Api/RichUi/UiRichTags.cs`; Core-only parsing does not promote Agent command tags into executable commands.
- [ ] `Mezhs.Agent.Api/Commands/AgentRichTags.cs` composes Core UI definitions with `Registry.All` and overrides only the base `escape` handler.
- [ ] `Mezhs.Common` contains the injected registry and reusable parser mechanics, with no hardcoded Agent command names or Core UI catalog.
- [ ] Escape discovery uses the shared parser traversal/tokenizer and bounded structural matching; no separate handler-side search loop, generalized tag finder, or cross-wrapper delimiter pairing.
- [ ] Only complete, registered, root-only Agent commands/markers in a root-level escape are discovered. Other escape content remains inert; incomplete/split commands and commands nested in complete known, unknown, SVG, or raw controls are not promoted. Arbitrarily malformed UI markup is not a guaranteed containment boundary, per owner override.
- [ ] Existing command extraction, source spans, batch validation, policy checks and actual execution remain Agent-owned and unchanged in authority.
- [ ] Generic escaped-command tests enumerate `Registry.All`; specialized containment, malformed-batch, ordering, source-span and real Executor regressions remain explicit.
- [ ] Core/Agent DI and rich-message projection use the appropriate catalog; the radio grammar receives the active catalog on re-entrant validation.
- [ ] Relevant .NET build, parser/protocol/Executor tests, browser regressions and CI are verified on the final reviewed revision. Record any unavailable check as BLOCKED rather than claiming PASS.

## How

- `Mezhs.Api/RichUi/UiRichTags.cs` declares base presentation tags and exposes the Core catalog.
- `Mezhs.Agent.Api/Commands/AgentRichTags.cs` extends that catalog from `Registry.All` and supplies the Agent-specific `escape` handler.
- The `escape` handler requests eligible command discovery through `RichTagContext.Discover(...)`; it does not tokenize, search source or execute commands itself.
- `Mezhs.Common/RichUi/RichDocumentParser.cs` owns bounded discovery using the existing `ReadChildren` traversal, tokenizer, closing-boundary logic and `ReadCommand` extraction.
- Core and Agent hosts inject their respective catalogs. The Agent command parser uses the Agent catalog. Policy, interpreter, batch validation and execution remain in Agent.
- Generic command-form tests derive from `Registry.All`; explicit tests cover security boundaries and behavior not expressible as a simple registry loop.

## Steps

1. **Development — complete**
   - Implement the catalog ownership split, shared bounded discovery, API injection and registry-driven generic coverage on the task branch.
   - Done when: changes are committed and pushed; implementation and verification evidence are recorded.
2. **Code Review — next, independent**
   - A fresh Code Reviewer independently reviews the complete change against `main`, this plan, architecture, ownership, security boundaries, regressions and test coverage.
   - Correct concrete findings on this task branch, verify corrections, and record the outcome and evidence. If review returns FAIL, hand off to another fresh independent Code Reviewer after corrections.
   - Done when: reviewer reports PASS or BLOCKED for a specific unresolved issue.
3. **Testing — after Code Review PASS**
   - Verify the final post-review revision with the required targeted tests and CI/browser checks. Do not reuse results from an earlier SHA.
   - Done when: all required checks pass or remaining blockers are reported with evidence.
4. **Deploy — owner approval only**
   - Keep the PR open and task branch intact until the owner explicitly approves merging. Do not change VERSION or CHANGELOG as part of this task.

## Verification

- Canonical parser: complete escaped SH/ATTACH/DONE, registry-derived command forms, exact command bodies/spans, mixed inert UI, gray literal content, nested escape, unclosed/mismatched tags and DONE ordering.
- Security boundaries: split escape wrappers are literal and never execute; nested complete known/unknown/SVG/raw content is not promoted; invalid command batches fail closed. Malformed UI tag pairing is unsupported by owner decision.
- Agent parser/Executor: one complete escaped shell command runs once; escaped DONE works; attachment staging and ordinary root commands remain unchanged.
- Build and integration: .NET solution/build, `Mezhs.RichUi.TestApp`, `tests/agent-protocol-result.ps1`, `tests/policy-decoder.ps1`, relevant API and browser tests, plus the MEŽS Core GitHub Actions workflow on the final reviewed SHA.
- Record exact commit SHA, commands, CI run URLs/results, and checks not performed.

## Changes

### 2026-11-20 — Agent/Core tag ownership and shared discovery

**Change:** Base UI definitions moved to `Mezhs.Api/RichUi/UiRichTags.cs`; Agent command tags and the `escape` override moved to `Mezhs.Agent.Api/Commands/AgentRichTags.cs`. The shared parser accepts an injected registry and uses its existing traversal for bounded command discovery. Generic command coverage derives from `Registry.All`.

**Reason:** The Core API owns the base UI surface; only the Agent API owns execution and `SH`. A second handler-side search implementation duplicates parser responsibility and can drift from structural matching.

**Authority:** Owner decisions in the 2026-11-20 task discussion.

**Trade-off:** The shared parser exposes a small discovery contract so an owning API can request command discovery without implementing a second scanner.

### 2026-11-20 — Independent Code Review required

**Change:** Add a fresh, independent Code Review stage before final Testing. The reviewer may correct concrete findings on the existing task branch; a FAIL requires a fresh independent reviewer after corrections.

**Reason:** The owner explicitly requested a new code reviewer in the 2026-11-20 handoff.

**Authority:** Owner instruction in the 2026-11-20 task execution.

**Supersedes:** The earlier instruction in this plan not to spawn an independent agent and to rely on direct owner review.

**Trade-off:** Review must be performed by a fresh execution with no suggested conclusions or copied findings; it will use this plan and repository state as its source of truth.


### 2026-11-20 — Code review: malformed leaf containment

**Change:** The independent review found that escape discovery skipped only the opening tag of a non-self-closing registered Leaf control. A nested Agent command could therefore be promoted from malformed content such as `<escape><icon><SH>...</SH></icon></escape>`. Discovery now skips through paired leaf closes both when scanning directly and when matching a parent container's close. Added parser and real Executor regressions for direct and parent-nested malformed leaves, proving nested SH remains inert while an independent root SH still executes.

**Reason:** Malformed UI controls must not grant child command authority; this is an explicit security acceptance criterion.

**Authority:** Independent Code Review against this plan's acceptance criteria.

**Verification:** Corrected source and test changes were read back from branch head `fb248d5a4981fe8412ba71d575b20c16d5a4822b`. The tests have not been executed in this review session; no PASS is claimed.

### 2026-11-20 — Current-main integration limitation

**Change:** Recorded that the task branch is nine commits behind current `main`, the PR currently reports `mergeable=false`, and the intervening main changes also modify `RichDocumentParser.cs` and `tests/Mezhs.RichUi.TestApp/Program.cs`.

**Reason:** The final implementation must be reviewed and tested against current `main`, including its parser validation refactor, rather than treating the older PR base as current.

**Authority:** Repository comparison and PR metadata observed during this review.

**Trade-off:** Keep the task branch and draft PR intact; no changes to `main`, VERSION or CHANGELOG.


### 2026-11-20 — Owner decision on malformed leaf controls

**Change:** The owner accepts that malformed input such as `<escape><icon><SH>echo EXECUTED</SH></icon></escape>` need not work. Removed the review-added special leaf shielding and its bespoke regression checks rather than expanding protection for arbitrary invalid agent-generated markup. The ordinary parser semantics apply.

**Reason:** Keep the implementation focused on complete registered commands in root escape wrappers, without extra handling for every malformed construction.

**Authority:** Owner instruction in execution `exec_1adbdd26250b4e8db31e7dac795f45c9`.

**Verification:** Integration keeps current main's result-based parser validation and parser-regression tests. Running the resulting tests is still required; no PASS claimed.


### 2026-11-20 — Integrated current main into task branch

**Change:** Created merge commit `3f55426cf551cea82a274c490e9fc9144c4f9c83` with the task branch and `main` (`d3a23f09fe15bf5359d447a33ad5f96fb1d3edc7`) as parents. The merged tree keeps main's result-based, exception-free parser validation and test additions, while retaining Agent escape command discovery. The review-added malformed-leaf shielding and bespoke tests were excluded per the owner's explicit scope decision. Merged codegraph snapshot selected from current main; normal branch generation may refresh it.

**Reason:** The task branch had diverged and its parser overlapped a mainline refactor.

**Verification:** GitHub compare reports `behind_by=0` and PR #69 is mergeable. Tests and CI on the final revision have not yet been confirmed; no PASS is claimed here.


### 2026-11-20 — Compiler correction and developer verification

**Change:** Fixed C# compiler error CS0136 in `RichDocumentParser.cs` by renaming the discovery-local `closer` to `discoveredClose`. The branch correction was committed as `1511954c7631f92f34ded03847fc8e7d74db8548`. Updated the acceptance language and reference docs to match the owner's explicit decision not to guarantee command containment for arbitrary malformed nested markup.

**Reason:** The main-integration merge introduced a scope collision with an existing `closer` local; the first CI run on `a0789dd0a2ca1d46068cc49e293d427f779e9c72` failed to compile with CS0136. Historic malformed-input promises would otherwise contradict the owner's accepted scope override.

**Verification:** MEŽS Core Windows GitHub Actions run [44828360163](https://github.com/DreigoUndead/Mezhs/actions/runs/44828360163), attempt 2, completed **success** on code revision `1511954c7631f92f34ded03847fc8e7d74db8548`. All 21 job steps succeeded, including .NET solution build, RichUi parser tests, real Chromium browser regressions, real Agent Executor protocol smoke, Agent API/policy, browser lifecycle, and restart-recovery checks. Attempt 1 failed an unrelated group-deletion Chromium focus assertion; the same test passed on attempt 2 without a code change, so its root cause is undetermined. Fresh independent Code Review and a separate Testing-role verification are still outstanding. Documentation-only follow-up requires checking CI on the resulting branch revision.


### 2026-11-20 — Reintegrated v0.9.3 main before independent review

**Change:** Merged current `main` at `cce2d8236d63254f726f325ff566d7bf1b98331b` (release v0.9.3) into the task branch as merge commit `5f18baf237e8751c595f598408166e4e2c402c45`. Retained new `RichMarkdownNodes` fenced-Markdown corrections and regression coverage, while adapting the added test cases to the injected `AgentRichTags.Tags` parser constructor. No direct changes to the release metadata were made by this task; VERSION and CHANGELOG arrived through the main merge.

**Reason:** `main` advanced eight commits after developer verification; the independent reviewer must evaluate against current main rather than the superseded branch base.

**Verification:** GitHub comparison after merging reports `behind_by=0`. The prior successful CI run `44828360163` (attempt 2) covered code commit `1511954c`, not this integration. New mainline changes and documentation updates still require final-head CI before acceptance.


### 2026-11-20 — Post-integration developer CI and codegraph refresh

**Change:** After merging v0.9.3 main, developer verification was run on branch commit `b395a660a22566f0dd6ac7f3e873cfd462a1f3fc`; the automatic codegraph workflow then advanced the branch to `78e95d32568cf61eb0bb8d597283d460ded8abab` with exactly one changed path, `.agents/codegraph/graph.json`. No new application-source changes were introduced by that generated-graph commit.

**Verification:** Windows MEŽS Core CI run [44829727497](https://github.com/DreigoUndead/Mezhs/actions/runs/44829727497) finished **success**, all 21 job steps passed on `b395a660`, including the new v0.9.3 Markdown regressions, real Chromium, Agent protocol/Executor and concurrency/restart checks. The codegraph-only bot commit received a separate `action_required` CI run and is not counted as a pass for that SHA. This documentation update records evidence and triggers a fresh check on the current head; no PASS is claimed for the independent Code Review or subsequent Testing role.


### 2026-11-21 — Code Review #2: suppress fully consumed multi-command escape wrappers

**Finding:** `RichDocumentParser.DiscoverInOpaque` suppressed the complete `escape` wrapper only when it held exactly one command. For adjacent commands such as `<escape><SH>...</SH><DONE></escape>`, `Parser.VisibleContent` retained `<escape></escape>`, and `AgentWorker.BuildExecutionResult` treated that markup as a nonempty execution result despite there being no remaining visible content.

**Correction:** Generalized the full-wrapper source-span adjustment to one or more adjacent commands consuming the entire escape body. The first command now owns the opening wrapper and the last owns the closing wrapper; command order, body bytes, individual command records and text outside the wrapper remain unchanged. Added regression assertions in `tests/agent-protocol-result.ps1` for the Agent visible result and exact boundary spans, and in `tests/Mezhs.RichUi.TestApp/Program.cs` for Markdown flow with two adjacent escaped commands. Code commits: `1720c50d`, `332a3ff3`, `851e47d0`.

**Review outcome:** **FAIL (corrected)** under `.agents/code-review.md`: a concrete defect was fixed, so the next Code Review must be independent. Current-HEAD build, targeted tests and CI are not yet confirmed; earlier PASS evidence applies to the pre-correction revision `45ae425b`. Keep Testing as a separate later stage and do not merge without owner approval.


### 2026-11-21 — Independent Code Review #3: whitespace-only command gaps in escape wrappers

**Finding:** The earlier complete-wrapper suppression handled adjacent escaped commands but not wrappers containing only command tags and whitespace. For example, `<escape>\n<SH>echo OK</SH>\n<DONE>\n</escape>` left `<escape>...</escape>` in `Parser.VisibleContent`. `AgentWorker.BuildExecutionResult` treats that residual markup as nonempty final assistant content, even though the rich UI displays no substantive content. This is an observable result regression, not a claim of unauthorized command execution.

**Correction:** `RichDocumentParser.DiscoverInOpaque` now treats whitespace-only gaps as consumed wrapper content, extending contiguous command source spans across those gaps and the wrapper boundaries without changing command bodies, order, registry eligibility or execution policy. Regression cases were added to `tests/Mezhs.RichUi.TestApp/Program.cs` for rich Markdown flow and exact spans, and to `tests/agent-protocol-result.ps1` for parser visibility plus a real Executor execution that must not persist a nonempty final result. Code commits: `0abeee33`, `4f3c271f`, `df91f04b`, `c5a5d6ae`.

**Review outcome:** **FAIL (corrected)** under `.agents/code-review.md`; require a fresh independent Code Review and then a separate Testing role. Prior successful Windows CI on `c0c2d714` (run [44832248783](https://github.com/DreigoUndead/Mezhs/actions/runs/44832248783)) predates this correction and does not verify it. The new Windows CI run [44833590238](https://github.com/DreigoUndead/Mezhs/actions/runs/44833590238) was in progress when recorded; no test PASS is claimed yet. Keep branch/PR draft and do not merge without owner approval.
