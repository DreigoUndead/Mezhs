# Agent command tags inside escape

ID: escape-agent-command-tags  
Branch: `escape-agent-command-tags`  
Level: **Small — explicit owner override (2026-11-20)**  
State: **Development correction verified in CI; independent Code Review and Testing still required**

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
