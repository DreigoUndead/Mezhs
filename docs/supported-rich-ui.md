# Supported rich UI in MEŽS model replies

MEŽS uses **one authoritative C# `RichDocumentParser`** (in `Mezhs.Common`) for Markdown, supported rich tags and Agent protocol commands. The server serializes a validated, versioned typed document; the shared React `RichDocumentView` renders only those typed nodes. No frontend parser, legacy markup fallback, or model-authored JavaScript executes. Ordinary Markdown, pipe tables, links and fenced code remain supported. During provisional/streamed content, the frontend displays inert source until the server supplies a validated document. Agent transcripts are read-only for model-authored new-turn actions.

The v1 document includes `version`, `valid`, `nodes` and `diagnostics`; nodes carry `kind`, stable `id`, `sourceSpan` (zero-based UTF-16 source positions, exclusive end), text, optional known tag name, scalar attributes, and children. Agent command records are host-only and never serialized as frontend actions. On validation failure, the whole source is shown inertly; **no Agent commands are returned or executed**.

## Static layout

The supported inert elements are `box`, `row`, `col`, `grid`, `grid-item`, `text`, `title`, `badge`, `divider`, `caption`, `icon`, `Link` (safe `http`, `https`, `mailto` destinations) and `Chart` (only a literal `json:` URI-encoded object payload, not JavaScript object expressions). Attributes are quoted strings, standalone booleans, or bounded numeric/boolean `{...}` literals. Dynamic `{expression}`, event handlers and arbitrary props are not supported. Ordinary Markdown is parsed as typed nodes between valid rich elements.

```mdx
<box gap={3}>
  <row align="start" gap={3}>
    <box background="surface-secondary" radius="lg" padding={3}>
      <icon name="scroll-text" size="lg" />
    </box>
    <box flex="1" gap={1}>
      **Logs — What happened?**
      A record emitted by `Log.Info()`.
    </box>
  </row>
  <divider color="subtle" />
  <row gap={3}>
    <icon name="git-branch" />
    **Traces — How did the operation execute?**
  </row>
</box>
```

The explicit icon subset includes `scroll-text`, `git-branch`, `chart-no-axes-combined`, `network`, `database`, `git-pull-request`, `check`, `check-circle`, `circle-check`, `alert-circle`, `x-circle`, `info`, and `arrow-down`. Other icon names display safely as text. `radius="xl"` is supported. Do not embed arbitrary React elements, HTML event handlers, code imports, CSS, or external assets.

## Inline SVG diagrams

Validated `svg` elements support bounded native drawing tags: `g`, `rect`, `path`, `line`, `polyline`, `polygon`, `circle`, `ellipse`, `text`, and `tspan`. Use numeric geometry, `viewBox`, `width="100%"`, and a plain accessible label. Static `fill`, `stroke`, `strokeWidth`, `fillOpacity`, `fontSize`, `fontWeight`, and `textAnchor` are supported. All SVG geometry must be **literal**. `{#each}`, `{#if}`, variables, formulas and arbitrary JavaScript are **not** expanded. SVG child elements use the validated SVG grammar (including nested `<g>`), not Markdown indentation rules.

```mdx
<svg viewBox="0 0 340 100" width="100%" aria-label="Telemetry flow">
  <rect x="4" y="5" rx="5" width="130" height="40" fill="#64748b" fillOpacity={0.12} />
  <text x="69" y="30" textAnchor="middle" fontSize={12}>Application</text>
  <path d="M134 25 H205" stroke="#64748b" strokeWidth={2} />
  <text x="220" y="30">Collector</text>
</svg>
```

Unsafe SVG (events, `script`, `foreignObject`, `image`, `use`, CSS, `url(...)` paint, external resources, unrecognized attributes or excessive size) is shown as escaped source, not mounted as a partial SVG.

## One-state radio question

