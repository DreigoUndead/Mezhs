const assert = require("node:assert/strict");
const path = require("node:path");
const Module = require("node:module");
const { createRequire } = Module;
const { test } = require("node:test");

const root = path.resolve(__dirname, "..");
const frontend = path.join(root, "src/Mezhs.Agent.Web");
const resolveFrontend = createRequire(path.join(frontend, "package.json"));
const esbuild = resolveFrontend("esbuild");
const React = resolveFrontend("react");
const { renderToStaticMarkup } = resolveFrontend("react-dom/server");

const output = esbuild.buildSync({
  entryPoints: [path.join(frontend, "node_modules/@mezhs/web-lib/src/MarkdownContent.tsx")],
  preserveSymlinks: true,
  bundle: true,
  platform: "node",
  format: "cjs",
  jsx: "automatic",
  write: false,
  loader: { ".css": "text" },
  external: ["react", "react/jsx-runtime", "react-dom"],
}).outputFiles[0].text;

const file = path.join(frontend, "rich-formatting-tests.cjs");
const compiled = new Module(file);
compiled.filename = file;
compiled.paths = Module._nodeModulePaths(frontend);
compiled._compile(output, file);
const html = (value) => renderToStaticMarkup(React.createElement(compiled.exports.MarkdownContent, { content: value }));
const render = (renderedHtml, content = "Fallback") => renderToStaticMarkup(React.createElement(compiled.exports.MarkdownContent, { content, renderedHtml }));

test("GFM tables, cell alignment, and escaped pipes", () => {
  const output = html("| Name | Result |\n| :--- | ---: |\n| A \\| B | **PASS** |");
  assert.match(output, /<table>/);
  assert.match(output, /text-align:left/);
  assert.match(output, /text-align:right/);
  assert.match(output, /A \| B/);
  assert.match(output, /<strong>PASS<\/strong>/);
  assert.doesNotMatch(output, /\| :---/);
});

test("GFM formatting is owned by the library", () => {
  const output = html("# Heading\n\n- ~~Removed~~\n- [x] Done\n\n**Bold** and *italic*");
  assert.match(output, /<h1>Heading<\/h1>/);
  assert.match(output, /<del>Removed<\/del>/);
  assert.match(output, /<input[^>]*checked/);
  assert.match(output, /<strong>Bold<\/strong>/);
  assert.match(output, /<em>italic<\/em>/);
});

test("standard Markdown links, blockquotes and fenced code render normally", () => {
  const output = html(
    "> A **quoted** point\n\n" +
    "[Website](https://example.com)\n\n" +
    "```js\nconst message = '<box>text</box>';\n```"
  );
  assert.match(output, /<blockquote>/);
  assert.match(output, /<strong>quoted<\/strong>/);
  assert.match(output, /href="https:\/\/example\.com"/);
  assert.match(output, /<pre><code/);
  assert.match(output, /&lt;box&gt;text&lt;\/box&gt;/);
});

test("ChatGPT rich markup from an Agent reply renders a real row and icon", () => {
  const output = html('<row align="center" gap={2}><icon name="check-circle" color="success" size="lg"/> **Removal completed successfully.** </row>\n\nThe changes are applied.');
  assert.match(output, /class="rich-row"/);
  assert.match(output, /gap:8px/);
  assert.match(output, /<svg[^>]*class="[^"]*lucide/);
  assert.match(output, /<strong>Removal completed successfully\.<\/strong>/);
  assert.match(output, /The changes are applied/);
  assert.doesNotMatch(output, /&lt;row|&lt;icon/);
});

test("unsupported and executable MDX remains visible, not executable", () => {
  const output = html('<unknown>Keep me</unknown>\n\n<row gap={globalThis.hack()}>Unsafe expression</row>');
  assert.match(output, /Keep me/);
  assert.match(output, /Unsafe expression/);
  assert.doesNotMatch(output, /class="rich-row"|<unknown>|globalThis\.hack\(\)/);
});

test("unsafe links and HTML event handlers are not activated", () => {
  const output = html('[Unsafe](javascript:alert(1))\n\n<img src="x" onerror="alert(1)">');
  assert.doesNotMatch(output, /javascript:alert\(1\)/i);
  assert.match(output, /href="javascript:void\(0\)"/);
  assert.doesNotMatch(output, /<img\b/i);
  assert.match(output, /&lt;img/);
});

test("incomplete custom markup is displayed without crashing", () => {
  const output = html('<row><badge>Unclosed');
  assert.match(output, /Unclosed/);
  assert.doesNotMatch(output, /class="rich-/);
});

test("rendered provider HTML preserves semantic structure", () => {
  const output = render("<section><h3>Rich result</h3><table><tr><th>Key</th><th>Value</th></tr><tr><td>A</td><td>12</td></tr></table><blockquote>Explanation</blockquote></section>");
  assert.match(output, /<h3>Rich result<\/h3>/);
  assert.match(output, /<table>/);
  assert.match(output, /<th>Key<\/th>/);
  assert.match(output, /<blockquote>Explanation<\/blockquote>/);
});

test("provider HTML is sanitized, not executable", () => {
  const output = render('<div onclick="alert(1)"><b>Safe</b><script>alert(2)</script><a href="javascript:alert(3)">Unsafe</a><img src="https://example.com/icon.png" onerror="alert(4)"></div>');
  assert.match(output, /<b>Safe<\/b>/);
  assert.doesNotMatch(output, /onclick=|onerror=|<script>|javascript:alert/i);
  assert.match(output, /<img/);
});

test("plain Markdown is used when no provider HTML exists", () => {
  const output = render(undefined, "## **Markdown fallback**");
  assert.match(output, /<h2><strong>Markdown fallback<\/strong><\/h2>/);
});

test("rendered HTML keeps the provider text accessible as a fallback", () => {
  const output = render("<svg><text>Unsupported chart</text></svg>", "Chart value: 42");
  assert.match(output, /Show original text/);
  assert.match(output, /Chart value: 42/);
});
