"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const Module = require("node:module");
const { test } = require("node:test");
const { canonicalDocument } = require("./helpers/canonical-rich-document.cjs");

const root = path.resolve(__dirname, "..");
const frontend = path.join(root, "src/Mezhs.Agent.Web");
const requireFrontend = Module.createRequire(path.join(frontend, "package.json"));
const esbuild = requireFrontend("esbuild");
const React = requireFrontend("react");
const { renderToStaticMarkup } = requireFrontend("react-dom/server");
const code = esbuild.buildSync({
  entryPoints: [path.join(frontend, "node_modules/@mezhs/web-lib/src/MarkdownContent.tsx")],
  preserveSymlinks: true, bundle: true, platform: "node", format: "cjs",
  jsx: "automatic", write: false, loader: { ".css": "text" },
  external: ["react", "react/jsx-runtime", "react-dom"],
}).outputFiles[0].text;
const moduleFile = path.join(frontend, "rich-formatting-tests.cjs");
const compiled = new Module(moduleFile);
compiled.filename = moduleFile;
compiled.paths = Module._nodeModulePaths(frontend);
compiled._compile(code, moduleFile);

function html(content, props = {}) {
  const document = props.document === undefined ? canonicalDocument(content) : props.document;
  return renderToStaticMarkup(React.createElement(compiled.exports.MarkdownContent,
    { content, ...props, document }));
}
function fixture(name) {
  return fs.readFileSync(path.join(root, "tests/fixtures/rich-model-ui", name), "utf8");
}

test("WritingBlock is rendered from the C# typed document, with escaped metadata and raw edit source", () => {
  const input = '<WritingBlock variant="email" recipient="alex@example.com" subject="Hello">\n**Hi Alex,**\n\n- First item\n</WritingBlock>';
  const output = html(input);
  assert.match(output, /class="rich-writing-block"/);
  assert.match(output, /Email draft/);
  assert.match(output, /alex@example\.com/);
  assert.match(output, /aria-label="Edit writing block"/);
  assert.match(output, /\*\*Hi Alex,\*\*/);
  assert.match(output, /aria-label="Copy writing block"/);
});

test("raw CodeBlock receives literal source, never executable descendants", () => {
  const input = '<box border>\n<CodeBlock language="typescript" editable>\nconst x = "<SH>not a command</SH>";\n</CodeBlock>\n</box>';
  const output = html(input);
  assert.match(output, /class="rich-code-block"/);
  assert.match(output, /aria-label="Edit code block"/);
  assert.match(output, /&lt;SH&gt;not a command&lt;\/SH&gt;/);
  assert.doesNotMatch(output, /class="rich-question"/);
});

test("Markdown fences protect apparent rich tags", () => {
  const output = html('~~~mdx\n<WritingBlock variant="document">\nHidden\n</WritingBlock>\n~~~');
  assert.doesNotMatch(output, /class="rich-writing-block"/);
  assert.match(output, /&lt;WritingBlock/);
});

test("GFM is converted by Markdig on the server", () => {
  const output = html("| Type | Value |\n|:---|---:|\n| **Logs** | 42 |");
  assert.match(output, /<table/);
  assert.match(output, /<strong>Logs<\/strong>/);
  assert.match(output, /42/);
});

test("common Markdown blocks and inline formatting survive the typed AST", () => {
  const output = html("# Heading\n\n> A **bold** note with `inline` code.\n\n- First\n- Second");
  assert.match(output, /<h1/);
  assert.match(output, /<blockquote/);
  assert.match(output, /<strong>bold<\/strong>/);
  assert.match(output, /<code>inline<\/code>/);
  assert.match(output, /<ul/);
});

test("safe Markdown links retain their destination", () => {
  const output = html("[Docs](https://example.com)");
  assert.match(output, /href="https:\/\/example\.com"/);
});

