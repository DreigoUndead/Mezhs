// Run after the frontend npm dependencies are restored by the Agent Web build.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { createRequire } = require("node:module");
const { test } = require("node:test");

const root = path.resolve(__dirname, "..");
const frontendRequire = createRequire(path.join(root, "src/Mezhs.Agent.Web/package.json"));
const ts = frontendRequire("typescript");
const { renderToStaticMarkup } = frontendRequire("react-dom/server");
const sourceDir = path.join(root, "src/Mezhs.Web.Lib/src");
const cache = new Map();

function load(file) {
  if (cache.has(file)) return cache.get(file).exports;
  const source = fs.readFileSync(path.join(sourceDir, file + ".tsx"), "utf8");
  const output = ts.transpileModule(source, { compilerOptions: {
    module: ts.ModuleKind.CommonJS,
    target: ts.ScriptTarget.ES2020,
    jsx: ts.JsxEmit.ReactJSX,
  } }).outputText;
  const module = { exports: {} };
  cache.set(file, module);
  const localRequire = (id) => {
    if (id.endsWith(".css")) return {};
    if (id.startsWith("./")) return load(id.slice(2));
    return frontendRequire(id);
  };
  new Function("require", "module", "exports", output)(localRequire, module, module.exports);
  return module.exports;
}

const React = frontendRequire("react");
const { MarkdownContent } = load("MarkdownContent");
const html = (value) => renderToStaticMarkup(React.createElement(MarkdownContent, { content: value }));

test("standard GFM table with alignment and escaped pipes", () => {
  const rendered = html("| Name | Result |\n| :--- | ---: |\n| A \\| B | **PASS** |");
  assert.match(rendered, /<table>/);
  assert.match(rendered, /<th[^>]*text-align:left/);
  assert.match(rendered, /<th[^>]*text-align:right/);
  assert.match(rendered, /A \| B/);
  assert.match(rendered, /<strong>PASS<\/strong>/);
  assert.doesNotMatch(rendered, /\| :---/);
});

test("nested static layout, styles, badges, icons and link components", () => {
  const rendered = html('<box gap={1}><row align="center" gap={2}><icon name="check-circle" color="success"/> **Done**</row><row justify="between"><text color="secondary">Main</text><badge color="success">0 ahead</badge></row></box>\n<Link url="https://github.com/example/repo/pull/1" title="PR #1"/>');
  assert.match(rendered, /class="rich-box"/);
  assert.match(rendered, /class="rich-row"/);
  assert.match(rendered, /class="rich-badge rich-success"/);
  assert.match(rendered, /<strong>Done<\/strong>/);
  assert.match(rendered, /href="https:\/\/github.com\/example\/repo\/pull\/1"/);
  assert.match(rendered, /PR #1/);
  assert.doesNotMatch(rendered, /&lt;row|&lt;box|&lt;Link/);
});

test("fenced and inline code preserve literal presentation tags", () => {
  const rendered = html("Here is \`<row>literal</row>\`:\n\n\`\`\`html\n<box>not a layout</box>\n\`\`\`");
  assert.match(rendered, /<code>&lt;row&gt;literal&lt;\/row&gt;<\/code>/);
  assert.match(rendered, /&lt;box&gt;not a layout&lt;\/box&gt;/);
  assert.doesNotMatch(rendered, /class="rich-box"/);
});

test("unknown HTML and dangerous URLs never execute", () => {
  const rendered = html('<script>alert(1)</script><Link url="javascript:alert(1)" title="Bad"/><row onClick={evil()}>Safe</row>');
  assert.match(rendered, /&lt;script&gt;/);
  assert.doesNotMatch(rendered, /href="javascript:/);
  assert.doesNotMatch(rendered, /onclick=/i);
  assert.match(rendered, /Safe/);
});

test("legacy Markdown and visible literal escapes still work", () => {
  const rendered = html("# Heading\n\n- *first*\n- second\n\n<escape><DONE></escape>");
  assert.match(rendered, /<h1>Heading<\/h1>/);
  assert.match(rendered, /<em>first<\/em>/);
  assert.match(rendered, /&lt;DONE&gt;/);
});
