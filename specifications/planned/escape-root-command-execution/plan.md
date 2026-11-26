# Unified rich-tag discovery and escape command semantics

ID: escape-root-command-execution
Branch: `escape-root-command-execution` (existing unmerged dependency/PR #74)
Level: **High-risk (scope expanded, 2026-11-24)** — architectural refactor of the canonical parser, host-command eligibility and rich UI serialization/rendering. The earlier Small owner override applied only to the superseded narrow escape fix.

## What

Build one registry-driven MEŽS rich-document pipeline with shared structural matching, registered handlers and strict host-command boundaries. The existing source is on the unmerged `escape-root-command-execution` branch; the owner approved a simpler unique-name / RequiredAncestors architecture after Step 4 identified a contextual-variant flaw. Step 3 Development is reopened for this change; fresh independent Step 4 review, Step 5 Testing and Deploy remain pending.

- **Shared syntax:** `RichDocumentParser` owns one `FindRootTags` structural matcher and one `DispatchTag`. `RichTagRegistry.Ui` contains Core UI tags; Agent extends that immutable catalog with its own `SH`, `ATTACH` and `DONE` definitions and authorizes execution separately.
- **Child ownership:** `RichTagDefinition.SupportedChildren` declares generic nesting: null admits ordinary registered UI children, an empty set admits no MEŽS child tags, and an explicit set admits only the named direct registered children. Every tag name is unique within the composed catalog and has one structural role. Optional `RequiredAncestors` lists any acceptable ancestor *names* (any one match); null allows a tag anywhere, while a nonempty list excludes it at document root. There is no `AtRoot`, contextual variant, parent override or `SearchNested`-specific parser architecture.
- **Embedded SVG:** C# registers only `svg` as an opaque `RawEditor`. Its body is data in `RichNode(kind=raw, name=svg)`; `Svg.tsx` alone decodes and validates the finite SVG grammar and renders safe React elements. Unsupported SVG is inert and visually diagnostic.
- **Frontend extension:** trusted `rich-elements/Registry.ts` statically maps symbolic UI names to host-owned TSX controls. Ordinary new tags require their C# definition, frontend component and trusted frontend registration, plus an optional handler; adding a tag must not require changing generic parsing or rendering logic.
- **Agent-only behavior:** `CommandBatchAdapter` projects candidates/visible content but does not parse tag syntax; the Agent Registry/Interpreter/policy remain the sole command execution authority.

**Authoritative execution rule:** complete registered host-command tags are eligible at document root, or as **direct children of an `escape` that is itself at document root**. All other placements are inert (including nested escapes, SVG, unknown/invalid wrappers and raw editors). This applies to every registered executable root-only tag, not only shell. Escape renders ordinary content as muted literal text, excluding executable command spans. Preserve exact original source spans and bodies, full-batch validation, the final/unique `DONE` invariant and existing behavior for Markdown code examples.

Keep safe `RichDocument` JSON transport, bounded inputs, Markdown/radio behavior, inert editor/unknown diagnostics, explicit frontend action authorization and legacy/streaming fallback. Do not add generic JS execution, DOM HTML injection, arbitrary imports or unrelated host capabilities.

Detailed current boundaries and verification: [architecture.md](architecture.md). Its older conceptual snippets must not override the current decisions above.

## Why

The original failure was missed shell commands when models wrapped root commands in `escape`. Successive special-purpose scanners and literal flags added complexity. The owner instead chose a single structural matcher, declared tag semantics and root-only command eligibility.

During implementation, additional owner challenges exposed two further architecture mistakes: a duplicate list of tag-name-dependent parser rules and C# interpretation of a separate embedded SVG language. The intended correction is not another set of special cases. A new control or embedded language should be added at its **owning extension point** without modifying generic syntax behavior or duplicating validation. Approval requires verifying this actual contract in source, not only confirming that existing behavior tests pass.

## Acceptance criteria

- [ ] One shared structural matcher (`FindRootTags`) maintains complete parent/child boundaries for known and unknown wrappers; one dispatcher invokes registered tag callbacks. No competing Agent tag scanner or tag-name-specific processing path for ordinary UI additions.
- [ ] The composed catalog rejects duplicate source names. A tag has exactly one structural definition; `SupportedChildren` constrains direct children and optional nonempty `RequiredAncestors` permits the tag only if *any* structural ancestor matches. Ancestor checks happen after structural matching (never to skip containment); inline/interactive behavior remains registered metadata. No new generic parser cases are required for ordinary UI tags.
- [ ] A hypothetical new ordinary UI tag needs only C# registration, its TSX implementation, and a trusted frontend registry binding (optional host handler). A hypothetical Agent command extends only Agent definitions/handlers, not the Core UI parser. The Code Reviewer **walks these examples against actual callers/source**, and records any exceptions with evidence.
- [ ] Each layer owns only information it needs: shared C# syntax/document structure; Agent command authorization/execution; trusted React presentation and bounded embedded-language decoding. No duplicate SVG child hierarchy or SVG-specific branches in generic C# or React rendering.
- [ ] SVG is one registered opaque raw control in C#. `Svg.tsx` validates its supported child grammar, attributes, geometry, nesting and resource bounds; invalid/malicious SVG stays inert with no HTML/JS injection or external references.
- [ ] Root-level commands and **direct** commands inside root-level `escape` are collected once, in source order, with original bodies/spans; nested commands in all other wrappers remain inert. Invalid batches have no side effects; `DONE` is final and unique.
- [ ] Unknown/malformed markup and fake closers cannot promote nested `SH`/`ATTACH` to root. Opaque boundaries, limits, source spans, Markdown, questions, raw editors and diagnostics have meaningful regression coverage.
- [ ] Versioned `RichDocument` transport, static trusted TSX registration, host-authorized actions, streaming fallback, Core/Agent catalog isolation and existing consumer behavior remain compatible or any synchronized change is explicitly justified.
- [ ] Real .NET parser/build, SSR/TypeScript, Agent/Executor and isolated Chromium checks pass at one **identified final source revision**; code review inspects actual architecture, not just test outcomes. Earlier PASS at different SHAs does not verify current code.
- [ ] Work remains on the task branch. No modification/merge to `main`, version bump or release until explicit owner approval.

## How

Use the current source and [architecture.md](architecture.md), not superseded `SearchNested` pseudocode. Prefer deleting redundant tag handling to adding flags. The generic C# parser discovers structure and produces data/candidates; `RichTagCatalog` composes reusable Core tags and Agent extensions; `RichTagHandlers` contains host-owned callbacks that cannot execute commands directly; `CommandBatchAdapter` adapts candidate records. A separate finite SVG decoder in `Svg.tsx` owns SVG-only syntax. Trusted frontend `rich-elements/Registry.ts` maps symbolic names to locally imported components; `RichDocumentView.tsx` must not gain per-control rendering cases.

Before accepting architecture, perform a **small extension exercise**:
1. Add (conceptually or through a minimal test fixture) one ordinary UI tag, one Agent-only command and one opaque embedded control.
2. Identify the exact files changed and owner of parsing, validation, rendering and execution. Flag any new generic parser/renderer condition or duplicated source of truth.
3. Compare against a simpler solution that removes or delegates a responsibility; retain additional machinery only where an established requirement/security boundary justifies it.
4. Verify both the declared contract and the implemented behavior. Passing regression tests alone does not establish architectural acceptance.

This exercise is a review method, not permission to add unnecessary example controls or speculative frameworks.

## Steps

1. **Plan:** document owner decisions, behavior, ownership, risk and acceptance criteria on this task branch. Keep active requirements in the main sections; put superseded rationale in `Changes`.
2. **Plan Review:** originally required for High-risk, but the owner explicitly waived this step for the current iteration in `exec_30641e35bca0468ea0527bbdc3767073`. That waiver does not cover review, testing or deploy.
3. **Development:** implement one generic tag pipeline and composed registry; keep SVG opaque; update synchronized C# / TSX tests, instructions and plan. The 2026-11-26 owner-approved unique-name / `RequiredAncestors` simplification reopens this step. Run developer verification on the corrected source SHA before review handoff.
4. **Code Review — independent:** after Step 3 verification, use a fresh reviewer to inspect the **current** branch head, unique-name / ancestor contract, full change, extensibility and execution boundaries. Record exact SHA and PASS/FAIL/BLOCKED evidence. Do not reuse any review PASS from superseded revisions.
5. **Testing — independent:** on the corrected reviewed revision, run isolated live Agent/Executor and Chromium tests plus the required parser/build/SSR checks. Correct and repeat review/testing as workflow requires.
6. **Deploy:** only after owner approval, reconcile advanced `main`, merge and perform release-reference work. There is **no merge authorization** in the current review request.

## Verification

Verify on one exact final commit (the reviewer must identify it and report results, not infer success):

- `dotnet run --project tests/Mezhs.RichUi.TestApp/Mezhs.RichUi.TestApp.csproj -c Release --nologo`
- `node --test tests/rich-model-ui-rendering.test.cjs` and frontend TypeScript/Vite build
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/agent-protocol-result.ps1`
- `tests/agent-web-shared-ui.ps1`, `tests/agent-architecture.ps1`, relevant API/Agent fixtures
- `dotnet build Mezhs.sln -c Release --nologo -v minimal` and `git diff --check main...HEAD`
- Independent Chromium checks using isolated browser profile/test storage, including SVG safe/unsafe output and interactive controls.
- The source-level extension exercise above, proving no new generic per-tag cases and respecting host command authority.

SVG-specific grammar/limits are now validated by trusted `Svg.tsx` and SSR tests, **not** by C# SVG-subtag validation. C# tests must verify opaque containment and no command promotion. Older successful reviews/tests do not verify the new SVG decoder. No live tests may use the owner's active profile/databases.

## Current acceptance status

The earlier owner-acceptance blocker (missing generalized registered handler dispatch after a behavioral PASS) led to a return to Development and subsequent implementation changes; its rationale remains in `Changes`. The owner subsequently rejected contextual same-name variants and approved one definition per tag name with optional `RequiredAncestors`. The current intended implementation keeps one structural finder, Core/Agent catalog composition, registered callbacks, static frontend registry and frontend-owned SVG grammar.

**Current state:** The 2026-11-26 owner decision returns the task to Step 3 Development for unique-name / `RequiredAncestors` simplification. Historical review FAIL and verification evidence belong to superseded revisions. Developer verification on the new code and subsequent independent Step 4 and Step 5 are still required. Deploy remains blocked.

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

### 2026-11-24 - Independent Step 5 Testing PASS

**Tested application revision:** `0831970d153b22e9681629b3d203dd8a4e644500` on `escape-root-command-execution`. Independent Tester used the task worktree without changing production configuration or application source.

**Observed results on this revision:**
- C# canonical rich UI parser: **131/131 PASS**; includes root commands, root escape direct commands, inert nested wrappers/escapes, structural boundaries, opaque bodies, exact source spans, radio grammar, malformed input and resource limits.
- Node rich rendering/SSR: **45/45 PASS**.
- Isolated live Agent/Executor protocol suite: **PASS**; durable command execution exactly once, ordered shells, rejected batches with no shell/filesystem side effects, nested command inertness, same-turn completion and continuation.
- Shared Agent Web UI: **19/19 PASS** plus shared component assertions; Agent architecture suite: **PASS**.
- Full `dotnet build Mezhs.sln -c Release --nologo -v minimal`: **PASS**, zero errors; TypeScript/Vite builds successful. One existing unrelated WhatsApp CS0114 warning. npm reported dependency audit advisories; they were not attributed to this change.
- Real headless Chromium (`node --test tests/rich-model-ui-browser.test.cjs`): **1/1 PASS**. Ran with Microsoft Edge, a fresh temporary browser profile and local mocked API data. Browser assertions include inert nested command-shaped editor contents, safe rich layout/SVG, editing/copying, read-only/host-authorized UI actions, question state isolation, draft/upload preservation and chat/connection switching.
- `git diff --check main...HEAD`: **PASS**. Original application revision was clean and unchanged during testing.

**Boundaries and limitations:** Chromium UI scenarios used a mocked API, while real Agent/Executor protocol scenarios were exercised separately with isolated storage. No production profile/database was used; no external provider authentication was required. No blocking defect was found in the tested scope. The advanced `main` branch still needs reconciliation during owner-approved Deploy. No merge, version bump or release was performed.

**Outcome:** **Step 5 Testing PASS**. Proceed to Step 6 Deploy only for owner-approval/reconciliation work; never merge or release without explicit owner authorization.

### 2026-11-24 — Owner identifies missing generalized handler contract; Deploy paused

**Finding:** Owner reports the agreed `Tag`/`TagContent`/registered-`Function` contract is not observable in the delivered implementation. Source inspection confirms a genuine gap: `FindRootTags`/parent tree were implemented, but the generic handler invocation contract was not. `RenderChildren` still dispatches command-role special cases, and only `escape` registers a `ProcessContent` callback.

**Change:** Mark architecture acceptance incomplete, return to development for the specific missing registry/dispatcher contract, and suspend the current Deploy handoff. Keep prior review/testing evidence scoped to its actual revision; require fresh Code Review and Testing for any implementation correction.

**Authority:** Owner MEŽS execution `exec_6755761a7e3d498cac0c7b2c41a04508`: "the deploy step is asking me for codereview now. o do not see agreed upon contract implemented anywhere. what gives?"

**Trade-off:** This reopens development without discarding the working finder, serialized UI contract or existing passing behavior. No version change, merge, or unauthorized host execution scope expansion.

### 2026-11-24 — Owner-directed Step 3 Development resumed; handler dispatch refactor

**Authority:** Owner Step 6 blocker in execution `exec_a9fc61992ad44c39be0bc042561b5dff` and subsequent implementation direction in `exec_c75cdc07b4b048639db3ddabad191b17`. **Workflow:** Step 6 Deploy remains BLOCKED; the task has returned to Step 3 Development. No further review, merge, rebase, version bump or release is authorized by this transition.

**Implementation pushed to task branch (current source revision `b9fb16213a01ee07880f355a1cdabe2d2d5d3171`):**
- `RichTagDefinition.ProcessContent` is now a registered typed callback receiving validated `RichTagContent` (parameters, content, original source/body spans, parent context and root eligibility).
- `SH`, `ATTACH`, `DONE` share a registry-owned executable handler; `escape` has its registered handler. No executable name checks are embedded in escape processing. Removed the old special-case `Command/Marker` collection branch from `RenderChildren`, the `ReadChildren` wrapper and the `DONE`-specific registry default. The top-level parser now fails immediately on structural-finder errors before Markdown projection.
- The single `DispatchTag` is used by ordinary rendering and by direct registered children of document-root escape. `LocatedTag` remains the structural parent/subtag tree; no second tree/parser or new frontend action framework was added. `RichTagContent` intentionally does not duplicate `LocatedTag.SubTags`.
- Existing `RichDocument` transport, static React mapping, root-only containment, structural finder, original command bytes/spans and Agent Interpreter/Registry execution boundary remain unchanged. Added a focused registry-handler contract assertion to the existing C# parser fixture.

**Verification status:** Source-level checks confirmed the registered callbacks, single dispatcher definition, absence of the old command-role collection branch/obsolete scanners, root escape guard and final-`DONE` rule. These are **static observations, not a test-suite PASS**. The external editing environment has no `dotnet` / C# compiler or accessible Windows MEŽS Agent host, and no GitHub status/workflow run was reported for the pushed revision. Required developer parser/build/SSR/Agent checks and independent Code Review + Testing must run on the corrected revision before any return to Deploy. No new agent was created during this implementation.

**Next:** Complete developer verification in the isolated task worktree; fix any findings on this branch. Then create fresh independent Step 4 Code Review and Step 5 Testing against the final verified revision, respecting the no-concurrent-task-worker workflow.

### 2026-11-24 — Owner architecture questions and runtime-instruction simplification

**Authority:** Owner task in MEŽS execution `exec_81ad5297295e4cb7999dab45a12db356`. Step 3 remains active; Deploy and merge remain blocked. These findings supersede the claim that registering command callbacks alone completed the target architecture.

**Implemented instruction cleanup:** Removed all discussion of `escape` from the runtime `instructions.yaml` and the generated shell guidance in `src/Mezhs.Agent.Api/Policy/PolicyDecoder.cs`. Keep the instruction focused on the supported Agent command syntax, host shell and result verification. This is prompt wording only, not a change to parser execution semantics. Source at `3ac60c2e1b3322c3c5fa0867965c38ab766b7a12`.

**Architecture findings (source inspection, not test PASS):**
- `RichDocument.cs` combines public JSON DTOs, internal parsed tag data, static UI and Agent tag registration, attribute allowlists and delegates. This blurs ownership and makes its filename misleading. `RichDocumentParser.cs` performs structural matching, Markdown projection, SVG special treatment and command-candidate dispatch; no dedicated general tag-handler class currently exists. `ProcessContent` is an inline delegate; the previous refactor registered command handlers but did not implement a dedicated owning handler class.
- `MaxNodes * 4` is a 24,000 recognized-token structural scan budget (6,000 node cap times four), used to prevent excessive syntactically valid delimiters that may not become rendered nodes. It is a generic resource bound, not an SVG or individual-control exception. Preserve a bounded tokenizer budget; improve its naming/rationale if the architecture changes.
- `svgScope`, `tag.Name == "svg"`, SVG-specific fallback, validation, budgets and text handling still exist throughout the generic parser; the frontend renderer also uses `svgScope`. They contradict the owner's desire for registry-driven per-control behavior without bespoke parser branches. SVG safety checks are necessary, but their policy should be owned by registered handler/validation contracts, not scattered conditional logic. Preserve existing SVG security, text namespace collision behavior and fallbacks.
- Many presentation controls have dedicated React component files accepting data-only mapped props; there are exceptions: one generic SVG renderer handles multiple tags, two raw editors have dedicated rendering, and Markdig nodes are dispatched in `RichDocumentView`. The UI functions are not the C# host-command handlers.
- Today all tag syntax definitions (presentation and `SH`/`ATTACH`/`DONE`) are in `Mezhs.Common.RichUi.RichTagRegistry`; `Mezhs.Agent.Api.Commands.Registry` independently defines Agent command form/behavior. This is not the proposed ownership split of UI definitions in shared Core/API and executable Agent handlers in Agent API. Any relocation requires a single injected/configured registry or equivalent composition path that preserves existing Core rendering and Agent authorization without an alternate scanner.
- File naming should follow resolved responsibility boundaries: a document DTO file should contain only document contracts, a registry file should own declarative tag metadata, and handler definitions should live with the owning subsystem. Avoid cosmetic renames before the architecture is settled.

**Required follow-up before review:** Consolidate tag-definition ownership and the real handler/dispatcher contract; eliminate SVG-aware branches from generic markup traversal through a registry-driven mechanism without weakening safe SVG validation or multiplying scanners; align frontend mapping and regressions. Decide the minimal shared Core vs Agent composition model against actual callers. Run .NET parser/build, SSR, Agent/Executor and independent Chromium checks at the corrected revision. Do not claim a Code Review or Testing PASS based on the prior `0831970d` test baseline or on static inspection. No new agent started by this inspection.

### 2026-11-24 — Step 3 follow-up: composed UI/Agent registries and registered SVG handling

**Owner direction:** execution `exec_cfe6a301e4704f16887e395a9ac26126`: complete the valid architecture findings, with no unnecessary parser exceptions. **Implementation source revision:** `30db09a1827fa536827e2c63401f677db42ba291`. This work is still **Step 3 Development, not Deploy**.

**Implemented:**
- `RichDocument.cs` now contains only the existing versioned serializable document contracts. `RichTagRegistry.cs` owns the internal `RichTagDefinition`, validated `RichTagContent`, immutable composable `RichTagCatalog` and Core/UI tag declarations. `RichTagHandlers.cs` contains host-owned callback implementations; none executes a shell action. `RichDocumentParser` takes an optional catalog instance instead of depending on a global mixed UI/Agent command registry.
- `Mezhs.Agent.Api/Commands/Registry.cs` adds `SH`, `ATTACH` and `DONE` to the Core UI catalog from `Registry.All`, preserving their forms, metadata and one registered candidate handler. The Agent Interpreter/Registry still perform independent command authorization, whole-batch validation and actual execution. `Mezhs.Api` registers the UI catalog in DI; the Agent host overrides this registration with the composed catalog. `ChatService`, `MessageService`, Agent `Parser` and existing .NET/Agent fixtures now use the appropriate catalog. This preserves the existing `RichDocument` JSON and React component mapping.
- Shared UI tag declarations stay in `Mezhs.Common` (the shared parser's dependency layer, used by `Mezhs.Api`); moving them physically into `Mezhs.Api` would force an undesirable Common→Api dependency. Agent-specific executable tag declarations are now physically in `Mezhs.Agent.Api`. Core-only parsing no longer produces executable Agent candidates for those names.
- SVG-specific parser decisions (`svgScope`, explicit `tag.Name == "svg"` tests and special normalization) were replaced with registered per-tag context, child and literal-content policies. `RichSvgValidation.ValidForTag` owns SVG value/nesting rules and `RichSvgQuota` owns the same 250-tag, depth-12 and 24,000-character limits. `RichTagHandlers.SvgTag/SvgLiteral` invoke them through callbacks; the generic traversal uses metadata and one dispatcher, not SVG name branches. The safe finite `Svg.tsx` renderer stays shared between SVG primitives to avoid duplicating trivial DOM validation.
- The one `FindRootTags` matcher remains authoritative; handler ownership of command candidate/body selection and root-level escape dispatch was clarified. The former implicit structural scan limit is now the named `MaxTagTokens=24_000` with an explicit diagnostic. Removed the obsolete discovery wrapper and an unused rendering out-parameter. No second scanner or generic client action framework was introduced.
- The owner's previous instruction simplification remains: no `escape` guidance in `instructions.yaml` or `PolicyDecoder.cs`. Added regression assertions for separate UI/Agent catalogs, SVG registry metadata and policy-instruction cleanliness. Existing Markdown, opaque editor, SVG, invalid batch and Agent/Executor fixtures remain in place.

**Verification evidence and limits:** Static inspection and source-level assertions are the only checks performed in this editing environment. The repository connector confirms committed files at the recorded source revision; the current environment has no .NET compiler, Windows MEŽS shell or live local Agent/Executor/browser profile, and no GitHub Actions run/status has yet been observed for the revision. **Do not mark .NET parser, solution build, Node SSR, live Agent/Executor, Chromium, or independent review/testing PASS without actually executing those checks.** This architecture is implemented but remains unverified. Existing independent PASS at `0831970d` covered the superseded revision only.

**Required before Step 4:** On an isolated Windows task worktree, run the plan's full Developer verification matrix including `dotnet build`, parser  and SSR tests, Agent protocol with side effects and host policy, and `git diff --check main...HEAD`. Fix any failures on the task branch. Then initiate fresh independent Step 4 Code Review and Step 5 Testing on the exact corrected revision; only owner-approved Step 6 may reconcile with advanced `main`. No agent spawned, merge, version or release action occurred in this step.

### 2026-11-24 — Registered inline content model and context variants

**Owner feedback:** MEŽS execution `exec_be4bc142936349d39ef498308bd5a91e` requires that adding a UI tag not require modifying generic parsing logic. Source revision: `40e5f225949b32f512ba4ee54772bb241664916a`.

- The Agent's `Commands/Parser.cs` is a post-parse command-batch adapter: it validates candidates against Agent `Registry`, projects `VisibleContent`, and never independently tokenizes tags. Its name could be changed for clarity without altering its role.
- `RichTagCatalog` remains one effective immutable catalog per host. Core receives `RichTagRegistry.Ui`; Agent composes `Registry.All` over it. A mutable global singleton would leak execution definitions between Core and Agent process roles and fixtures; immutable host-local composition preserves the intended extension model. Physical implementation of shared definitions stays in Common to avoid an Api/Common dependency cycle.
- Removed the parallel `InlineElements` tag-name allowlist and the `Link` name-specific nesting branch from `RichDocumentParser`. Registered `Inline` and `Interactive` flags now control those behaviors, including newly registered controls. Distinct Markdown node kinds remain defined in `RichMarkdownNodes`, not mixed with UI tag definitions in the generic parser.
- `RichTagCatalog` now resolves tag-name collisions through metadata (`Context` and `AvailableOutsideContext`) rather than a hard-coded SVG `text` branch. The shared structural finder can still recognize registered leaf/opaque forms regardless of display eligibility. Registered SVG text is an ordinary contextual definition in the catalog.
- Simplified the registry registration helpers from `Presentation` + `SvgTag` + `Tag` to general `Tag` plus one shared SVG-family preset (`SvgTag` retains explicit geometry allowlists and security rules). Adding a regular UI tag is one declarative tag entry, and optionally a registered handler.
- Added C# parser regressions for a previously unknown inline tag registered by metadata and for contextual name resolution with no tag-specific parser code.

**Remaining frontend contract question:** React currently uses an explicitly imported static component map in `RichDocumentView.tsx`. Registering a new frontend control still needs that trusted frontend map updated alongside its TSX file. This remains a separate static registry, intentionally avoiding user-supplied import paths and dynamic model-driven imports. Whether to replace it with compile-time local component discovery must be decided against the existing esbuild SSR test/build pipeline; do not silently add dynamic import behavior.

**Verification:** The source is committed, but no .NET compiler/MEŽS command output or independent tests have yet been observed by this execution. Do not claim test PASS until the shell/CI results are available. High-risk Code Review, Testing and Deploy remain blocked until correction and verification.

### 2026-11-24 — Owner-directed parent-defined SupportedChildren

**Authority:** Owner execution `exec_f4832ab65c2d4c52a0e7cbfce06eabd4` approved replacing search-nested behavior with parent-supported child sets and asked to address remaining architecture review points. **Current implementation revision:** `37b329cb449acd3d0a530a7b5a7ef1c3b8f899cd` (documentation update follows).

**Implementation:**
- `RichTagDefinition.SupportedChildren` replaces `SearchChildren`, `StrictChildren` and context/availability flags: null = unrestricted registered normal UI, empty = no nested tags, explicit IDs = only permitted registered descendants. `AtRoot` and optional distinct `Id` permit nested-only name variants. `RichTagCatalog` resolves parent-specific IDs, verifies referenced children at catalog construction, and retains a separate structural lookup so excluded wrappers still shield commands. `AllowAsChild=false` remains the explicit Agent root-only host capability restriction.
- `svg`/`g` explicitly list permitted `svg:*` graphics; SVG `text`/`tspan` permit only `svg:tspan`. The old name-based SVG nesting function has been deleted. SVG attribute validation, safe finite renderer and resource quotas remain intact. A custom registered nested-only tag and denied descendants have parser regression tests.
- Removed the redundant `expected` string argument. **Security correction:** Unknown-wrapper descendants now use their structural `LocatedTag.Parent` to enforce root-only command inertness even without a registered parent definition; added an explicit nested `SH`/`ATTACH` regression. Unknown wrappers still use nested Markdown content semantics without gaining command eligibility.
- Agent `Commands/Parser.cs` was renamed to `Commands/CommandBatchAdapter.cs`, including DI/call-site/test references. It remains an adapter over the single common C# parser, not an independent parser. Common remains the shared lower-level dependency of Core and Agent, preventing a dependency cycle.
- Moved the statically imported frontend presentation component map from `RichDocumentView.tsx` to trusted `rich-elements/Registry.ts`. Registered `Cite` and raw editor factories there as well, deleting additional name-special-case branches in the generic React renderer. This is a **static trusted allowlist**, not automatic TSX file scanning; adding a new control requires the C# tag definition, its trusted frontend registry binding, and TSX component (optional host handler). Automatic discovery would need changes to Vite and esbuild test tooling and has not been justified or authorized.
- The separate SVG React renderer and Markdown lowering remain responsible for their own specialized semantics, while the generic C# tag matcher stays unchanged for a newly declared UI tag.

**Verification:** Reviewed actual committed source, integration call sites and new regression assertions. Issued root-level MEŽS host commands to fetch an isolated detached verification worktree and run `dotnet run` parser tests, `dotnet build`, Node SSR, Agent protocol/Executor and policy tests, plus `git diff --check`. **No execution results have returned in this conversation so far; do not record these as PASS.** The GitHub connector reports no workflow-run/commit-status evidence for the source revision. Next required action is to consume the actual MEŽS shell results, correct failures, and then obtain independent Step 4 Code Review and Step 5 Testing on the corrected exact SHA. No merge, release or Deploy owner approval is implied.

### 2026-11-24 — Owner-approved opaque SVG / frontend SVG grammar

**Owner direction:** execution `exec_c41716376abc42eca48e9c5dd6297757` approved replacing the C# SVG tag hierarchy with one opaque `svg` control and moving its embedded SVG decoding/rendering to the trusted TSX component. **Source revision:** `536be72e42d15320805896f150a22f5d0fcbcaa4`. These changes remain on task branch `escape-root-command-execution` and are not a release.

**Implemented:**
- Shared C# `RichTagRegistry.Ui` now registers only `Tag("svg", RichTagRole.RawEditor, attributes: ...)`; its `SupportedChildren` set is empty. Removed `SvgTag` presets, C# graphic/text/tspan variants and their parent allowlists, `RichTagHandlers.SvgTag/SvgLiteral`, unused `RichTagDefinition` SVG-specific metadata, `RichSvgQuota` and `RichSvgValidation.cs`.
- `RichDocumentParser` transports SVG as existing data-only `RichNode(kind="raw", name="svg", text=rawBody, attributes=validatedRootAttributes)`. It does not discover rich UI or Agent command children inside that region. To avoid accidental command promotion from quoted or nested fake closers, the generic raw-editor boundary matcher now skips markup comments/CDATA, respects quoted attribute delimiters, and balances nested same-name tags; this is *boundary finding only*, not a second rich-tag semantic parser.
- `RichDocumentView.tsx` no longer carries SVG state or branches. Its trusted `rich-elements/Registry.ts` maps the one `svg` raw control to `Svg.tsx`. `Svg.tsx` validates a finite SVG/XML subset using static tag and attribute allowlists, parent-child SVG rules, safe numeric/color/path/transform values, entity decoding and resource/depth limits. It emits React SVG elements with validated props; unsupported/unsafe SVG degrades to a visible inert `rich-unknown` diagnostic. No HTML insertion, arbitrary imports, URL references or model-defined callbacks.
- Updated C# fixtures to assert raw shape, command isolation, fake closing delimiters and correct root command ordering. Updated SSR regressions for legitimate SVG rendering, unsafe tags/props/URLs, nested SVG, geometry limits, element/depth quotas and inert text/entity handling. SVG-specific validity is intentionally a frontend check now; C# no longer reports SVG-geometry validation failures.
- Updated `architecture.md` to document the final embedded-language ownership and override the previous `SupportedChildren=SVG graphics` design.

**Verification required:** GitHub commit and file-diff checks confirm source changes and removals; source inspection only, **not a compiler/renderer/MEŽS command PASS**. The connector showed zero GitHub workflow runs and combined-status contexts on the source revision. Execute `dotnet build Mezhs.sln -c Release --nologo -v minimal`, `dotnet run --project tests/Mezhs.RichUi.TestApp/Mezhs.RichUi.TestApp.csproj -c Release --nologo`, `node --test tests/rich-model-ui-rendering.test.cjs`, `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/agent-protocol-result.ps1`, frontend TypeScript/Vite production build and independent browser test on the **exact final revision** before Step 4 review/Step 5 testing. A malformed unterminated outer opaque opener still follows the existing incomplete-opener protocol; independent security testing must exercise that boundary.

**Status:** Step 3 implementation committed, runtime verification pending. No changes to `main`, deploy, release, or owner-approval status.

### 2026-11-24 — Design extension checks and current-plan reconciliation

**Authority:** Owner `exec_d5b8246f325546fa8b2830be3792f8d4` requested review of what guidance was missing; owner `exec_76856c3ac24b4595a421dffe3750b8d0` requested updating Batman/plan and starting a fresh Code Review.

**Change:** Added three compact Batman questions on extension cost, opaque embedded-language ownership and removing responsibilities instead of adding machinery. Reconciled this plan's active requirements, architecture, acceptance tests and workflow status with the latest composed registry and opaque SVG/TSX implementation; removed stale top-level `SearchNested` expectations and reclassified the old handler blocker as historical. Added a source-level extension exercise for ordinary UI controls, Agent commands and opaque embedded controls to test architecture beyond regression behavior.

**Reason:** Previous reviewer/testing passes missed a promised generic handler contract; owner intervention also exposed avoidable SVG and tag-specific complexity. Testing only existing fixtures does not prove the extension/ownership contract is real.

**Trade-off:** The extension exercise is an architectural check, not a mandatory implementation of sample controls. Prior successful tests are retained as history but do not establish current revision correctness. Exact-SHA runtime verification and independent Review/Testing are still required before owner-approved Deploy.


### 2026-11-25 — Independent Step 4 Code Review #1: FAIL after corrections

**Reviewed:** branch `escape-root-command-execution`, original head `2fa12cdc51ce345c36b5edec33efa9f3ae79d659`, then corrected through `0e14b5403cecdccf25ceb422ff93b61526b2da38`. Existing task worktree was clean before fast-forward and remained isolated from `main`. Reviewer inspected the complete task change, current plan and architecture, registered Core/Agent tag contracts, single structural finder/dispatcher, source-span command candidate handling, React static UI registry, opaque SVG renderer, and relevant tests. This was the first review of the current substantially revised architecture, not a continuation of the older historical PASS.

**Findings and corrections (all pushed on task branch):**
1. **Blocking frontend build regression:** `src/Mezhs.Web.Lib/src/RichDocumentView.tsx` passed three arguments to a two-argument `renderNode` function at two table rendering paths, causing TypeScript TS2554 and failing both Web and Agent Web builds. Corrected both call sites at `74980afc`. Original failure reproduced in `dotnet build Mezhs.sln`.
2. **Blocking contextual metadata regression:** `RichDocumentParser.NormalizeContentModel` resolved element metadata without its parent definition; nested-only registered variants (`AtRoot=false`) lost `Inline`/`Interactive` metadata during normalization, violating the extensibility/nesting contract. Carried the effective registered parent context into recursive normalization and added a focused nested-only inline test, at `d7d93f86` and `5ac3af6b`. This is a functional behavior correction requiring another independent review.
3. **Misleading failing test fixture:** explicit-ID nested-only child acceptance test assumed immediate text children, but the supported Markdig projection includes a paragraph. Replaced only the expectation with a descendant traversal at `67fb22e7`; original parser suite reproduced 145/146 with that one failed assertion, subsequent 146/146 after correction.
4. **Stale policy regression assertion:** `tests/policy-decoder.ps1` expected the old instructional phrase `Complete command pairs inside Markdown`, removed deliberately during owner-requested instruction simplification. Updated the assertion to check current authoritative shell syntax at `7d9ecbba`; the previously failing policy test then passed. This was a test expectation mismatch, not evidence that the runtime command policy failed.
5. **Diff hygiene:** removed one extra EOF blank line in trusted TSX registry `src/Mezhs.Web.Lib/src/rich-elements/Registry.ts` at `0e14b540`, clearing `git diff --check`.

**Source-level extension/ownership exercise:** An ordinary tag is introduced through a Core `RichTagDefinition`, a trusted frontend component and a registration in `rich-elements/Registry.ts`, without adding name-specific processing to generic C# `FindRootTags`/`RenderChildren` or React `RichDocumentView`; the test-only `custom-inline` and nested-only registration exercises verify this. A new Agent host command is added to the Agent-only composed `Registry.RichTags` with a registered `ProcessContent` candidate handler and separate Interpreter/policy validation, not by duplicating tag tokenization in `CommandBatchAdapter`; existing root-only `SH`/`ATTACH`/`DONE` confirm containment. Embedded SVG remains one C# opaque raw control; finite XML/SVG parsing, validation and geometry rendering belong to `Svg.tsx` and are not a second Agent-capable tag scanner. Raw-body matching is a boundary finder, not competing command authorization. Source review found no new arbitrary JS/DOM execution or unchecked host UI callback path.

**Verified on exact corrected source revision `0e14b5403cecdccf25ceb422ff93b61526b2da38`:**
- `dotnet run --project tests/Mezhs.RichUi.TestApp/Mezhs.RichUi.TestApp.csproj -c Release --nologo`: **146/146 PASS** (MEŽS command execution `10683`).
- `node --test tests/rich-model-ui-rendering.test.cjs`: **48/48 PASS** (`10683`).
- `dotnet build Mezhs.sln -c Release --nologo -v minimal`: **PASS, zero errors**, one known unrelated WhatsApp CS0114 warning (`10686`).
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/policy-decoder.ps1`: **PASS** (`10686`).
- `tests/agent-architecture.ps1`: **PASS**; `tests/agent-web-shared-ui.ps1`: **19/19 PASS** (`10689`).
- `tests/agent-protocol-result.ps1`: **PASS** on this exact corrected revision (`10693`), including isolated live Agent/Executor command-side-effect checks for valid root/escape shells, nested inertness, failed batches with no filesystem side effects, ordering, durable IDs and continuation.
- `git diff --check main...HEAD`: **PASS**; checked-out task worktree clean and tracking remote (`10683`). Frontend TypeScript/Vite builds included in the successful solution build.
- The test command outputs returned observed pass/fail results. These are not inferred from earlier review or Development runs.

**Outcome:** **Step 4 Code Review FAIL (corrected)** because functional frontend and contextual-parser behavior changed within this review. Per `.agents/code-review.md`, proceed to a **fresh independent Step 4 Code Reviewer** to verify the corrections and adjacent risks. Do not silently upgrade this review to PASS. Isolated live Chromium visual/action testing was **not** performed in this review and remains mandatory in Step 5 independent Testing. No owner approval to reconcile/merge `main`, version bump, or deploy was given. npm dependency audit emitted warnings during initial builds; not analyzed as part of the specific parser/escape behavior change.

### 2026-11-25 — Independent Step 4 Code Review #2: FAIL after correction (runtime verification pending)

**Reviewed:** branch head `f6729a31e4704558928e59d85b73fda51f411026` (documentation-only successor of previously tested `0e14b540`). Independently inspected the current parser, per-parent Core/Agent catalog, registered handler dispatch, source-span command projection, Agent Interpreter batch authorization, frontend presentation registry, SVG finite decoder, Markdown lowering, and regression fixtures. The extension exercise confirms a normal UI tag requires the Core registration, trusted TSX component and static frontend binding; an Agent command requires Agent registry/interpreter authorization; an embedded control is opaque in C# and interpreted by its own frontend owner.

**Blocking finding (functional):** `RichDocumentParser.NormalizeContentModel` previously returned `unknown` nodes without visiting their `Children` (prior lines 106–110), although `RichDocumentView` renders those descendants as functional components. A complete unknown wrapper around nested interactive `Link` controls thus bypassed the same nested-interactive rejection applied to known parents, allowing invalid nested anchors. The issue is supported by the parser/renderer code path; it is not an Agent command-authorization bypass. Corrected by recursively normalizing unknown-node descendants with a fresh outer context; added parser and SSR regression coverage on the same branch in commits `a236b606`, `db4957f3`, `7f270ee7`.

**Current outcome:** **FAIL (corrected), not PASS** under `.agents/code-review.md` because functional parser behavior changed during this review. The correction has not yet received actual .NET/Node/Windows or Chromium execution results. The reviewer must consume observed test outcomes on the resulting exact SHA, fix any regressions, and hand off to a fresh independent Step 4 Code Reviewer. Step 5 isolated Chromium/Agent Testing remains pending. Do not merge to `main` or deploy without owner approval.

### 2026-11-26 — Review #2 exact-revision verification confirmed

**Verified source revision:** `eb11ce7015b5476c023a5526e35cba2c30fb3d2c` on the isolated task worktree `D:/Projects/Mezhs-escape-execution`. `git status --short --branch` was clean/tracking the task branch, and `git diff --check main...HEAD` completed successfully (MEŽS execution `10709`).

**Observed test results** (not inherited from any earlier SHA):
- C# parser: **146/146 PASS**, including new unknown-wrapper/nested-interactive regression; `dotnet run --project tests/Mezhs.RichUi.TestApp/Mezhs.RichUi.TestApp.csproj -c Release --nologo` (execution `11089`).
- Node SSR: **48/48 PASS**, including nested-anchor/unknown-wrapper rendering and SVG safety; `node --test tests/rich-model-ui-rendering.test.cjs` (execution `11091`).
- Solution Release build: **PASS, 0 compiler warnings/errors**, including Web and Agent Web TypeScript/Vite production builds; `dotnet build Mezhs.sln -c Release --nologo -v minimal` (execution `11093`).
- Agent architecture and policy decoder: **PASS**; shared Agent Web UI **19/19 PASS** (execution `11095`).
- Isolated Agent/Executor protocol: **PASS**; valid inline/root-escape commands executed once with durable shell evidence, disallowed/nested commands remained inert, rejected batches produced no shell/file side effects, completion and continuation preserved protocol-stripped visible output (execution `11097`).

**Nonblocking observations:** npm audit during Release build reported 3 vulnerabilities in one frontend dependency set and 5 in another, plus an esbuild install-script warning. This task did not change dependency versions; review separately, do not apply automatic breaking audit fixes without scope/approval. The separate isolated live Chromium visual/interaction check has **not** been observed and remains required at Step 5 Testing. These command results show verification of the code at `eb11ce70`; this plan-only evidence commit does not introduce application-code changes.

**Workflow:** Review #2 remains **FAIL (corrected)** because a functional parser bug was fixed during review. Handoff to a *fresh, independent* Step 4 Code Reviewer on current branch head, with instruction to fetch `.agents/workflow.md`, `.agents/code-review.md`, `.agents/batman.md` and this plan from the task branch. Fresh reviewer must inspect correction, source-level extensibility and relevant regressions independently, and issue PASS/FAIL/BLOCKED by workflow. Do not skip Step 5 independent Testing or release/merge without owner approval.


### 2026-11-26 — Independent Step 4 Code Review #3: FAIL after correction; verification pending

**Reviewed starting revision:** `821333af6db3c8e4de92246ddecf441318d40536` on the task branch, whose only change after the previously verified application revision `eb11ce7015b5476c023a5526e35cba2c30fb3d2c` was this plan file. Inspected the branch-specific workflow and Batman guidance, active requirements, shared parser/catalog/handlers, Agent adapter/registry/interpreter, renderer/TSX registry/SVG decoder, and focused parser/SSR/Agent tests. The prior 146/146 parser, 48/48 SSR, Release build, Agent protocol and related PASS evidence applies to the unmodified application revision only.

**Finding (P2, functional extensibility contract; source-path evidence, not a runtime reproduction):** `RichTagCatalog.TryGetStructural(name)` returned the first registration for a surface name, while `RichTagCatalog.TryGet(name, parent)` could select a distinct parent-approved nested-only variant by ID. `FindRootTags` used the first registration to decide whether to scan child markup or treat the body as an opaque editor. Consequently, if one surface name had an opaque root variant and a nested container variant, the nested container's children were never structurally discovered even though semantic rendering later selected the container. This violates the declared `SupportedChildren` / contextual variant / ordinary-tag extension contract. Existing tests covered a nested-only *new name*, not different roles sharing one name. No evidence of unauthorized command execution in existing registered tags was found.

**Correction committed on this branch:** `25eb18e7` adds a contextual structural-lookup overload that prefers the parent's eligible variant while retaining a structural fallback for excluded/unknown wrappers; `21826646` carries the resolved structural definition through the single `LocatedTag` tree so raw/container decisions match the parent catalog; `df0e9d19a0aa7f924c617d0cb53577dd00b3dfca` adds a test-only same-surface-name root-raw/nested-container fixture, asserting nested rich child discovery, root opaque preservation and root shell candidate preservation. No tag-name branch, second scanner, Agent authorization change, frontend executable path, or Core-to-Agent dependency was added. The trusted frontend still requires an explicit TSX component/registry mapping for a real UI addition.

**Verification and limitation:** GitHub branch inspection confirms the three commits and the exact three changed files (catalog, parser and C# test). Source lines and commit diff were re-read after the edits. This review environment has no `.NET`, `PowerShell`, MEŽS Agent API or accessible Windows repository checkout, and no GitHub status checks/workflow runs were returned for `df0e9d19`. **No runtime test execution or compiler PASS is claimed for these corrections.** The regression is a static code-path finding and its source fix remains unverified, not a demonstrated successful runtime repair.

**Outcome:** **FAIL (correction committed, verification pending)**. Run the plan's parser suite, Release build, SSR/TypeScript, policy/architecture, isolated Agent/Executor protocol and diff checks on `df0e9d19` or a later identified exact application revision; correct any failures and record actual outputs. Then hand off to a fresh independent Step 4 Code Reviewer, not directly to Step 5. Step 5 isolated Chromium and live independent Testing remain pending. No approval to merge `main`, deploy, change VERSION or release.


### 2026-11-26 — Owner decision: unique tag names and RequiredAncestors

**Authority:** owner in MEŽS execution `exec_ef67468cb66c4942b60de4c928ab29bd` directed replacing the `AtRoot` concept with an optional ancestor-name requirement; follow-up `exec_98e165efe9594f318a9c0958f2d45fc8` approved implementation ("great, go for it"). The preceding discussion explicitly rejected parent-dependent tag meanings and overriding child definitions.

**Accepted contract:** Within each composed `RichTagCatalog`, source tag names are unique and determine one immutable structural role. `RequiredAncestors=null` means no ancestor restriction; a nonempty set of registered ancestor names requires **any** one name to occur in the structural parent chain, direct or indirect. No ancestors exist at document root. `SupportedChildren` continues to restrict direct eligible children when explicitly configured. `AllowAsChild=false` remains the separate Agent-only executable-root restriction and root-`escape` host dispatch remains unchanged. The shared finder always discovers full boundaries for eligible and ineligible known/unknown wrappers; ancestor eligibility applies afterwards. Invalid ancestors/duplicate names are rejected at catalog composition. SVG continues as an opaque embedded language owned by trusted TSX, not MEŽS contextual tag definitions.

**Implementation on task branch:** removed `Id`, `Key`, `AtRoot`, parent-dependent structural variant lookup, and contextual definition selection. Added `RequiredAncestors` metadata, ancestry admission based on `LocatedTag.Parent`, and regression fixtures for direct/transitive ancestor eligibility, unknown-wrapper ancestry, root rejection, unique names, bad references and inert nested host commands. Previous `dual-role` hypothetical test/implementation is superseded. Earlier plan entries remain historical evidence, not current architectural acceptance.

**Verification gate:** These edits are development changes and require fresh .NET parser/solution build, SSR/TypeScript, Agent/Executor and policy/architecture checks on a pinned code revision. No previous review/test PASS is inherited, and Step 5 isolated Chromium remains required. No merge or release authorization.
