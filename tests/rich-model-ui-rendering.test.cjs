const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const Module = require("node:module");
const { test } = require("node:test");
const frontend = path.resolve(__dirname, "../src/Mezhs.Agent.Web");
const requireFrontend = Module.createRequire(path.join(frontend, "package.json"));
const esbuild = requireFrontend("esbuild");
const React = requireFrontend("react");
const { renderToStaticMarkup } = requireFrontend("react-dom/server");
const bundle = esbuild.buildSync({
  entryPoints: [path.join(frontend, "node_modules/@mezhs/web-lib/src/MarkdownContent.tsx")],
  preserveSymlinks: true, bundle: true, platform: "node", format: "cjs",
  jsx: "automatic", write: false, loader: { ".css": "text" },
  external: ["react", "react/jsx-runtime", "react-dom"],
}).outputFiles[0].text;
const moduleFile = path.join(frontend, "rich-model-ui-tests.cjs");
const compiled = new Module(moduleFile);
compiled.filename = moduleFile;
compiled.paths = Module._nodeModulePaths(frontend);
compiled._compile(bundle, moduleFile);
const html = (content, props = {}) => renderToStaticMarkup(React.createElement(compiled.exports.MarkdownContent, { content, ...props }));
const svg = fs.readFileSync(path.join(__dirname, "fixtures/rich-model-ui/representative-svg.md"), "utf8");
const question = fs.readFileSync(path.join(__dirname, "fixtures/rich-model-ui/representative-question.md"), "utf8");

test("representative telemetry SVG becomes real accessible selectable drawing", () => {
  const output = html(svg);
  assert.match(output, /<svg\b[^>]*class="rich-svg"/);
  assert.match(output, /aria-label="OpenTelemetry pipeline"/);
  assert.match(output, /viewBox="0 0 400 120"/);
  assert.match(output, /<rect\b/);
  assert.match(output, /<path\b/);
  assert.match(output, /<text[^>]*>Application<\/text>/);
  assert.match(output, /<text[^>]*>Elastic<\/text>/);
  assert.match(output, /End of diagram/);
  assert.doesNotMatch(output, /&lt;svg/);
});

test("SVG text is distinct from rich text", () => {
  const output = html('<svg viewBox="0 0 40 20"><text x="2" y="12">SVG</text></svg>\n\n<text weight="bold">DIL</text>');
  assert.match(output, /<text[^>]*>SVG<\/text>/);
  assert.match(output, /<span[^>]*font-weight:700[^>]*>DIL<\/span>/);
});

for (const [name, sample] of [
  ["script", '<svg viewBox="0 0 10 10"><script>alert(1)</script></svg>'],
  ["event", '<svg viewBox="0 0 10 10" onLoad="alert(1)"><rect width="5" height="5"/></svg>'],
  ["image", '<svg viewBox="0 0 10 10"><image href="https://evil.example/a" /></svg>'],
  ["use", '<svg viewBox="0 0 10 10"><use href="#x"/></svg>'],
  ["foreignObject", '<svg viewBox="0 0 10 10"><foreignObject>HTML</foreignObject></svg>'],
  ["paint URL", '<svg viewBox="0 0 10 10"><rect width="5" height="5" fill="UrL(https://evil.example/a)"/></svg>'],
  ["style", '<svg viewBox="0 0 10 10"><rect width="5" height="5" style="color:red"/></svg>'],
  ["namespace", '<svg viewBox="0 0 10 10" xmlns:xlink="http://x"><rect width="5" height="5"/></svg>'],
  ["large coordinate", '<svg viewBox="0 0 10 10"><rect x="999999999" width="5" height="5"/></svg>'],
  ["large dimension", '<svg viewBox="0 0 10000 99999"><rect width="5" height="5"/></svg>'],
  ["long path", '<svg viewBox="0 0 10 10"><path d="' + "M1 1".repeat(500) + '"/></svg>'],
]) {
  test("unsafe SVG rejects whole subtree: " + name, () => {
    const result = html("Before\n\n" + sample + "\n\nAfter");
    assert.doesNotMatch(result, /class="rich-svg"/);
    assert.match(result, /&lt;svg/);
    assert.match(result, /Before/);
    assert.match(result, /After/);
    assert.doesNotMatch(result, /<script|<image|<foreignObject|onload="/i);
  });
}

test("question defaults to B, read-only without host callback", () => {
  const output = html(question);
  assert.match(output, /<fieldset class="rich-question"/);
  assert.match(output, /checked="" value="B"/);
  assert.match(output, /Two configured exporters/);
  assert.match(output, /<button type="button" disabled=""/);
  assert.match(output, /Submission unavailable in this view/);
  assert.match(output, /Further considerations appear below/);
  assert.doesNotMatch(output, /onClick=|GenUI\.issueNewTurn/);
});

test("host callback enables the question without invoking it during render", () => {
  const output = html(question, { onIssueNewTurn() { throw Error("SSR must never dispatch"); } });
  assert.match(output, /<fieldset class="rich-question"/);
  assert.doesNotMatch(output, /<button[^>]*disabled/);
});

for (const [name, changes] of [
  ["wrong setter", s => s.replace("onChange={setChoice}", "onChange={globalThis}")],
  ["wrong state", s => s.replace("value={choice}", "value={other}")],
  ["extra statement", s => s.replace('DIL.useState("B")', 'DIL.useState("B"); alert(1)')],
  ["unsafe initialization", s => s.replace('DIL.useState("B")', 'globalThis.constructor("x")')],
  ["malicious handler", s => s.replace('GenUI.issueNewTurn(', 'globalThis.alert(')],
  ["computed handler", s => s.replace('GenUI.issueNewTurn(', 'GenUI["issueNewTurn"](')],
  ["extra expression", s => s.replace('Choice: $' + '{choice}', 'Choice: $' + '{choice} $' + '{choice}')],
  ["invalid default", s => s.replace('DIL.useState("B")', 'DIL.useState("Q")')],
  ["malicious label", s => s.replace('One provider, two instances', '<img src=x onerror=alert(1)>')],
]) test("invalid DIL stays inert: " + name, () => {
  const output = html(changes(question), { onIssueNewTurn() { throw Error("must never dispatch"); } });
  assert.doesNotMatch(output, /<fieldset class="rich-question"/);
  assert.doesNotMatch(output, /<button[^>]*onClick/);
  assert.match(output, /radio-group|GenUI|globalThis|useState/);
});

test("Markdown-escaped DIL syntax cannot activate controls", () => {
  const escaped = [
    question.replace("{@body", "\\\\{@body"),
    question.replace("<radio-group", "\\\\<radio-group"),
    question.replace("<button", "\\\\<button"),
  ];
  for (const source of escaped) {
    const output = html(source, { onIssueNewTurn() { throw Error("Escaped syntax must never send"); } });
    assert.doesNotMatch(output, /<fieldset class="rich-question"/);
  }
});

test("fenced and inline code never become active DIL", () => {
  const source = "~~~mdx\n" + question + "\n~~~\n\n" +
    String.fromCharCode(96) + '{@body const [choice,setChoice] = DIL.useState("B")}' + String.fromCharCode(96);
  const output = html(source, { onIssueNewTurn() { throw Error("No send"); } });
  assert.doesNotMatch(output, /<fieldset class="rich-question"/);
  assert.match(output, /&lt;radio-group/);
});

test("unsupported body remains readable and inert", () => {
  const output = html("{@body const x = alert(1)}\n\nText after unsupported syntax");
  assert.doesNotMatch(output, /<fieldset|<script|onClick=/);
  assert.match(output, /Text after unsupported syntax/);
});
