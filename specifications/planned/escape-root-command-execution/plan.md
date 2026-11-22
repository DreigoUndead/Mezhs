# Executable root commands inside escape literals

ID: escape-root-command-execution
Branch: `escape-root-command-execution`
Level: **High-risk** — changes host command discovery and security boundaries.

## What

Make an `escape` wrapper display all ordinary source as muted literal text while recognizing and executing contained command tags that are root-only (`AllowAsChild == false`) and have a registered executable behavior. In the current protocol this includes `SH`, `ATTACH`, and `DONE` (completion). No arbitrary callback, dynamic JavaScript, or presentation component may gain host execution authority.

A command is eligible only when it is a direct command-level child of the `escape` body, not buried in another complete paired rich-UI, unknown, SVG, or raw-editor container. Other wrappers retain their current root-only, inert nested-command rules. Preserve command order, exact body, and original source spans.

Render non-executed escape content as visually muted literal text, including unsupported or malformed presentation markup. Executed command spans do not remain displayed as literal source. Do not change styling of generic literal nodes outside `escape`.

## Why

A real Agent reply for execution `exec_3583b9fdf8854c3abb640a223830ca8e` placed its intended PowerShell `SH` inside `escape`; the execution completed without creating an Executor shell record for that command. Current parser treats `escape` as wholly opaque. Owner correction of 2026-11-22 requires executable root-only command exceptions while all other escape content stays muted literal.

The November 19 owner correction in `specifications/implemented/uniform-rich-tag-matching/plan.md` remains authoritative for other rich containers but is superseded **for `escape` only**.

## Acceptance criteria

- [ ] `escape` containing ordinary prose, Markdown, presentation tags and malformed visual markup displays those bytes as muted literal text, not rendered UI.
- [ ] A complete, direct `SH` or `ATTACH` inside `escape` is discovered once, follows original order with independent root commands, and omits only its exact source span from displayed literal text.
- [ ] A direct `DONE` inside `escape` is recognized as the completion command and retains existing final-command and duplicate constraints.
- [ ] Commands in nested non-command containers inside `escape` remain inert text; commands inside all other UI/Unknown/SVG/raw wrappers remain inert as before.
- [ ] Missing command closers remain literal; malformed/unauthorized matched commands fail closed before any batch side effects; no UI or browser code gains execution ability.
- [ ] Source spans, exact shell body (including whitespace/newlines), output projection and resource budgets remain correct; examples inside escape become potentially executable by design and are documented.
- [ ] Agent policy, authoritative Agent Registry, Executor lifetime, and host-only execution authority remain unchanged.

## How

Keep `RichDocumentParser` as the only structural scanner. Make `escape` a narrowly defined command-aware literal scope rather than a general searchable UI container. Use the tag registry's `!AllowAsChild` and executable command role (`Command`/`Marker`) to select eligible host command candidates; the Agent command registry still owns actual behavior and policy validation. Reuse the canonical tag tokenizer, paired-tag matching, and existing command-validation path. Avoid a second parser, global child eligibility changes, or frontend-originated execution.

Represent `escape` display content distinctly enough for `RichDocumentView` and shared CSS to render it muted and preserve literal characters/whitespace, without applying this style to unrelated literals. Ensure display nodes cannot promote command authority.

## Steps

1. **Plan Review:** independently check command capability semantics, safe scope boundaries, and test coverage before implementation.
2. **Development:** implement the minimal parser/renderer delta on this branch; update protocol instruction text, focused tests, current specification and plan evidence.
3. **Code Review:** independently inspect host execution authority, exact-span removal, malformed/nested cases, and front-end literal rendering.
4. **Testing:** independently execute focused C#/Agent shell integration and Node/Chromium regressions, including an isolated live Agent test when practical; record tested SHA and actual results.
5. **Deploy:** request separate owner approval before merging; update release reference docs at deploy, not during development.

## Verification

Target `tests/agent-protocol-result.ps1`, canonical rich-document C# parser tests, Node/Chromium rich formatting/rendering checks, and relevant .NET build/CI. Include positive direct-command and negative nested-command fixtures, adjacent root commands, failed batch atomicity and literal styling. Do not use the owner's active Agent storage or browser profile for tests.

## Changes

### 2026-11-22 — Owner revised escape semantics

**Change:** Within `escape`, callable root-only command tags execute while all other contents render muted as literal text.
**Reason:** Real completed Agent turn contained its attempted command inside `escape` without an Executor execution.
**Authority:** Owner task for MEŽS execution `exec_3a00398ec9034e2894259baa0dc49fc9`.
**Trade-off:** Complete command syntax shown as an example directly inside `escape` is actionable subject to Agent policy. Authors must avoid accidental complete callable command tags.
**Supersedes:** The November 19 blanket prohibition on commands nested within `escape` only; other containers retain it.
