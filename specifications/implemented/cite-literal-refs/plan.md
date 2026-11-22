# Accept literal Cite reference arrays
ID: cite-literal-refs
Branch: cite-literal-refs
Level: **Standard** — narrow parsing compatibility fix, no execution or source-resolution authority.

## Goal and scope
Support the existing `Cite` control when an AI reply uses a data-only `refs={["turn0search0","turn0search1"]}` array. Today the shared parser turns it into an Unknown control with "Unsupported non-literal attribute expression." The existing renderer is intentionally an inert "source unavailable" marker because the transcript has no verified citation URLs.

Out of scope: resolving source references to links, executing JavaScript expressions, changing command authority, or extending all rich UI array attributes.

## Approach
- Reuse `RichTagRegistry`, `SafeLiteralReader` and the existing `Cite` renderer.
- Permit only a bounded array of quoted string literals for `Cite.refs` in the canonical C# attribute parser; serialize through the existing `json:` data representation.
- Leave dynamic expressions and non-citation array attributes inert; do not add another parser or allow arbitrary citations to become URLs.
- Keep the user-visible rich UI reference document synchronized at release per `.agents/release.md`.

## Acceptance criteria
- [ ] `<Cite ref="turn0search0"/>` still renders an inert source indicator.
- [ ] `<Cite refs={["turn0search0","turn0search1"]}/>` parses as the existing `Cite` control, not Unknown.
- [ ] Dynamic expressions and non-string citation arrays remain Unknown and do not run anything.
- [ ] Adjacent Markdown/rich content still renders, with no changes to Agent command discovery.

## Steps

1. **Development** — Reconcile the implementation with the current main parser, run developer verification, commit on the task branch.
2. **Code Review** — Independent review and correction until PASS.
3. **Testing** — Independent final verification using the updated revision.
4. **Deploy** — Owner approval is required before merge or release.

## Verification
- Add targeted canonical-parser plus React SSR regression tests in `tests/rich-model-ui-rendering.test.cjs`, including negative cases.
- Run the relevant tests and review the change; obtain an independent code review before final release if available.
- No version/changelog or `main` changes before owner approval.

## Implementation record

The original branch implementation accepts a bounded, JSON-like array of quoted strings only for `Cite.refs`. The existing inert citation indicator is reused without introducing source links or new executable syntax. Original developer CI MEŽS Core #792 passed on `46bdfabd7f34effff0478b555249a43515ce6761`; independent code review and final testing remain outstanding.

## Changes

### 2026-11-22 — Reconcile with current parser

**Change:** Integrate the existing narrow `Cite.refs` literal-array acceptance into the latest canonical `TryParseAttributes` parser, carrying over the positive and negative tests, without replacing newer parser changes. Preserve the task's Standard workflow and independent review/testing requirements.

**Reason:** The original branch diverged from `main` during recent parser changes; applying its old parser implementation directly would overwrite unrelated improvements.

**Authority:** Owner instructed continuation of PR #66; original feature scope and safety requirements are unchanged.

**Trade-off:** Citation references remain inert `[source unavailable]` indicators; no trusted URL resolution is added. New revision requires developer CI and independent verification.
