# Executable root commands inside escape literals

ID: escape-root-command-execution
Branch: `escape-root-command-execution`
Level: **Small (owner override, 2026-11-22)** — narrowly scoped change to existing `escape` command recognition; command authority, policy validation and Executor semantics remain unchanged.

## What

Make an `escape` wrapper display all ordinary source as muted literal text while recognizing and executing contained command tags that are root-only (`AllowAsChild == false`) and have a registered executable behavior. In the current protocol this includes `SH`, `ATTACH`, and `DONE` (completion). No arbitrary callback, dynamic JavaScript, or presentation component may gain host execution authority.

A command is eligible only when it is a direct command-level child of the `escape` body, not buried in another complete paired rich-UI, unknown, SVG, or raw-editor container. Other wrappers retain their current root-only, inert nested-command rules. Preserve command order, exact body, and original source spans.

Render non-executed escape content as visually muted literal text, including unsupported or malformed presentation markup. Executed command spans do not remain displayed as literal source. Do not change styling of generic literal nodes outside `escape`.

## Why

A real Agent reply for execution `exec_3583b9fdf8854c3abb640a223830ca8e` placed its intended PowerShell `SH` inside `escape`; the execution completed without creating an Executor shell record for that command. Current parser treats `escape` as wholly opaque. Owner correction of 2026-11-22 requires executable root-only command exceptions while all other escape content stays muted literal.

The November 19 owner correction in `specifications/implemented/uniform-rich-tag-matching/plan.md` remains authoritative for other rich containers but is superseded **for `escape` only**.

## Acceptance criteria

- [x] `escape` containing ordinary prose, Markdown, presentation tags and malformed visual markup displays those bytes as muted literal text, not rendered UI.
- [x] A complete, direct `SH` or `ATTACH` inside `escape` is discovered once, follows original order with independent root commands, and omits only its exact source span from displayed literal text.
- [x] A direct `DONE` inside `escape` is recognized as the completion command and retains existing final-command and duplicate constraints.
- [x] Commands in nested non-command containers inside `escape` remain inert text; commands inside all other UI/Unknown/SVG/raw wrappers remain inert as before.
- [x] Missing command closers remain literal; malformed/unauthorized matched commands fail closed before any batch side effects; no UI or browser code gains execution ability.
- [x] Source spans, exact shell body (including whitespace/newlines), output projection and resource budgets remain correct; examples inside escape become potentially executable by design and are documented.
- [x] Agent policy, authoritative Agent Registry, Executor lifetime, and host-only execution authority remain unchanged.

## How

Keep `RichDocumentParser` as the only structural scanner. Make `escape` a narrowly defined command-aware literal scope rather than a general searchable UI container. Use the tag registry's `!AllowAsChild` and executable command role (`Command`/`Marker`) to select eligible host command candidates; the Agent command registry still owns actual behavior and policy validation. Reuse the canonical tag tokenizer, paired-tag matching, and existing command-validation path. Avoid a second parser, global child eligibility changes, or frontend-originated execution.

Represent `escape` display content distinctly enough for `RichDocumentView` and shared CSS to render it muted and preserve literal characters/whitespace, without applying this style to unrelated literals. Ensure display nodes cannot promote command authority.

## Steps

1. **Development:** implement the minimal parser and muted-literal rendering delta on this branch, with targeted regressions and any necessary instruction updates.
2. **Verification:** run focused parser and Agent command tests plus the relevant renderer checks; record observed results. Separate reviewers/testers are not required for this Small owner-overridden task.
3. **Deploy:** request owner approval before merging; update release reference docs if behavior changes require it.

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

**Outcome:** Development and required focused verification complete. Await owner approval for Deploy/merge; `main` is release-only.
