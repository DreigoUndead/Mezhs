# Rich UI instructions for MEŽS replies

Write ordinary Markdown by default. Use supported declarative elements when layout, diagrams or interaction improve the answer. **No arbitrary HTML, executable JavaScript, React callbacks, imports or CSS injection.** MEŽS parses the source once in C# and renders typed nodes. Complete unknown tags and invalid UI controls are displayed as red-bordered inert **Unknown controls** rather than invalidating the entire reply. They cannot execute nested root-only Agent commands; no model-provided tag name becomes real HTML.

## Presentation

- Layout: `box`, `row`, `col`, `grid`, `grid-item`; text: `text`, `title`, `caption`, `badge`, `divider`, `icon`.
- Content: `Link` (literal safe HTTP(S) or mailto URL), `Cite` (unverified source indicator), `Chart` (literal `json:` data), `CodeBlock`, `WritingBlock`.
- Attributes accept quoted strings, standalone booleans, bounded numeric/boolean literals (`gap={3}`), and **data-only** nested object literals (`border={{size:2,color:"danger"}}`). Objects allow unquoted keys but quoted string values, finite numbers, booleans, null, arrays and nested objects within limits; they are never evaluated as JavaScript.
- The built-in `box` styling supports `border={{size:2,color:"danger"}}` using a 1–4 px bounded size and named tones (`danger`, `warning`, `success`, `strong`, `secondary`). Unknown presentation attributes stay inert; event handlers, DOM injection, CSS strings, spreads and dynamic functions are displayed as Unknown controls instead of running.

```mdx
<box border radius="xl" padding={3} gap={2}>
  <row align="center" gap={2}>
    <icon name="info" />
    <text strong>Summary</text>
  </row>
  Markdown **inside** layout remains Markdown.
</box>
```

## Markdown inside declarative layout

Whitespace used to indent nested rich controls is presentation formatting. In a known or Unknown rich container, **four leading spaces do not create an implicit code block**. Use an explicit fenced block or `CodeBlock` when source code is intended. Outside rich containers, ordinary Markdown still supports four-space indented code. This is a scope rule in the canonical Markdown parser, not a renderer-specific workaround.

## Generic Markdown syntax

Pipe tables are part of the canonical Markdown interpretation layer, separate from the tag registry. Use ordinary GitHub-Flavored Markdown syntax; the same stage can support additional bounded Markdown constructs in the future without changing tag discovery:

```md
| Feature | Status |
| --- | --- |
| Unknown UI fallback | Supported |
| Literal border objects | Supported |
```

## SVG

Use only literal `svg` geometry with `g`, `rect`, `path`, `line`, `polyline`, `polygon`, `circle`, `ellipse`, `text`, or `tspan`. Geometry is bounded; script-capable tags, events, external resources, CSS and `url(...)` paint never mount as SVG. Invalid SVG subtrees are displayed as inert Unknown controls. No SVG loops or interpolated variables.

```mdx
<svg viewBox="0 0 160 60" width="100%" aria-label="Simple diagram">
  <rect x="5" y="5" width="60" height="35" rx="5" fill="#64748b" />
  <path d="M65 22 H100" stroke="currentColor" strokeWidth={2} />
  <text x="106" y="27">Done</text>
</svg>
```

## Radio question

One `DIL.useState` declaration, 2–12 literal radio options, one button with a single `GenUI.issueNewTurn` message. The **finite supported grammar is interpreted as data**, not executed JavaScript. The host alone authorizes sending; Agent transcript buttons remain disabled. Only a literal `${answer}` template or a finite strict-equality conditional message is supported.

```mdx
{@body const [answer,setAnswer] = DIL.useState("A")}
<box border radius="xl" padding={3} gap={2}>
  <radio-group direction="col" value={answer} onChange={setAnswer}>
    <radio value="A">**A** — First choice</radio>
    <radio value="B">**B** — Second choice</radio>
  </radio-group>
  <button block onClick={() => GenUI.issueNewTurn(`Choice: ${answer}`)}>Confirm</button>
</box>
```

## Raw editors

`CodeBlock` and `WritingBlock` accept opening and closing tags anywhere, not only on separate lines. Their opaque bodies display literal editable/copyable text; Agent commands nested inside them are not discovered.

```mdx
<WritingBlock variant="email" recipient="person@example.com" subject="Update">
Hello,

**Draft message**.
</WritingBlock>
```

Known and unknown paired tags use position-independent balanced matching for containers; complete unknown tags are rendered with a visible diagnostic border, while an unmatched opener remains ordinary text. `SH` and `ATTACH` stay root-only opaque commands (`AllowAsChild=false`); `DONE` stays a root-only marker. The only exception is root-level `<escape>`, which recognizes Agent protocol tags with `AllowAsChild=false` (SH, ATTACH, DONE), but does not pair opening and closing delimiters across escape wrappers. Other escape content (including unmatched tags) remains literal and is displayed in gray, and an escape nested within another UI wrapper cannot authorize commands. Complete commands in ordinary Markdown, including code fences, remain actionable. Agent policy, protocol validation and resource budgets remain enforced.
