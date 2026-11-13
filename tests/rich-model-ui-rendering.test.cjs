"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const Module = require("node:module");
const { performance } = require("node:perf_hooks");
const { test } = require("node:test");
const { canonicalDocument, canonicalDocuments } = require("./helpers/canonical-rich-document.cjs");

const root = path.resolve(__dirname, "..");
const frontend = path.join(root, "src/Mezhs.Agent.Web");
const requireFrontend = Module.createRequire(path.join(frontend, "package.json"));
const esbuild = requireFrontend("esbuild");
const React = requireFrontend("react");
const { renderToStaticMarkup } = requireFrontend("react-dom/server");
const output = esbuild.buildSync({
  entryPoints: [path.join(frontend, "node_modules/@mezhs/web-lib/src/MarkdownContent.tsx")],
  preserveSymlinks: true, bundle: true, platform: "node", format: "cjs",
  jsx: "automatic", write: false, loader: { ".css": "text" },
  external: ["react", "react/jsx-runtime", "react-dom"],
}).outputFiles[0].text;
const moduleFile = path.join(frontend, "rich-model-ui-tests.cjs");
const compiled = new Module(moduleFile);
compiled.filename = moduleFile;
compiled.paths = Module._nodeModulePaths(frontend);
compiled._compile(output, moduleFile);
const fixtures = name => fs.readFileSync(path.join(root, "tests/fixtures/rich-model-ui", name), "utf8");
const svg = fixtures("representative-svg.md");
const question = fixtures("representative-question.md");
const customQuestion = fixtures("custom-controls-question.md");
const actualTelemetry = fixtures("actual-telemetry-2026-11-05-sanitized.md");

function html(content, props = {}) {
  return renderToStaticMarkup(React.createElement(compiled.exports.MarkdownContent, {
    content, ...props,
    document: props.document === undefined ? canonicalDocument(content) : props.document,
  }));
}
const kinds = (nodes, kind) => nodes.flatMap(node =>
  [...(node.kind === kind ? [node] : []), ...kinds(node.children || [], kind)]);

