# Safe rich UI rendering for model replies
ID: rich-model-ui-rendering
Level: High-risk — model-supplied markup must not cross into executable JavaScript, DOM event handlers, unsafe SVG, or chat submission without explicit user interaction.

## Goal and observed failure
The shared MEŽS rich-message renderer currently recognizes only a small static element allowlist. In the supplied screenshots, native SVG and an interactive ChatGPT DIL question (`{@body}`, `radio-group`, `radio`, `button`, `DIL.useState`, `GenUI.issueNewTurn`) show as literal source rather than as a diagram and usable controls.

Render the *supported, safe subset* of those constructs correctly in normal MEŽS conversations, without changing how ordinary Markdown, code examples, and existing rich markup behave. The rendering mechanism belongs to `src/Mezhs.Web.Lib` and must be shared by every provider; no ChatGPT-only HTML substitution or runtime-generated JavaScript.

## Scope and architecture
1. **Evidence and parser boundary.** Inspect `MarkdownContent.tsx`, `RichMarkup.tsx`, `RichExpression.ts`, `markdown.css`, `MezhsChatApp.tsx`, and `ChatSurface.tsx`. Characterize `remark-mdx` handling of the exact screenshot patterns before altering the parser. Keep model text as untrusted data: never compile MDX, evaluate JavaScript, use `dangerouslySetInnerHTML`, or install model-provided event handlers.
2. **Static SVG.** Allow a bounded set of native, inline SVG primitives sufficient for the screenshot (e.g. `svg`, `rect`, `path`, SVG `text`; add a few ordinary drawing primitives only if needed). Convert to React SVG elements using strict tag and property allowlists, validated numeric/color/path values, size limits, and node/depth limits. Allow responsive `width="100%"` and `viewBox`; reject executable/event/HTML constructs and resource loading: no `script`, `foreignObject`, `image`, `use`, `href`, URL-based CSS/paint, arbitrary `style`, animations, or `on*` handlers. SVG `text` must be distinguished from the existing DIL `text` element based on parent namespace. Preserve safe diagram labels as selectable text.
3. **Declarative controls.** Extend the same parser and renderer for `radio-group`, `radio`, and `button` at minimum, including Markdown labels inside options and a clearly styled/accessible selected state. Controls must use React-managed state scoped to the *individual rendered message*; never share a choice across messages or chats. Implement with a stable component boundary, not dynamically invoked React hooks or evaluated closures.
4. **Restricted DIL state and bindings.** Support the specific data-only pattern from the screenshot: `{@body const [choice,setChoice] = DIL.useState("B")}`, `value={choice}`, `onChange={setChoice}`, and `onClick={() => GenUI.issueNewTurn(`Grill Me Q1: ${choice}. Continue ...`)}`. Parse/validate syntax (using the existing restricted expression parser or a similarly constrained AST), and represent it as a typed declarative state/action description. Whitelist only the needed state declaration, field reads, template interpolation, and one allowed action. All other JavaScript, function calls, member access, hooks, and unsupported handlers are inert and cannot run. Do not silently produce apparently active buttons when their action cannot be supported. Shield fenced code and inline code from any DIL parsing.
5. **Submission bridge.** Add a narrow, optional host callback (for example `onIssueNewTurn(content)`) through the shared message rendering interfaces. Connect it to the current chat's existing `ChatProvider.sendMessage` flow; preserve selected connection, chat ID, model, sending/busy guards, error handling, and offline behavior. A click sends exactly the selected, interpolated question once; it does not overwrite or clear the user's unrelated draft or attach unrelated pending files. Rendering a message never submits a request. In hosts without a submission callback (e.g. read-only transcript), controls must not pretend that submission is available.
6. **Graceful degradation.** Unsupported or malformed constructs remain escaped readable source (or clearly inert controls), without disabling unrelated safe Markdown, charts, SVG blocks or earlier messages. Avoid hardcoded rewrites for the two screenshots. Keep existing provider-specific conversion and message ownership boundaries intact.
7. **Styling.** Add responsive SVG sizing, labeled radio choices, focus/keyboard-visible states, and button disabled/busy styling to shared `markdown.css`. Respect current theme/layout; avoid broad global selectors.

## Security constraints and tests
- No `eval`, `Function`, dynamic import, arbitrary model JS, DOM injection, arbitrary network calls, or model-authored shell/API invocations. A recognized `issueNewTurn` is allowed *only* on a real user click through the host-owned callback.
- Test injection attempts in JSX attributes, malformed expressions, `globalThis`/constructor/prototype access, custom event handlers, dangerous URLs, unknown DIL, embedded `<script>`, `<foreignObject>`, external SVG resources/paint, and oversized/recursive markup. Verify they're inert while adjacent content still renders.
- Preserve tests for Markdown tables, headings, ordinary links, code fences, existing `box`/`grid`/`row`/`Chart`, current loop and restricted-expression handling.
- Test the supplied representative SVG source as **an actual `<svg>` React tree**, not escaped source, with readable labels. Confirm DIL `<text>` outside SVG remains the rich text component.
- Test the complete Grill Me sample with both default B and changed A: correct checked radio, correct outgoing message string, exactly one dispatch per valid click, disabled behavior while busy/unavailable, message isolation, and unchanged unrelated draft.
- Test with both `MezhsChatApp` and `ChatTranscript` integration paths (or document why the latter is intentionally read-only); include browser interaction tests, not server-side HTML assertions alone.

## Acceptance criteria
- [ ] The example OpenTelemetry diagram renders visibly and responsively without exposing SVG source.
- [ ] The Grill Me question renders real radio options and an actionable Confirm button, with the selected answer reflected in the one new chat turn.
- [ ] Model reply content cannot execute arbitrary JavaScript or unsafe SVG; unsupported constructs fail inertly.
- [ ] Existing Markdown and supported rich blocks remain unchanged and existing tests pass.
- [ ] Multiple messages and conversations cannot leak control state or dispatch to the wrong conversation.
- [ ] Shared UI remains provider-neutral; no duplication in the React hosts.
- [ ] Relevant regression, security, and browser interaction tests are run and results tied to the exact candidate SHA.

## Verification and workflow
High-risk process per `.agents/workflow.md`:
1. Independently review **this plan** for architecture and security before implementation.
2. Implement on branch `rich-model-ui-rendering`, moving this plan from `specifications/planned/` to `specifications/implemented/` on that branch before code review. Update `specifications/current/` only if a durable user-visible behavior contract needs documenting.
3. Run developer checks including `node --test tests/agent-web-rich-formatting.test.cjs`, applicable UI/browser checks, TypeScript builds, and integration tests. Refresh codegraph if dependency edges change.
4. Separate independent **code review before independent final testing**, as required for High-risk work.
5. Report results and exact SHA for owner approval. **Do not merge without approval.** Do not edit VERSION or CHANGELOG before the eventual merge; update them only once during release finalization.
