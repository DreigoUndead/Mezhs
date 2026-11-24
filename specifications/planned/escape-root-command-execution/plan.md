# Unified rich-tag discovery and escape command semantics

ID: escape-root-command-execution
Branch: `escape-root-command-execution` (existing unmerged dependency/PR #74)
Level: **High-risk (scope expanded, 2026-11-24)** — architectural refactor of the canonical parser, host-command eligibility and rich UI serialization/rendering. The earlier Small owner override applied only to the superseded narrow escape fix.

## What

Refactor MEŽS rich-tag parsing around **one reusable implementation of first-level structural matching** (`FindRootTags` conceptual contract), registry-defined tag behavior, recursive `SearchNested` discovery and a single command candidate collection path. The owner's `Tag`/`TagContent` pseudocode is an architectural guideline, not an instruction to copy record signatures or split the existing type system literally.

**Latest authoritative execution rule:** complete registered host-command tags execute at the **document root** or **directly inside an escape that itself is at document root**. All other locations are inert, including `box -> escape -> SH`, `escape -> box -> SH`, nested escapes, SVG, unknown/invalid wrappers and raw editors. The exception applies to all registered executable root-only tags (`SH`, `ATTACH`, `DONE` today), not just shell. Escape's ordinary content is muted literal text; executed command spans are omitted from that display.

Preserve other existing protocol semantics: host-only authorization and batch validation, exact original command body and spans, Markdown (including existing root commands in fences/code), safe rich UI fallback, SVG limits, typed `RichDocument` transport, React local component mapping and explicit host-authorized UI actions. Do not add arbitrary JS/DOM execution, dynamic imports, new host capabilities or a generic action framework as part of this work.

Full design: [architecture.md](architecture.md) (includes current code seams, registry/data model, finder contract, command matrix, API/React boundary, migration and regressions).

## Why

The original problem was missed shell commands when a model wrapped an intended root command in `escape`. Attempts to correct it introduced successive `ReadEscapeLiteral`, `literalScope` and `ScanOpaqueContent` mechanisms, duplicating structural tag scanning and proliferating exceptions. The owner explicitly rejected this complexity and revised the execution rule to root-only escape. A coherent registry-driven discovery/handler architecture should replace ad hoc scanners while retaining safety and presentation behavior.

## Acceptance criteria

- [ ] A single structural tag-matching **implementation** is reused by normal recursive discovery and the root-escape handler; no surviving second nesting scanner or competing command parser. Multiple calls to that one implementation on bounded content are acceptable.
- [ ] `FindRootTags` recognizes all complete structural wrappers (including excluded and unknown ones) to preserve direct-parent boundaries; `tagsSearch` filters **returned** tags, not structural matching. Balanced closing tags, opaque command bodies, self-closing tags, markers and incomplete openers retain established behavior.
- [ ] `FindTags` builds consistent direct-child relationships with assigned internal `Parent` and `SearchNested` only for ordinary nested discovery. No double insertion, duplicate handler call or source-order loss.
- [ ] Root and direct children of **root-level escape** collect registered executable root-only tags exactly once. No commands execute inside other containers or inside nested escapes. Rejected/invalid batches cannot produce partial side effects. `DONE` remains final/unique.
- [ ] Root-level escape's function has no hard-coded `SH`, `ATTACH` or `DONE` names and does not execute commands directly; it reuses first-level matching and the common candidate collector. The Agent Registry/Interpreter/policy continue to own validation and host execution.
- [ ] Original `RichDocument` JSON and frontend `RichDocumentView` component mapping remain compatible unless a justified synchronized versioned change is needed. `Parent`, handler functions and command candidates are never serialized as UI callbacks.
- [ ] Bounded validated params, markup normalization, source spans, literal/raw/unknown diagnostics, safe SVG, Markdown, React state, host action context, streaming/legacy fallback and resource limits remain correct.
- [ ] Focused .NET/parser, SSR/TypeScript, Agent/Executor and isolated live Chromium checks pass at one identified final revision. Independent reviews and tests record actual evidence. All superseded ancestry tests/spec/instructions are updated.
- [ ] No commit/merge to `main`, version bump or release change before explicit owner approval. Preserve task-branch isolation and document remaining conflicts with advanced `main`.

## How

Use [architecture.md](architecture.md) as the design and verification reference. Reuse existing `RichTagRegistry`, `RichDocument`, `RichDocumentParser`, `RichMarkdownNodes`, `RichQuestionGrammar`, `RichDocumentView` and `RichActionContext` where appropriate. Refactor or delete overlapping scanners and overgrown literal-mode flags rather than adding further wrappers. Keep C# backend tag handlers as candidate collectors/projection logic, not the same thing as React event callbacks. UI node `ControlName` is only a symbolic key resolved to a static React map; validated attributes remain inert data. Avoid rewriting the JSON parameter protocol or adding generic client-side action dispatch unless unavoidable and approved.

This design includes an explicit **root-only escape** rule that supersedes the Nov 24 "any ancestor" interpretation. Root-escape `SearchNested=false` is deliberate: its handler selects only direct root-only callable tags. Other nested wrappers are structural boundaries, not opportunities to search for executable substrings.

## Steps

1. **Plan — this update:** document the owner decisions, current architecture, affected interfaces, migration and acceptance matrix; commit on the existing task branch.
   - Done when current requirements supersede stale top-level plan content and detailed design is linked.
2. **Plan Review — independent (required):** validate scope, risk classification, feasibility, containment and proportionality against actual code and `.agents/batman.md`; correct plan findings or escalate owner-level changes.
   - Done when a fresh independent Plan Reviewer records **PASS**.
3. **Development:** following Plan Review PASS, refactor the tag discovery/processing pipeline and compatible React projection, remove obsolete scanners, update current specs/instructions, and run developer verification.
   - Done when a single cohesive implementation and regressions are committed/pushed to the task branch with evidence.
4. **Code Review — independent:** inspect full code change, security, architecture, regression coverage and diff against intended base; correct and repeat with fresh review until **PASS**.
5. **Testing — independent:** run isolated live Agent/Executor and Chromium UI checks, parser/build/SSR checks and regressions; correct defects, repeat independent verification until **PASS**.
6. **Deploy:** obtain explicit owner approval to reconcile with advanced `main` and merge; only then finalize release-reference docs and release activities.

**Handoff:** owner explicitly directed proceeding to **Step 3 Development** in execution `exec_30641e35bca0468ea0527bbdc3767073`, thereby waiving the otherwise required Step 2 Plan Review for this iteration. All remaining High-risk gates (independent Code Review, independent Testing, owner-approved Deploy) remain mandatory. Launch a fresh Developer agent using `.agents/mezhs.md`; do not claim an agent started without verifying its execution ID.

## Verification

See the detailed matrix in [architecture.md](architecture.md). At minimum:

- `dotnet run --project tests/Mezhs.RichUi.TestApp/Mezhs.RichUi.TestApp.csproj -c Release --nologo`
- `node --test tests/rich-model-ui-rendering.test.cjs`, and relevant frontend build/typecheck
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/agent-protocol-result.ps1`
- `tests/agent-web-shared-ui.ps1`, `tests/agent-architecture.ps1`, relevant API/Agent integration fixtures
- `dotnet build Mezhs.sln -c Release --nologo -v minimal`, `git diff --check main...HEAD`
- Independent isolated browser and Agent/Executor behavior checks with fresh test storage/profile and command-side-effect assertions.

Historical success at `fc586fba` and static inspection of later untested revisions do **not** verify the refactor. Keep explicit limitations and exact tested SHA; no live tests should use the owner's active profiles/databases.

## Changes

### 2026-11-22 — Owner revised escape semantics

**Change:** Within `escape`, callable root-only command tags execute while all other contents render muted as literal text.
**Reason:** Real completed Agent turn contained its attempted command inside `escape` without an Executor execution.
**Authority:** Owner task for MEŽS execution `exec_3a00398ec9034e2894259baa0dc49fc9`.
**Trade-off:** Complete command syntax shown as an example directly inside `escape` is actionable subject to Agent policy. Authors must avoid accidental complete callable command tags.
**Supersedes:** The November 19 blanket prohibition on commands nested within `escape` only; other containers retain it.

### 2026-11-22 — Owner risk classification override

**Change:** Reclassify the task from High-risk to Small and use a proportionate workflow without mandatory independent Plan Review, Code Review or Testing.
**Reason:** This is a localized `escape` parsing/presentation rule change; it does not add command capabilities or change authorization, shell execution or Executor ownership. The original classification over-weighted the affected subsystem rather than the change itself.
**Authority:** Owner correction in MEŽS execution `exec_566ad1e75a124f81a17a7f58adcafaf8`.
**Supersedes:** Initial High-risk classification and associated role gates in this plan.

### 2026-11-22 — Development verification

**Implementation:** `RichDocumentParser` recognizes direct executable root-only tags within `escape`, strips only their exact spans from the muted literal display text, and retains existing command validator/Agent Registry behavior. `RichDocumentView` and shared CSS display escaped literal segments as muted text. Runtime instructions and current specification updated.

**Tested revision:** `de2ba40fbc9562bc9694dece49d7c5799200161b`.

**Observed checks:**
- `dotnet run --project tests/Mezhs.RichUi.TestApp/Mezhs.RichUi.TestApp.csproj -c Release --nologo`: **130/130 PASS**. Includes direct `SH`/`ATTACH`/`DONE`, nested inert text, body bytes and source spans, invalid batches, and parser resource limits.
- `node --test tests/rich-model-ui-rendering.test.cjs`: **44/44 PASS**, including escaped-source HTML escaping and muted CSS.
- `powershell -NoProfile -ExecutionPolicy Bypass -File tests/agent-protocol-result.ps1`: **PASS**, including isolated live Agent/Executor proof that a direct `SH` nested in `escape` executes once with durable shell evidence, and invalid escaped commands reject without shell side effects.
- `dotnet build Mezhs.sln -c Release --nologo -v minimal`: **PASS, 0 errors, 1 warning** (CS0114 in unrelated `Mezhs.WhatsApp` test class).
- `git diff --check main...HEAD`: **PASS**. Worktree was clean at tested revision.

**Scope/limits:** Frontend checks used Node SSR; a separate Chromium live visual run was not performed. The reported original execution was not rerun retroactively; corrected behavior was established with isolated fixtures. No release documentation or merge was performed during Development.

**Outcome:** The original implementation passed the checks above at `de2ba40`. A follow-up parser-architecture correction is now in progress; those results do not verify the revised parser. Re-run focused checks before Deploy. Await owner approval for merge; `main` is release-only.


### 2026-11-22 — Reuse canonical parser traversal

**Finding:** The initial implementation added `ReadEscapeLiteral()`, a second tag-scanning loop that independently decided which nested tags shield command discovery. Although it reused the tokenizer and close helpers, it duplicated structural policy and conflicted with `.agents/batman.md` ("Does an existing mechanism already solve this problem?" / "Prefer ... one source of truth").

**Correction:** Remove `ReadEscapeLiteral()`. `ReadChildren()` now has a narrowly scoped literal mode used only for the body of `escape`: it uses the existing command recognition/attribute-validation path for direct commands and the existing paired-tag matching helpers to skip nested non-command wrappers. It does not lower Markdown, parse radio controls, validate or render nested presentation tags, or grant command eligibility to other containers. The outer escape node removes only the source spans of command candidates discovered by this traversal.

**Regression:** Add coverage for malformed nested presentation attributes and commands nested inside SVG/unknown wrappers.

**Verification (corrected revision `fc586fba4209f1eaf31830b57f13bb641f4ba21a`, 2026-11-24):**
- `dotnet run --project tests/Mezhs.RichUi.TestApp/Mezhs.RichUi.TestApp.csproj -c Release --no-restore`: **131/131 PASS**. Direct escaped commands, inert known/unknown/editor/SVG and malformed nested markup, exact spans/bodies, failed-closed batches, parser limits.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/agent-protocol-result.ps1`: **PASS**. Isolated live Agent/Executor proves escaped `SH` executes once with durable evidence and invalid escape-command batches have no shell execution or filesystem side effects. Same-turn completion and continuation also passed.
- `node --test tests/rich-model-ui-rendering.test.cjs`: **44/44 PASS**, including literal escape presentation and renderer containment.
- `dotnet build Mezhs.sln -c Release --nologo -v minimal`: **PASS**, 0 errors, 1 existing unrelated CS0114 warning in `Mezhs.WhatsApp`.
- `git diff --check main...HEAD`: **PASS**; task worktree clean and at exact `fc586fba` after tests.
- Developer diff review: **PASS**. Verified the separate scanner is removed, `ReadChildren` reuses registered command attributes/eligibility and paired tag matching, and muted escape literal text excludes only eligible host-command spans. No policy or Executor changes introduced by the refactor. This is self-review, not independent review (Small override).
- Limitations: Node SSR checks, not a new Chromium visual test. Test fixtures establish corrected behavior; the original completed execution cannot be rerun retroactively. The parser source remained unchanged after these results at `fc586fba`.

**Outcome:** Development and the required targeted verification complete on `fc586fba`. The branch awaits owner approval before Deploy/merge. As of the GitHub comparison on 2026-11-24, `main` has advanced by eight commits independently of this branch; reconcile any conflicts during Deploy after approval. Main was not changed by this task.

### 2026-11-24 — Owner adds independent Code Review

**Change:** The owner explicitly requested a fresh independent Code Review agent, with emphasis on `.agents/batman.md`, before Deploy. This is step 3; Deploy moves to step 4. Preserve the Small classification and all earlier accepted guarantees; a separate Testing role is still not mandatory.
**Authority:** MEŽS execution `exec_4aeed6330e894543b7833c72225ee906`.
**Review emphasis:** Check whether existing `ReadChildren`/registry/closing helpers suffice, whether another tag scan or duplicated authorization/validation has crept in, whether responsibilities and literal rendering boundaries are coherent, and whether the implementation and regressions are proportionate. Follow ordinary independent review rules: form findings independently and avoid reopening settled decisions without material evidence.
**Status:** Review agent creation pending; no merge authorization.

### 2026-11-24 — Owner simplifies universal escape ancestry; developer correction

**Authority:** Owner task in MEŽS execution `exec_34b4f60c1e794af398937ed5abacfd47` and follow-up `exec_0a2a44aa2dfc4497b8b62a22f62d5059`.

**Behavior correction:** Any executable `!AllowAsChild` tag is enabled when its immediate parent is `escape`, or when the tag is at root. Ancestors of the escape are irrelevant. Direct commands beneath other tags remain inert. The earlier code-review finding that `escape` itself must be root-level is withdrawn; the plan's prior broad containment wording is superseded.

**Batman correction:** One `ReadChildren` structural traversal and one registry-owned definition of `escape` as a command-aware opaque-literal scope. Balanced matching for nested escape; normal, raw and invalid opaque nodes reuse a generic exact-command-span projection rather than escape-specific name or command checks. No `SH`-specific parser branch or second scanner. Add focused parser, SSR and isolated Agent/Executor regressions for nested escape ancestry, raw editors, SVG, invalid/unknown containers, opaque command bodies and inert adjacent commands.

**Verification status:** Source changes and regression definitions were committed on the task branch through GitHub. Full .NET, PowerShell and SSR test execution is not yet verified in this agent environment; earlier 131/131, 44/44 and isolated integration passes apply to the older `fc586fba` revision only. Do not infer PASS for the new code. A fresh independent Step 3 Code Review is required after targeted verification. No merge or Deploy authorization.

### 2026-11-24 — Replace distributed literal flags with registered tag processor

**Authority:** Owner request in MEŽS execution `exec_019413f0ca094e7d9eeeaef86198f4f7`.

**Architecture:** Removed `literalScope` and `allowRootOnly` from `ReadChildren`. The registry now gives `escape` one `ProcessContent` handler; no parsing branch tests the literal tag name or any individual command name. `ProcessEscapedContent` invokes the shared opaque-content tag walk with direct-command eligibility; other opaque/raw/unknown parents invoke that walk without direct command eligibility, but still dispatch registered handlers nested within them. One `CollectCommand` validates command structure, attributes, count and exact source spans for both ordinary root-level and escaped candidates. This collects candidates only; the Agent interpreter still owns validation against Agent Registry/policy and any host actions.

**Matching/containment:** The shared balanced closing matcher shields nested registered-content processors from fake parent closers; command bodies remain opaque. Registered content handler dispatch validates its own opening-tag attributes before granting discovery permissions, including inside raw editors. Generic source-span projection strips executed candidates from literal/raw nodes. No executable path was added to the frontend.

**Regression changes:** Added direct and nested escape cases in known/unknown/invalid/SVG/raw ancestors; inert commands with other immediate parents; nested escape and parent-closing delimiters inside command bodies; invalid escape attributes; preserved source-span/body and raw presentation checks. Existing SSR and isolated Agent/Executor fixtures remain.

**Verification limitation:** GitHub source inspection confirms the old mode flags and command-name checks are absent. This environment has no .NET SDK, PowerShell or accessible Windows MEŽS Agent API; targeted C# parser tests, PowerShell Agent/Executor integration, SSR tests and full solution build have **not** been executed against this revision. Historical passing results belong to older commits. Do not treat the regression definitions or static inspection as a runtime PASS. Once executable tests are run, record evidence and proceed to a fresh independent Step 3 Code Review. No deploy/merge authorization.


### 2026-11-24 — Owner adopts unified tag registry and root-only escape, expands scope

**Change:** Replace the current repeated structural scanners with one reusable `FindRootTags` implementation, recursive `FindTags` discovery, assigned parent, and tag-specific handler dispatch. A root `escape` invokes that same finder on its body for direct registered root-only callable commands; nested `escape` is inert. Preserve typed `RichDocument` serialization, fixed React component mappings, safe params and host action context. Reclassify from Small to High-risk because the task is now a cross-parser/API/UI refactor, not the former localized escape fix. See [architecture.md](architecture.md).

**Reason:** Owner rejected the `ReadEscapeLiteral` → `literalScope` → `ScanOpaqueContent` sequence as duplicated machinery, iterated the `Tag`/`TagContent` pseudocode, explicitly changed the command matrix to `nothing -> escape -> command` only, and requested full documentation and a Developer agent.

**Authority:** MEŽS owner executions `exec_8e65c8e86c2c437cb921f7394d3da593`, `exec_89f6ae98aa55400b97aafa9149732ff0`, `exec_6c27eacf88ce442ea5a7ab764dd72955`, `exec_4e19a1ef652043a5928cc048dfac8ea2`.

**Trade-off:** Nested `escape` inside box/raw/unknown/SVG no longer executes; prior tests requiring it must be replaced with inert expectations. Handler may rescan the root `escape` body using the **same** `FindRootTags` implementation; single implementation is the invariant, not one total source traversal. Larger scope restores the independent High-risk workflow gates.

**Supersedes:** The earlier Nov 24 any-ancestor escape guarantee, the Small risk-class override for the narrow change, earlier implementation-complete claims, and older Development/Review step numbering. Previous verification remains historical only.

**Status:** Plan and architecture recorded on `escape-root-command-execution`. Await independent Plan Review before substantive Development unless the owner explicitly directs a workflow override. The local Agent API could not be reached from this execution environment; no successor execution ID has been created or verified.

### 2026-11-24 — Owner directs immediate Step 3 Development

**Change:** Explicit owner authorization to proceed directly to Step 3. Waive the independent Step 2 Plan Review for this iteration only. Development and developer verification should follow `architecture.md` and the acceptance criteria; independent Code Review (Step 4), Testing (Step 5) and explicit merge approval (Step 6) still apply.

**Authority:** Owner request in MEŽS execution `exec_30641e35bca0468ea0527bbdc3767073`: "go to step 3".

**Status:** New Developer execution to be created using local MEŽS Agent API and current policy inherited from the owner execution. Execution ID must be confirmed through the Agent API before recording launch as successful. The external connector environment cannot access loopback `127.0.0.1:5060`; this fact alone does not establish the Agent host's availability.

### 2026-11-24 — Step 3 unified finder implementation and developer verification

**Implementation:** Refactored `RichDocumentParser` to a single `FindRootTags` structural matcher with private parent/child relationships and a shared root rendering traversal. The root `escape` processor calls that same matcher on its bounded body and collects only direct registered root-only command tags through one `CollectCommand`; nested escapes, unknown/invalid UI, SVG and raw editors never promote commands. Removed `ScanOpaqueContent`, `FindContainerClose`, and `FindOpaqueClosing`. Retained serialized `RichDocument` shape, bounded static attributes, Markdown/radio, SVG validation, safe React projection and Agent Registry/policy execution ownership. Corrected `instructions.yaml`, current Agent spec and generated `PolicyDecoder` instructions to reflect root-only escape. Updated parser/SSR/Agent regression fixtures to retire the superseded any-ancestor rule.

**Baseline:** At `54cb8d18`, .NET parser tests were **130/131**, the sole failure being the already-contradictory registry assertion expecting `escape.SearchChildren=false`; SSR **45/45** passed. During refactoring three radio grammar assertions initially failed because the next structural tag truncated the bounded radio grammar input; the developer corrected the grammar span/consumed-tag handling and reran the suites. The first isolated Agent run timed out on an inert-nested-command fixture because its policy requires successful `SH` evidence for `DONE`; the fixture was corrected to execute an independent root `SH` and assert no nested side effect. Neither intermediate failure is claimed as a production failure.

**Developer verification (source revision `d1f8014c08ac33d1c06931c265fb726360b67c8d`):**
- `dotnet run --project tests/Mezhs.RichUi.TestApp/Mezhs.RichUi.TestApp.csproj -c Release --nologo`: **131/131 PASS** at `a8a843d8` (parser source unchanged since then).
- `node --test tests/rich-model-ui-rendering.test.cjs`: **45/45 PASS** at `a8a843d8` (frontend source unchanged since then).
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/agent-protocol-result.ps1`: **PASS** at `eb9f6c0a`, including isolated durable execution for root/root escape, inert nested wrappers, invalid batches without side effects, ordered shells and continuation; `PolicyDecoder` textual instructions were subsequently aligned at `d1f8014c`.
- `dotnet build Mezhs.sln -c Release --nologo -v minimal`: **PASS** at `eb9f6c0a`, zero errors, one existing unrelated CS0114 warning in WhatsApp.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/agent-web-shared-ui.ps1`: **PASS**, 19/19 SSR UI checks plus shared-Agent assertions, at `d1f8014c`.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/agent-architecture.ps1`: **PASS** at `d1f8014c`.
- Developer scanner inspection: **PASS**, old three structural helpers absent; one `FindRootTags` implementation used for both root traversal and root-escape processing; worktree clean and branch pushed. Independent review is not replaced by this self-check.

**Remaining verification:** Run the final-role tests at the exact review/test revision, including `git diff --check main...HEAD`; an independent isolated live Chromium visual/action test was **not** performed in Development. The main branch has independently advanced and must not be rebased or merged for this work without explicit owner-approved Deploy. No version/release change was made.

**Next:** Step 4 independent Code Review, then Step 5 independent Testing and Step 6 owner-approved Deploy. Owner waived only Step 2 Plan Review; no other high-risk gate is waived.

### 2026-11-24 — Independent Step 4 Code Review started

**Verified handoff:** Developer source revision `ec4b6b1fea1329cb7bd6af5190013fd2de1533f6` was confirmed clean, with parser **131/131**, SSR **45/45**, `git diff --check main...HEAD` **PASS**. Live Agent/Executor, UI, architecture and solution-build verification is recorded above. New independent Code Review was created through the local MEŽS Agent API and confirmed with GET:
- Execution ID: `exec_2335997714264889a3a73c09f5fac89e`
- Chat ID: `chat_4851f8f7b46649dbbfbf803c3e720459`
- Policy: `high`
- Initial observed status: `Running`

**Handoff scope:** Review branch `escape-root-command-execution`, follow high-risk workflow and `.agents/batman.md`; inspect semantic correctness of one structural matcher, command authority boundaries and nesting, malformed/opaque containment, limits, compatibility with radio Markdown and rich UI serialization, failure-closed behavior, and sufficiency of tests. Reviewer findings, if any, must be addressed before independent Step 5 Testing. **No merge/release authority granted.**

### 2026-11-24 - Independent Step 4 Code Review PASS

**Reviewed revision:** `dd2267e5f4173b389d5d4bbfd05cbd55b1fc7601` on `escape-root-command-execution`.

**Independent review:** Inspected the canonical RichDocument parser and registry, its structural matching and direct-parent boundaries, root escape handler, common command collector, typed document projection, Agent Registry/Interpreter validation, React component mapping, and regression coverage. No unresolved blocking defects identified. No implementation corrections required.

**Verified on reviewed revision:**
- Rich UI C# parser: 131/131 PASS.
- Frontend SSR: PASS, exit code 0.
- Full Release solution and TypeScript/Vite builds: PASS, 0 errors, 1 existing unrelated WhatsApp CS0114 warning.
- Shared Web UI: 19/19 PASS; Agent architecture checks: PASS.
- Isolated live Agent/Executor protocol suite: PASS. Verified durable single command execution, root escape execution, inert nested commands, ordered shells, fail-closed rejected batches with no shell/filesystem side effects, and continuation.
- `git diff --check main...HEAD`: PASS; reviewed task worktree clean.

**Remaining limitations:** Live Chromium interaction/visual testing was not performed by Code Review and remains mandatory for independent Step 5 Testing. npm audit reported dependency advisories during the build; no causal connection to this parser change has been established. `main` has advanced separately; no rebase, merge or release is approved.

**Outcome:** Code Review PASS. Proceed to fresh independent Step 5 Testing; Step 6 still requires explicit owner approval.