test("the C# parser supplies rich UI nodes, not executable model-authored JSX", () => {
  const input = '<box border radius="lg"><text color="secondary">Label</text></box>';
  const parsed = canonicalDocument(input);
  assert.equal(parsed.version, 1);
  assert.equal(parsed.valid, true);
  assert.equal(kinds(parsed.nodes, "element").length, 2);
  assert.match(html(input, { document: parsed }), /class="rich-box/);
  assert.match(html(input, { document: parsed }), /Label/);
});

test("MarkdownContent does not depend on deleted frontend grammar or extractors", () => {
  const source = fs.readFileSync(path.join(root, "src/Mezhs.Web.Lib/src/MarkdownContent.tsx"), "utf8");
  assert.match(source, /RichDocumentView/);
  for (const legacy of ["RichMarkup", "RichExpression", "RichQuestion", "RichRawBlocks", "RichMarkdownProtection"])
    assert.doesNotMatch(source, new RegExp(legacy));
});

test("raw editors keep shell-shaped bodies literal, within layout containers", () => {
  const input = [
    '<box border radius="lg">',
    '<WritingBlock variant="document">',
    '~~~md',
    '<SH>',
    'echo NEVER_EXECUTE',
    '</SH>',
    '~~~',
    '</WritingBlock>',
    '<CodeBlock language="typescript" editable>',
    'const x = "<SH>INERT</SH>";',
    '</CodeBlock>',
    '<text>Sibling</text>',
    '</box>',
  ].join("\n");
  const doc = canonicalDocument(input);
  assert.equal(doc.valid, true);
  const output = html(input, { document: doc });
  assert.match(output, /rich-writing-block/);
  assert.match(output, /rich-code-block/);
  assert.match(output, /echo NEVER_EXECUTE/);
  assert.match(output, /Sibling/);
});

test("standalone raw openers inside fenced Markdown remain code, not editor UI", () => {
  const source = '~~~mdx\n<CodeBlock language="js">\nunsafe()\n</CodeBlock>\n~~~';
  assert.equal(kinds(canonicalDocument(source).nodes, "raw").length, 0);
  assert.doesNotMatch(html(source), /class="rich-code-block"/);
});

test("escaped and commented tags are not converted to rich nodes", () => {
  const inputs = [
    '`<box><text>Inline</text></box>`',
    '<!-- <box><text>Comment</text></box> -->',
    '~~~mdx\n<box><text>Fenced</text></box>\n~~~',
  ];
  for (const [input, doc] of inputs.map((s,i)=>[s,canonicalDocuments(inputs)[i]])) {
    assert.equal(kinds(doc.nodes, "element").length, 0, input);
    assert.doesNotMatch(html(input, { document: doc }), /class="rich-box/);
  }
});

test("Markdown bullets and tables remain typed, without a second React Markdown parser", () => {
  const input = '- **One**\n- Two\n\n| A | B |\n|---|---|\n| 1 | 2 |';
  const doc = canonicalDocument(input);
  assert.ok(kinds(doc.nodes, "list").length > 0);
  assert.ok(kinds(doc.nodes, "table").length > 0);
  assert.match(html(input, { document: doc }), /<table/);
  assert.match(html(input, { document: doc }), /<ul/);
});

test("valid bounded SVG is mounted as native accessible geometry", () => {
  const doc = canonicalDocument(svg);
  assert.equal(doc.valid, true);
  assert.equal(kinds(doc.nodes, "element").filter(n=>n.name==="svg").length, 1);
  const result = html(svg, { document: doc });
  assert.match(result, /<svg/);
  assert.match(result, /<rect/);
  assert.match(result, /<path/);
  assert.match(result, /Application/);
  assert.match(result, /Collector/);
});

test("SVG text nodes are not interpreted as outer rich text", () => {
  const input = '<svg viewBox="0 0 100 80"><text x="4" y="6">Literal text</text></svg>';
  const parsed = canonicalDocument(input);
  assert.equal(parsed.valid, true);
  assert.match(html(input, { document: parsed }), /<text/);
});

test("SVG script-capable subtrees reject the entire document", () => {
  const cases = [
    '<svg viewBox="0 0 20 20"><script>execute()</script></svg>',
    '<svg viewBox="0 0 20 20"><image href="https://evil.example/a.svg"/></svg>',
    '<svg viewBox="0 0 20 20"><foreignObject>evil</foreignObject></svg>',
    '<svg viewBox="0 0 20 20" onload="alert(1)"><rect width="2" height="3"/></svg>',
    '<svg viewBox="0 0 20 20"><rect fill="url(#x)" width="2" height="3"/></svg>',
    '<svg viewBox="0 0 20 20"><rect style="display:none" width="2" height="3"/></svg>',
    '<svg viewBox="0 0 20 20"><use href="#anything"/></svg>',
  ];
  for (const [input, doc] of cases.map((s,i)=>[s,canonicalDocuments(cases)[i]])) {
    assert.equal(doc.valid, false, input);
    assert.doesNotMatch(html(input, { document: doc }), /<svg/);
  }
});

test("SVG numeric resource bounds reject pathological values", () => {
  const input = '<svg viewBox="0 0 400000000 300"><rect x="1" y="1" width="20" height="20"/></svg>';
  assert.equal(canonicalDocument(input).valid, false);
});

test("SVG nested inside layout is still validated by the C# authority", () => {
  const input = '<box border>\n<svg viewBox="0 0 20 20">\n<rect width="10" height="10" />\n</svg>\n</box>';
  const doc = canonicalDocument(input);
  assert.equal(doc.valid, true);
  assert.match(html(input), /<svg/);
});

test("representative radio question lowers to inert precomputed selection data", () => {
  const doc = canonicalDocument(question);
  assert.equal(doc.valid, true);
  const qs = kinds(doc.nodes, "question");
  assert.equal(qs.length, 1);
  assert.equal(qs[0].attributes.initial, "B");
  assert.deepEqual(kinds(qs, "questionOption").map(x=>x.attributes.value), ["A", "B"]);
  const output = html(question, { document: doc });
  assert.match(output, /class="rich-question"/);
  assert.match(output, /disabled=""/);
});

test("host callback is required and never called during SSR", () => {
  let calls=0;
  const output = html(question, { onIssueNewTurn() { calls++; } });
  assert.equal(calls, 0);
  assert.match(output, /class="rich-question"/);
  assert.doesNotMatch(output, /disabled=""/);
});

test("four-answer real fixture contains four literal precomputed messages", () => {
  const doc = canonicalDocument(customQuestion);
  assert.equal(doc.valid, true);
  const qs = kinds(doc.nodes, "question");
  assert.equal(qs.length, 1);
  const options = kinds(qs, "questionOption");
  assert.equal(options.length, 4);
  assert.deepEqual(options.map(x=>x.attributes.value), ["A","B","C","D"]);
  assert.ok(options.every(x=>x.attributes.message.startsWith("Grill Me Q1: ")));
  assert.ok(options[0].attributes.message.includes("minimum interval"));
  assert.match(html(customQuestion, { document: doc }), /Different semantics/);
});

test("invalid question grammar cannot manufacture new-turn callbacks", () => {
  const cases = [
    '{@body const [choice,setChoice] = DIL.useState("A")}\n<button onClick={()=>globalThis.alert(1)}>Bad</button>',
    '{@body const [choice,setChoice] = DIL.useState("A")}\n<radio-group value={choice} onChange={setChoice}><radio value="A">A</radio></radio-group>',
    '{@body const [choice,setChoice] = DIL.useState("A")}\n<radio-group value={choice} onChange={setChoice}>\n  <radio value="A" onClick="alert(1)">A</radio>\n</radio-group>',
  ];
  for (const input of cases) {
    const doc=canonicalDocument(input);
    assert.equal(kinds(doc.nodes,"question").length,0);
    assert.doesNotMatch(html(input,{document:doc}),/class="rich-question"/);
  }
});

test("a fenced question stays inert even with an adjacent enabled host callback", () => {
  const input='~~~mdx\n'+question.trim()+'\n~~~';
  let calls=0;
  assert.equal(kinds(canonicalDocument(input).nodes,"question").length,0);
  assert.doesNotMatch(html(input,{onIssueNewTurn(){calls++;}}),/class="rich-question"/);
  assert.equal(calls,0);
});

test("malformed ancestors reject any previously valid display controls", () => {
  const input='<badge>Safe</badge>\n<box><text>Unclosed</text>';
  const doc=canonicalDocument(input);
  assert.equal(doc.valid,false);
  assert.doesNotMatch(html(input,{document:doc}),/class="rich-badge"/);
});

test("unknown tags and event handlers cannot reach the host renderer", () => {
  const inputs=[
    '<text onClick="alert(1)">Unsafe</text>',
    '<constructor>Unsafe</constructor>',
    '<row><__proto__>Unsafe</__proto__></row>',
    '<Link url="javascript:alert(1)">Unsafe</Link>',
  ];
  for(const input of inputs){
    const out=html(input);
    assert.doesNotMatch(out, /<script\b|<[a-z][^>]*\bonClick=|href="javascript:/i);
  }
});

test("partial unsupported template constructs remain inert, not evaluated", () => {
  const input = '<box>\n{#each [{name:"Unsafe"}] as item}\n<text>{item.name}</text>\n{/each}\n</box>';
  const output = html(input);
  assert.doesNotMatch(output, />Unsafe</);
  assert.match(output,/item\.name/);
});

test("actual sanitized telemetry contains unsupported legacy SVG loops and fails closed", () => {
  const doc=canonicalDocument(actualTelemetry);
  assert.equal(doc.valid,false);
  assert.equal(kinds(doc.nodes,"question").length,0);
  assert.doesNotMatch(html(actualTelemetry,{document:doc}), /class="rich-question"|<svg/);
});

test("citation references are inert display nodes with no arbitrary URL authority", () => {
  const input='<Cite ref="turn0search0"/>';
  const doc=canonicalDocument(input);
  assert.equal(doc.valid,true);
  assert.equal(kinds(doc.nodes,"element").filter(n=>n.name==="Cite").length,1);
  assert.doesNotMatch(html(input,{document:doc}),/href="javascript:/);
});

test("editor bodies may contain HTML, shell delimiters and JSX without activation", () => {
  const input='<CodeBlock language="tsx">\n<div onClick={() => alert(1)}><SH>raw</SH></div>\n</CodeBlock>';
  const doc=canonicalDocument(input);
  assert.equal(doc.valid,true);
  const out=html(input,{document:doc});
  assert.match(out,/rich-code-block/);
  assert.match(out,/&lt;div/);
  assert.doesNotMatch(out,/<div onClick/);
});

test("unsupported executable attributes reject the whole document", () => {
  for(const source of [
    '<row onClick="alert(1)">x</row>',
    '<box gap={eval("bad")}>x</box>',
    '<badge className="surprise">x</badge>',
  ]) {
    assert.equal(canonicalDocument(source).valid,false);
    assert.doesNotMatch(html(source),/class="rich-row|class="rich-box|class="rich-badge"/);
  }
});

test("parsing many protected inline code spans stays bounded", { timeout: 15000 }, () => {
  const source=Array.from({length:500},(_,i)=>'`literal '+i+' <SH>NO</SH>`').join(" ");
  const start=performance.now();
  const doc=canonicalDocument(source);
  assert.equal(doc.valid,true);
  assert.equal(kinds(doc.nodes,"question").length,0);
  assert.ok(performance.now()-start<12000);
});
