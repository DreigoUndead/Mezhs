# Executable root commands inside escape literals

ID: escape-root-command-execution
Branch: `escape-root-command-execution`
Level: **Small (owner override, 2026-11-22)** — narrowly scoped change to existing `escape` command recognition; command authority, policy validation and Executor semantics remain unchanged.

## What

Make an `escape` wrapper display all ordinary source as muted literal text while recognizing and executing contained command tags that are root-only (`AllowAsChild == false`) and have a registered executable behavior. In the current protocol this includes `SH`, `ATTACH`, and `DONE` (completion). No arbitrary callback, dynamic JavaScript, or presentation component may gain host execution authority.

A registered executable root-only command is eligible when its immediate parent is document root or `escape`, regardless of the ancestry of that `escape`. A command whose immediate parent is any other tag remains inert. Nested `escape` scopes are recognized even inside otherwise opaque rich-UI, unknown, SVG and raw-editor wrappers. Preserve command order, exact body, and original source spans.

Render non-executed escape content as visually muted literal text, including unsupported or malformed presentation markup. Executed command spans do not remain displayed as literal source. Do not change styling of generic literal nodes outside `escape`.

## Why

A real Agent reply for execution `exec_3583b9fdf8854c3abb640a223830ca8e` placed its intended PowerShell `SH` inside `escape`; the execution completed without creating an Executor shell record for that command. Current parser treats `escape` as wholly opaque. Owner correction of 2026-11-22 requires executable root-only command exceptions while all other escape content stays muted literal.

The November 19 owner correction in `specifications/implemented/uniform-rich-tag-matching/plan.md` remains authoritative for other rich containers but is superseded **for `escape` only**.

## Acceptance criteria

- [x] `escape` containing ordinary prose, Markdown, presentation tags and malformed visual markup displays those bytes as muted literal text, not rendered UI.
- [x] A complete, direct `SH` or `ATTACH` inside `escape` is discovered once, follows original order with independent root commands, and omits only its exact source span from displayed literal text.
- [x] A direct `DONE` inside `escape` is recognized as the completion command and retains existing final-command and duplicate constraints.
- [x] Direct root-only commands under `escape` execute regardless of that escape's ancestors; commands with any other immediate parent remain inert, including direct commands under UI/Unknown/SVG/raw wrappers.
- [x] Missing command closers remain literal; malformed/unauthorized matched commands fail closed before any batch side effects; no UI or browser code gains execution ability.
- [x] Source spans, exact shell body (including whitespace/newlines), output projection and resource budgets remain correct; examples inside escape become potentially executable by design and are documented.
- [x] Agent policy, authoritative Agent Registry, Executor lifetime, and host-only execution authority remain unchanged.

## How

Keep `RichDocumentParser` responsible for structural parsing and rendering. Register `escape` with a `ProcessContent` handler in `RichTagRegistry`; the handler discovers only direct registered root-only command candidates and leaves ordinary contents literal. Use a single shared `CollectCommand` path for root and escaped candidates. For opaque/raw/unknown ancestors, use a generic traversal that can invoke registered content handlers without granting their other children execution rights or rendering them as UI. Reuse the existing tokenizer, tag matching, attribute validation and source-span projection. Do not add tag-name-specific logic, a separate escape parser, or new Agent authorization. Agent Registry and policy continue to own execution.

Represent `escape` display content distinctly enough for `RichDocumentView` and shared CSS to render it muted and preserve literal characters/whitespace, without applying this style to unrelated literals. Ensure display nodes cannot promote command authority.

## Steps

1. **Development:** implement the minimal parser and muted-literal rendering delta on this branch, with targeted regressions and any necessary instruction updates.
2. **Verification:** run focused parser and Agent command tests plus the relevant renderer checks; record observed results. An independent Tester is not required for this Small owner-overridden task.
3. **Code Review (owner requested, independent):** assign a fresh Code Reviewer to inspect the complete change against the task plan, intended base and `.agents/batman.md`, emphasizing reuse of existing mechanisms, coherent ownership, duplicated scanning/validation, unnecessary complexity, and proportional tests. Follow `.agents/code-review.md` for independently substantiated findings, correction, fresh-review loop and PASS/FAIL/BLOCKED outcome. Do not proceed to Deploy until the review reaches PASS.
4. **Deploy:** request owner approval before merging; update release reference docs if behavior changes require it.

## Verification

Run targeted `tests/agent-protocol-result.ps1`, parser and renderer regressions covering direct versus nested commands, exact-span removal, invalid-batch rejection and muted literal presentation. Run the relevant build as needed. No independent verification gate is mandated. Do not use the owner's active Agent storage or browser profile for tests.

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
