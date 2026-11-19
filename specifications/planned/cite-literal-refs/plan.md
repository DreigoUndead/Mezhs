# Accept literal Cite reference arrays
ID: cite-literal-refs
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

## Verification
- Add targeted canonical-parser plus React SSR regression tests in `tests/rich-model-ui-rendering.test.cjs`, including negative cases.
- Run the relevant tests and review the change; obtain an independent code review before final release if available.
- No version/changelog or `main` changes before owner approval.
