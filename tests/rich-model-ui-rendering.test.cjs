const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const Module = require("node:module");
const { performance } = require("node:perf_hooks");
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
// Exercise the production extractor directly: a full Markdown render would hide
// scanner regressions behind unrelated Markdown/React parsing work.
const extractorBundle = esbuild.buildSync({
  entryPoints: [path.join(frontend, "node_modules/@mezhs/web-lib/src/RichQuestion.tsx")],
  preserveSymlinks: true, bundle: true, platform: "node", format: "cjs",
  jsx: "automatic", write: false, external: ["react", "react/jsx-runtime", "react-dom"],
}).outputFiles[0].text;
const extractorModule = new Module(path.join(frontend, "rich-question-extraction-tests.cjs"));
extractorModule.filename = path.join(frontend, "rich-question-extraction-tests.cjs");
extractorModule.paths = Module._nodeModulePaths(frontend);
extractorModule._compile(extractorBundle, extractorModule.filename);
const { extractRichQuestions } = extractorModule.exports;

const codeDirective = '{@body const [choice,setChoice] = DIL.useState("B")}';
const questionControls = question.slice(question.indexOf("<radio-group"));

for (const [name, example] of [
  ["four-space indentation", "    " + question.trim().replace(/\n/g, "\n    ")],
  ["tab indentation", "\t" + question.trim().replace(/\n/g, "\n\t")],
  ["blockquote fence", "> ~~~mdx\n> " + question.trim().replace(/\n/g, "\n> ") + "\n> ~~~"],
  ["list-nested fence", "- Example:\n\n  ~~~mdx\n" + question.trim().replace(/\n/g, "\n  ") + "\n  ~~~"],
]) {
  test("full Markdown code sample stays literal: " + name, () => {
    const parsed = extractRichQuestions(example);
    if (name === "list-nested fence" && Object.keys(parsed.questions).length) {
      const { pathToFileURL } = require("node:url");
      const { fromMarkdown } = await import(pathToFileURL(requireFrontend.resolve("mdast-util-from-markdown")).href);
      const stack = [fromMarkdown(example)];
      const spans = [];
      while (stack.length) {
        const node = stack.pop();
        if (node.type === "code" || node.type === "inlineCode") spans.push({ type: node.type, position: node.position });
        if (node.children) stack.push(...node.children);
      }
      const at = example.indexOf("{@body");
      console.log("DEBUG_NESTED", JSON.stringify({ at, chars: example.slice(at - 60, at + 60), spans, extracted: Object.keys(parsed.questions) }));
    }
    assert.deepEqual(Object.keys(parsed.questions), []);
    assert.equal(parsed.content, example);
    const rendered = html(example, { onIssueNewTurn() { throw Error("Code example must remain inert"); } });
    assert.doesNotMatch(rendered, /<fieldset class="rich-question"/);
    assert.match(rendered, /DIL\.useState/);
    assert.match(rendered, /<code\b/);
  });
}

for (const [name, example] of [
  ["indented declaration", "Example:\n\n    " + codeDirective + "\n\n" + questionControls],
  ["tab-indented declaration", "Example:\n\n\t" + codeDirective + "\n\n" + questionControls],
  ["blockquote fenced declaration", "Example:\n\n> ~~~mdx\n> " + codeDirective + "\n> ~~~\n\n" + questionControls],
  ["list-nested fenced declaration", "- Example:\n\n  ~~~mdx\n  " + codeDirective + "\n  ~~~\n\n" + questionControls],
]) {
  test("code-only declaration cannot authorize external controls: " + name, () => {
    const parsed = extractRichQuestions(example);
    assert.deepEqual(Object.keys(parsed.questions), []);
    assert.equal(parsed.content, example);
    const rendered = html(example, { onIssueNewTurn() { throw Error("Code example must remain inert"); } });
    assert.doesNotMatch(rendered, /<fieldset class="rich-question"/);
    assert.match(rendered, /DIL\.useState/);
    assert.match(rendered, /radio-group/);
  });
}

test("a valid DIL question following indented example still works", () => {
  const source = "    " + codeDirective + "\n\n" + question;
  const parsed = extractRichQuestions(source);
  assert.equal(Object.keys(parsed.questions).length, 1);
  assert.ok(parsed.content.startsWith("    " + codeDirective));
  const rendered = html(source, { onIssueNewTurn() {} });
  assert.equal((rendered.match(/<fieldset class="rich-question"/g) || []).length, 1);
  assert.match(rendered, /<code\b/);
});