Only one state declaration, two to twelve literal radio choices, and a button calling the single restricted `GenUI.issueNewTurn` template are supported. Values must match the initial state. `direction="col"` and boolean `block` are safe presentation options. Markdown, including multiline emphasis and inline code, is allowed within radio labels. The submission action is enabled **only** when the host explicitly grants the callback on a completed assistant message in the active chat.

```mdx
{@body const [answer,setAnswer] = DIL.useState("A")}
<box border radius="xl" padding={3} gap={3}>
  <radio-group direction="col" value={answer} onChange={setAnswer}>
  <radio value="A">
    **A — Send to the collector.** One pipeline, one endpoint.
  </radio>
  <radio value="B">
    **B — Separate exporters.** Independently configurable destinations.
  </radio>
  </radio-group>
  <button block onClick={() => GenUI.issueNewTurn(`Choice: ${answer}`)}>Confirm</button>
</box>
```

The `{@body}` declaration must be followed by the `radio-group` and `button`, optionally wrapped together in **one** statically validated `box`, as shown. No other intervening elements or nested interactive wrappers are supported. The same question also works without that `box`.

MEŽS interprets the exact form above **as data**. It does not execute the `{@body}` declaration, hooks, template interpolation or arrow function as supplied JavaScript. Unrecognized controls remain inert. Buttons are disabled without a host callback, including in Agent transcripts. Composer drafts/uploads are not used for these sends, which share the host's normal busy/offline/chat ownership checks.

## Rich question content and conditional answers

Radio question labels may now contain bounded static presentation elements, including `<text size="sm" color="secondary">…</text>` and `<icon name="arrow-right" inline/>`, alongside Markdown emphasis and inline code. Confirm buttons may contain the same validated static content. The selected radio value remains the one local state variable, and the host still exclusively controls whether the button can dispatch a turn.

The finite C# action grammar supports only two forms: one literal template interpolation of the declared radio state (for example, `` `Choice: ${answer}` ``), or the existing **strict-equality ternary chain** with one string literal per radio choice, ending with a literal fallback. MEŽS precomputes one literal message for each choice. It does **not** evaluate the expression. `!==`, concatenation, nested templates, other variable references, additional functions and general JavaScript remain unsupported. Malformed controls are inert.

For the exact four-option example, including all four conditional payloads and an arrow icon, see `tests/fixtures/rich-model-ui/custom-controls-question.md`. The corresponding parser/SSR and Chromium coverage is in `tests/rich-model-ui-rendering.test.cjs` and `tests/rich-model-ui-browser.test.cjs`.

This is **not** an arbitrary custom-component runtime or general DIL interpreter: it extends the single-state radio question and shares existing static rendering and the host callback. Other control types and state operations remain unsupported unless explicitly implemented and validated.

## Literal CodeBlock

A standalone, bounded CodeBlock is interpreted as a raw source editor or source viewer, not executable MDX. Its opening and closing tags must each be on their own line (up to three leading spaces). Supported attributes are a literal code language (letters, numbers, and common punctuation) and optional boolean editable. The contents are preserved as text even when they contain JSX-like tags, braces, Markdown fences, or strings that would otherwise be invalid MDX.

Example (the fenced sample below is documentation and is not interactive):

~~~mdx
<CodeBlock language="typescript" editable>
const item = { label: "<box>literal text</box>" };
</CodeBlock>
~~~

The component renders escaped selectable source; with editable it renders a local editor. Copy uses the browser clipboard and reports failure. It does **not** execute code, send chat messages, or grant model-defined callbacks. An invalid opening tag, excess content, or unsupported attributes do not activate the control. The closing tag is a standalone delimiter; to show the delimiter itself inside source, escape it rather than placing an unescaped standalone closing line.

