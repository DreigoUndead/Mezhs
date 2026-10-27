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

test("nested layout components accept literal values and parse nested Markdown", () => {
  const output = html('<box gap={1}><row align="center" gap={2}><icon name="check-circle" color="success"/> **Done**</row><row justify="between"><text color="secondary">Main</text><badge color="success">0 ahead</badge></row></box>');
  assert.match(output, /class="rich-box"/);
  assert.match(output, /class="rich-row"/);
  assert.match(output, /class="rich-badge rich-success"/);
  assert.match(output, /<strong>Done<\/strong>/);
  assert.doesNotMatch(output, /&lt;row|&lt;box/);
});

test("custom Link markup works inside GFM table cells", () => {
  const output = html('| Pull request | Status |\n|---|---|\n| <Link url="https://github.com/example/repo/pull/21" title="PR #21"/> | Merged |');
  assert.match(output, /<table>/);
  assert.match(output, /href="https:\/\/github.com\/example\/repo\/pull\/21"/);
  assert.match(output, /PR #21/);
  assert.match(output, /<td>Merged<\/td>/);
});

test("inline code and fenced code never become model UI components", () => {
  const output = html("Use \`<row>literal</row>\`.\n\n\`\`\`html\n<box>not a layout</box>\n\`\`\`");
  assert.match(output, /&lt;row&gt;literal&lt;\/row&gt;/);
  assert.match(output, /&lt;box&gt;not a layout&lt;\/box&gt;/);
  assert.doesNotMatch(output, /class="rich-box"/);
});

test("untrusted markup, URLs and expressions never execute", () => {
  const output = html('<script>alert(1)</script><Link url="javascript:alert(1)" title="Bad"/><row gap={globalThis.hack()}>Safe</row>');
  assert.doesNotMatch(output, /href="javascript:/);
  assert.doesNotMatch(output, /<script>/);
  assert.doesNotMatch(output, /onclick=/i);
  assert.match(output, /Safe/);
});

test("incomplete markup falls back without crashing or executing code", () => {
  assert.match(html("<row><badge>Unclosed"), /Unclosed/);
  assert.match(html("<escape><DONE></escape>"), /DONE/);
});