test("thousands of inline code spans do not slow question extraction quadratically", { timeout: 10000 }, () => {
  const manySpans = "`x` ".repeat(25_000);
  const started = performance.now();
  const parsed = extractRichQuestions(manySpans + "\n\n" + question);
  const elapsed = performance.now() - started;
  assert.equal(Object.keys(parsed.questions).length, 1);
  assert.equal(Object.keys(parsed.slotOffsets).length, 1);
  assert.ok(parsed.content.startsWith(manySpans));
  assert.ok(elapsed < 5000, `Shielding 25,000 spans took ${elapsed.toFixed(0)} ms`);
});

test("many protected and unsupported body directives do not gain action authority", () => {
  const inline = '`{@body const [choice,setChoice] = DIL.useState("B")}`\n'.repeat(500);
  const invalid = "{@body const ignored = alert(1)}\n".repeat(1000);
  const parsed = extractRichQuestions(inline + invalid + "\n\n" + question);
  assert.equal(Object.keys(parsed.questions).length, 1);
  assert.equal(Object.keys(parsed.slotOffsets).length, 1);
  assert.ok(parsed.content.startsWith(inline));
  assert.ok(parsed.content.includes("\\{@body const ignored = alert(1)}"));
  assert.match(html(inline.split("\n").slice(0, 4).join("\n") + "\n\n" + question), /<fieldset class="rich-question"/);
});

test("many unmatched unequal backtick runs cannot cause repeated rest-of-input searches", { timeout: 10000 }, () => {
  const unmatched = Array.from({ length: 350 }, (_, i) => "x" + "`".repeat(i + 1) + "x").join("\n");
  const started = performance.now();
  const parsed = extractRichQuestions(unmatched + "\n\n{@body invalid}");
  const elapsed = performance.now() - started;
  assert.equal(Object.keys(parsed.questions).length, 0);
  assert.ok(elapsed < 5000, `Shielding unmatched backticks took ${elapsed.toFixed(0)} ms`);
});

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
  ["deeply nested", '<svg viewBox="0 0 10 10">' + "<g>".repeat(14) + "<rect width='2' height='2'/>" + "</g>".repeat(14) + '</svg>'],
  ["too many nodes", '<svg viewBox="0 0 10 10">' + '<rect width="1" height="1"/>'.repeat(251) + '</svg>'],
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

test("model-supplied internal slots cannot impersonate extracted question controls", () => {
  for (const source of [
    '<mezhs-question-slot id="0" />\n\n' + question,
    '{@body const bogus = alert(1)}\n\n' + question + '\n\n<mezhs-question-slot id="0" />',
  ]) {
    const output = html(source, { onIssueNewTurn() { throw Error("Must not dispatch during render"); } });
    assert.equal((output.match(/<fieldset class="rich-question"/g) || []).length, 1);
    assert.match(output, /&lt;mezhs-question-slot id=&quot;0&quot;/);
  }
});

test("non-closing fence lines keep DIL examples inert", () => {
  for (const marker of ["```not-a-closer", "~~~not-a-closer"]) {
    const fence = marker.startsWith("`") ? "```" : "~~~";
    const source = fence + "mdx\n" + marker + "\n" + question + "\n" + fence;
    const output = html(source, { onIssueNewTurn() { throw Error("Fenced code must not dispatch"); } });
    assert.doesNotMatch(output, /<fieldset class="rich-question"/);
    assert.match(output, /&lt;radio-group/);
  }
});

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
    question.replace("{@body", "\\{@body"),
    question.replace("<radio-group", "\\<radio-group"),
    question.replace("<button", "\\<button"),
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

test("nearby onClick text never activates an inline-code DIL declaration", () => {
  const decoy = "`onClick={x}`\n\n`{@body const [choice,setChoice] = DIL.useState(\"B\")}`\n\n" +
    question.slice(question.indexOf("<radio-group"));
  const parsed = extractRichQuestions(decoy);
  assert.equal(Object.keys(parsed.questions).length, 0);
  assert.equal(Object.keys(parsed.slotOffsets).length, 0);
  const output = html(decoy, { onIssueNewTurn() { throw Error("Inline code must stay inert"); } });
  assert.doesNotMatch(output, /<fieldset class="rich-question"/);
  assert.match(output, /<code>\{@body const \[choice,setChoice\]/);
});

test("a valid question following inline handler examples is still recognized", () => {
  const source = "`onClick={x}`\n\n" + question;
  const parsed = extractRichQuestions(source);
  assert.equal(Object.keys(parsed.questions).length, 1);
  const output = html(source, { onIssueNewTurn() {} });
  assert.equal((output.match(/<fieldset class="rich-question"/g) || []).length, 1);
});