test("unsafe Markdown destinations never become navigable", () => {
  const output = html("[Do not open](javascript:alert(1))");
  assert.doesNotMatch(output, /href="javascript:/);
});

test("nested layout nodes render using the single canonical validation pipeline", () => {
  const output = html('<box border radius="lg" padding={3}>\n<row gap={2}>\n<icon name="git-branch" />\n<text color="secondary">Traces</text>\n</row>\n<divider/>\n</box>');
  assert.match(output, /class="rich-box/);
  assert.match(output, /rich-row/);
  assert.match(output, /Traces/);
  assert.doesNotMatch(output, /&lt;row/);
});

test("representative SVG is mounted only after server validation", () => {
  const input = fixture("representative-svg.md");
  const document = canonicalDocument(input);
  assert.equal(document.valid, true);
  const output = html(input, { document });
  assert.match(output, /<svg/);
  assert.match(output, /<rect/);
  assert.match(output, /Application/);
  assert.match(output, /Collector/);
});

test("two-choice canonical radio question renders disabled without host callback", () => {
  const input = fixture("representative-question.md");
  const output = html(input);
  assert.match(output, /class="rich-question"/);
  assert.match(output, /type="radio"/);
  assert.match(output, /Two configured exporters/);
  assert.match(output, /disabled=""/);
});

test("four-choice canonical question preserves each visible answer and host-only submission", () => {
  const input = fixture("custom-controls-question.md");
  let called = false;
  const output = html(input, { onIssueNewTurn() { called = true; } });
  assert.equal(called, false);
  assert.equal((output.match(/type="radio"/g) || []).length, 4);
  assert.match(output, /Minimum interval between request starts/);
  assert.match(output, /Different semantics/);
  assert.doesNotMatch(output, /disabled=""/);
});

test("verbatim content stays inert even when typed nodes are present", () => {
  const input = '<box><text>Never activate</text></box>';
  const output = html(input, { verbatim: true });
  assert.doesNotMatch(output, /class="rich-box/);
  assert.match(output, /&lt;box/);
});

test("unparsed provisional content remains inert Markdown, never rich UI", () => {
  const input = '<box><text>Still streaming</text></box>';
  const output = html(input, { document: null });
  assert.doesNotMatch(output, /class="rich-box/);
});

test("invalid rich elements fail closed for the entire document", () => {
  const cases = [
    '<badge>Valid</badge>\n<script>alert(1)</script>',
    '<box onClick="alert(1)">Unsafe</box>',
    '<box gap={globalThis.alert(1)}>Unsafe</box>',
    '<box gap={2} gap={3}>Unsafe</box>',
    '<box {...danger}>Unsafe</box>',
    '<box>Missing closer',
    '<row><text>Wrong closer</row></text>',
    '<Chart content={globalThis.alert(1)} />',
    '<WritingBlock variant="email" onClick="alert(1)">Unsafe</WritingBlock>',
  ];
  for (const input of cases) {
    const document = canonicalDocument(input);
    assert.equal(document.valid, false, input);
    const output = html(input, { document });
    assert.match(output, /class="prompt-text"/);
    assert.doesNotMatch(output, /class="rich-box|class="rich-writing-block|class="rich-badge"/);
    assert.doesNotMatch(output, /<script|onClick=/);
  }
});

test("inline fenced examples cannot grant control or command authority", () => {
  const input = '`<SH>echo NEVER</SH>`\n\n~~~mdx\n<box><icon name="check"/></box>\n~~~';
  const output = html(input);
  assert.doesNotMatch(output, /class="rich-box/);
  assert.match(output, /&lt;SH&gt;/);
});

test("unsupported DIL loop syntax cannot execute or expand", () => {
  const input = '<box>\n{#each [{label:"Alice"}] as item}\n<text>{item.label}</text>\n{/each}\n</box>';
  const document = canonicalDocument(input);
  assert.equal(document.valid, true);
  const output = html(input, { document });
  assert.doesNotMatch(output, />Alice</);
  assert.match(output, /item\.label/);
});

test("SVG event attributes and external resources fail closed", () => {
  for (const input of [
    '<svg viewBox="0 0 100 100"><rect width="10" height="10" onload="alert(1)"/></svg>',
    '<svg viewBox="0 0 100 100"><image href="https://example.com/x.svg"/></svg>',
    '<svg viewBox="0 0 100 100"><path d="M0 0 L10 10" stroke="url(#evil)"/></svg>',
  ]) {
    const document = canonicalDocument(input);
    assert.equal(document.valid, false);
    assert.doesNotMatch(html(input, { document }), /<svg/);
  }
});

test("literal HTML and prototype-like renderer names cannot select host components", () => {
  for (const input of [
    '<constructor>unsafe</constructor>',
    '<__proto__>unsafe</__proto__>',
    '<text onMouseOver="alert(1)">unsafe</text>',
  ]) {
    const output = html(input);
    assert.doesNotMatch(output, /onMouseOver=|<constructor|class="rich-text"/);
  }
});

test("incomplete rich markup never crashes the renderer", () => {
  const output = html('<box><row>broken');
  assert.match(output, /broken/);
  assert.doesNotMatch(output, /class="rich-box/);
});
