# Rich UI instructions for MEŽS replies

Write ordinary Markdown by default. Use supported declarative elements when layout, diagrams or interaction improve the answer. **No arbitrary HTML, JavaScript, React, CSS, expressions, imports or executable callbacks.** MEŽS parses the source once in C# and renders validated, typed nodes; unsupported or malformed documents are displayed inertly.

## Presentation

- Layout: `box`, `row`, `col`, `grid`, `grid-item`; text: `text`, `title`, `caption`, `badge`, `divider`, `icon`.
- Content: `Link` (literal safe HTTP(S) or mailto URL), `Cite` (unverified source indicator), `Chart` (literal `json:` data), `CodeBlock`, `WritingBlock`.
- Attributes use quoted strings, standalone booleans, or numeric/boolean literals in braces, e.g. `gap={3}`. Each control interprets its own presentation attributes; unsupported ones have no effect. Event handlers, DOM injection and arbitrary style attributes are forbidden.

```mdx
<box border radius="xl" padding={3} gap={2}>
  <row align="center" gap={2}>
    <icon name="info" />
    <text strong>Summary</text>
  </row>
  Markdown **inside** layout remains Markdown.
</box>
```

## SVG

Use only literal `svg` geometry with `g`, `rect`, `path`, `line`, `polyline`, `polygon`, `circle`, `ellipse`, `text`, or `tspan`. Geometry is bounded; script-capable tags, events, external resources, CSS and `url(...)` paint are rejected. No SVG loops or interpolated variables.

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

`CodeBlock` and `WritingBlock` accept opening and closing tags anywhere, not only on separate lines. Their bodies display literal editable/copyable text; complete Agent command pairs inside them are still recognized separately by the shared parser.

```mdx
<WritingBlock variant="email" recipient="person@example.com" subject="Update">
Hello,

**Draft message**.
</WritingBlock>
```

All supported paired tags use position-independent matching. A missing paired closing tag makes its opener ordinary text. Agent `SH`/`ATTACH`/`DONE` markers are recognized anywhere in an Agent reply, including Markdown code, SVG, `<escape>` and raw editor content; these contexts **do not** suppress command execution. Do not author complete paired shell examples in Agent replies unless you intend them to execute. Agent policy and whole-batch validation still control actions.