Nested static layout such as box/row/col/text continues to render through the same fixed registry. For example, a CodeBlock inside a valid box is displayed as code while sibling text remains normal rich content. Agent command parsing separately protects code examples and never treats raw CodeBlock or WritingBlock bodies as executable Agent commands. **Agent execution authority is structural, not necessarily top-level.** Valid standalone `SH`, `ATTACH` and final `DONE` commands may appear inside validated `box`, `row`, `col`, `grid`, `grid-item` and Markdown lists as well as at the root. Commands inside display-only elements (`text`, `title`, `badge`, `caption`, `Link`), SVG, inline/fenced/indented code, comments, `escape`, `WritingBlock` or `CodeBlock` bodies have **no authority**; command-shaped tags in forbidden scopes invalidate the entire document. Existing Agent Registry, policy, command ordering and all-or-nothing validation remain mandatory.

## Editable WritingBlock

A bounded `WritingBlock` renders model-authored Markdown as source in a local editable text area, with a **Copy** button. The supported literal attributes are `variant` (`email`, `chat_message`, `social_post`, `document`, `standard`), `recipient`, `cc`, `bcc`, `subject`, and optional five-digit `id`. Email metadata appears as labels above the editor. The Markdown is editable text, **not** rendered HTML, and no model-authored scripts or callbacks execute.

```mdx
<WritingBlock variant="email" recipient="team@example.com" subject="Project update">
Hello team,

**Highlights**

- The prototype is ready for review.
- Feedback is welcome.
</WritingBlock>
```

The Copy button copies the current edited body only, not the email headers. It reports an error when clipboard access is unavailable. Edits are local to the displayed block and do not alter the underlying message or chat composer; there is no send action. Invalid or unsupported attributes, executable expressions and oversized content do not activate the block. Fenced examples remain literal. `options` and application-specific sending are not supported.

## Citations

`<Cite ref="turn123search3"/>` accepts a literal source ID; arrays such as `refs={["turn123search3","turn123search10"]}` are not part of the canonical v1 attribute grammar. MEŽS currently has **no trusted citation-ID-to-URL provenance metadata** in the shared message contract. These render a compact *[source unavailable]* indicator; they are **not links** and must never be presented as independently verified sources. Use a normal supported Markdown link only when the real URL is known.

## Safety and compatibility

Fenced and inline code samples always remain literal. Escaped markup and unrecognized expressions remain inert. Model-authored scripts, arbitrary DIL hooks/components, event handlers, external SVG assets, remote citation IDs as URLs, dynamic code execution, and arbitrary CSS are **not supported**. The render pipeline bounds source size, AST depth, scalar attributes, radio state/action grammar and SVG complexity. Unsupported DIL templates remain visible inert text; no template expansion occurs.

The actual sanitized telemetry fixture and adversarial tests are in `tests/fixtures/rich-model-ui/` and `tests/rich-model-ui-rendering.test.cjs`. Rendering a model response does not imply those claims are verified facts.

## Element architecture and raw WritingBlock source

Each static supported render element has a separate host-owned implementation
module in `src/Mezhs.Web.Lib/src/rich-elements/` with its own allowed attributes.
`RichDocumentParser` and `RichTagRegistry` in `Mezhs.Common` validate source into a typed DTO. The React `RichDocumentView` dispatches **only server-validated symbolic names** to fixed host-owned components; model text cannot register modules or run JavaScript. Radio, SVG, citation, chart and writing controls retain their bounded specialized renderers. Agent `Parser` consumes the **same C# parsed command candidates**, with policy checks remaining Agent-owned.

WritingBlock metadata must be literal; its opening and closing tags must each occupy
an entire Markdown line (up to three leading spaces). A closing-looking tag inside a
Markdown fence, inline code span, or HTML comment remains literal; to include a
standalone closing delimiter in editable source, escape it. The source editor can preserve
otherwise invalid MDX-like text (such as an unmatched `{`), Markdown fences,
and template examples verbatim. Content inside fences, inline code and HTML
comments is never activated. Unsupported/malformed blocks remain inert source.
