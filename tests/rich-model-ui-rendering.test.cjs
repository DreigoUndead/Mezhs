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
const question = fs.readFileSync(path.join(__dirname, "fixtures/rich-model-ui/representative-question.md"), "utf8").replace(/\r\n?/g, "\n");
const customQuestion = fs.readFileSync(path.join(__dirname, "fixtures/rich-model-ui/custom-controls-question.md"), "utf8").replace(/\r\n?/g, "\n");

test("CodeBlock preserves literal source inside a nested box", () => {
  const source = [
    '<box border radius="lg" padding={3} gap={2}>',
    '  <CodeBlock language="typescript" editable>',
    'const broken = { unexpected: ;',
    '<SH>',
    'echo NEVER_EXECUTE',
    '</SH>',
    '  </CodeBlock>',
    '  <text color="secondary">Nested box content</text>',
    '</box>',
  ].join("\n");
  const output = html(source, { onIssueNewTurn() { throw Error("Code may not dispatch"); } });
  assert.match(output, /class="rich-box rich-bordered"/);
  assert.match(output, /class="rich-code-block"/);
  assert.match(output, /aria-label="Edit code block"/);
  assert.match(output, /&lt;SH&gt;/);
  assert.match(output, /Nested box content/);
  assert.doesNotMatch(output, /&lt;box border radius/);
  assert.doesNotMatch(output, /<fieldset class="rich-question"/);
});

test("CodeBlock read-only rendering escapes JSX and malformed template source", () => {
  const source = [
    '<CodeBlock language="tsx">',
    'const literal = "<script>doBadThings()</script>";',
    '{@body const [value,setValue] = DIL.useState("A")',
    '</CodeBlock>',
  ].join("\n");
  const output = html(source, { onIssueNewTurn() { throw Error("Code may not dispatch"); } });
  assert.match(output, /class="rich-code-block"/);
  assert.match(output, /<pre><code>/);
  assert.match(output, /&lt;script&gt;/);
  assert.doesNotMatch(output, /<script|<textarea/);
});

test("CodeBlock in Markdown fences and invalid attributes cannot activate", () => {
  const sample = '<CodeBlock language="typescript">\nconst x = 1;\n</CodeBlock>';
  const fenced = "~~~mdx\n" + sample + "\n~~~";
  assert.doesNotMatch(html(fenced), /class="rich-code-block"/);
  assert.match(html(fenced), /&lt;CodeBlock/);
  const rejected = '<CodeBlock language="typescript" onClick="attack">\nconst x = 1;\n</CodeBlock>';
  assert.doesNotMatch(html(rejected), /class="rich-code-block"/);
  const duplicate = '<mezhs-code-slot id="c0" />\n\n' + sample;
  assert.doesNotMatch(html(duplicate), /class="rich-code-block"/);
});

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



test("mixed LF/CRLF list-fenced code cannot authorize a DIL question", () => {
  const sample = "- Example:\n\n  ~~~mdx\n  " +
    question.trim().replace(/\n/g, "\r\n  ") + "\n  ~~~";
  const parsed = extractRichQuestions(sample);
  assert.deepEqual(Object.keys(parsed.questions), []);
  assert.equal(parsed.content, sample);
  const rendered = html(sample, { onIssueNewTurn() { throw Error("Mixed-newline code cannot dispatch"); } });
  assert.doesNotMatch(rendered, /<fieldset class="rich-question"/);
  assert.match(rendered, /DIL\.useState/);
});

test("CRLF Markdown code blocks remain literal", () => {
  for (const source of [
    "    " + codeDirective + "\n\n" + questionControls,
    "> ~~~mdx\n> " + question.trim().replace(/\n/g, "\n> ") + "\n> ~~~",
    "- Example:\n\n  ~~~mdx\n" + question.trim().replace(/\n/g, "\n  ") + "\n  ~~~",
  ]) {
    const windowsText = source.replace(/\n/g, "\r\n");
    const parsed = extractRichQuestions(windowsText);
    assert.equal(Object.keys(parsed.questions).length, 0);
    assert.equal(parsed.content, windowsText);
    assert.doesNotMatch(html(windowsText, { onIssueNewTurn() {} }), /<fieldset class="rich-question"/);
  }
});

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


// Actual stored model reply, sanitized without reconstructing its Markdown/MDX.
const actualTelemetryPath = path.join(__dirname,
  "fixtures/rich-model-ui/actual-telemetry-2026-11-05-sanitized.md");
const actualTelemetry = fs.readFileSync(actualTelemetryPath, "utf8");

test("actual sanitized telemetry reply preserves nested log/trace/metric card UI", () => {
  const output = html(actualTelemetry);
  assert.match(output, /class="rich-box/);
  assert.match(output, /class="rich-row/);
  assert.match(output, /aria-label="scroll-text"/);
  assert.match(output, /aria-label="chart-no-axes-combined"/);
  assert.match(output, /aria-label="git-branch"/);
  assert.match(output, /<span class="rich-icon" role="img" aria-label="scroll-text"><svg/);
  assert.match(output, /<span class="rich-icon" role="img" aria-label="chart-no-axes-combined"><svg/);
  assert.doesNotMatch(output, /&lt;box gap=|&lt;row align=|&lt;\/box&gt;/);
});

test("actual sanitized telemetry reply expands and renders native 340-by-236 SVG", () => {
  const output = html(actualTelemetry);
  assert.match(output, /class="rich-svg"/);
  assert.match(output, /viewBox="0 0 340 236"/);
  assert.match(output, /<rect\b/);
  assert.match(output, /<path\b/);
  assert.match(output, /<text\b/);
  assert.doesNotMatch(output, /&lt;svg viewBox/);
  assert.doesNotMatch(output, /&lt;Cite refs=/);
  assert.match(output, /class="rich-citation-unavailable"/);
  assert.match(output, /OpenTelemetry/);
});

test("actual sanitized telemetry reply offers A-default three-choice question", () => {
  const richExpressionBundle = esbuild.buildSync({
    entryPoints: [path.join(frontend, "node_modules/@mezhs/web-lib/src/RichExpression.ts")],
    preserveSymlinks: true, bundle: true, platform: "node", format: "cjs", write: false,
  }).outputFiles[0].text;
  const expressionModule = new Module(path.join(frontend, "rich-expression-tests.cjs"));
  expressionModule.filename = path.join(frontend, "rich-expression-tests.cjs");
  expressionModule.paths = Module._nodeModulePaths(frontend);
  expressionModule._compile(richExpressionBundle, expressionModule.filename);
  const prepared = extractRichQuestions(expressionModule.exports.expandRichTemplates(actualTelemetry));
  assert.equal(Object.keys(prepared.questions).length, 1);
  const data = Object.values(prepared.questions)[0];
  assert.equal(data.initial, "A");
  assert.deepEqual(data.options.map(option => option.value), ["A", "B", "C"]);
  assert.equal(data.block, true);
  const output = html(actualTelemetry, { onIssueNewTurn() {
    throw Error("Rendering must not submit");
  } });
  assert.equal((output.match(/<fieldset class="rich-question"/g) || []).length, 1);
  assert.match(output, /checked="" value="A"/);
  assert.match(output, /rich-question-block/);
  assert.match(output, /Confirm and continue/);
  assert.doesNotMatch(output, /<button[^>]*disabled/);
  const readOnly = html(actualTelemetry);
  assert.match(readOnly, /Submission unavailable in this view/);
});

test("standalone literal citation arrays render safe unavailable indicators", () => {
  for (const sample of [
    '<Cite refs={["turn123search3"]}/>',
    '<Cite refs={["turn123search3","turn123news10"]}/>',
  ]) {
    // This path has no unrelated JSX template to trigger RichExpression.
    const output = html("Before " + sample + " after");
    assert.match(output, /class="rich-citation-unavailable"/);
    assert.doesNotMatch(output, /&lt;Cite|<a\b|<script\b/i);
    assert.match(output, /Before/);
    assert.match(output, /after/);
  }
});

test("invalid citation references never create navigation or executable markup", () => {
  for (const sample of [
    '<Cite ref="javascript:alert(1)"/>',
    '<Cite refs={["turn123search1",globalThis.alert(1)]}/>',
    '<Cite refs={["turn123search1", "https://example.com"]}/>',
    '<Cite refs={["turn123search1"]} onClick={alert(1)}/>',
  ]) {
    const output = html("Before " + sample + " after");
    assert.doesNotMatch(output, /<(?:a|button|script)\b|<[^>]+\sonclick\s*=/i);
    // Literal source text remains safely escaped rather than becoming DOM attributes.
    assert.match(output, /Before/);
    assert.match(output, /after/);
  }
});

test("multiline DIL declarations remain actionable only outside Markdown code", () => {
  for (const newline of ["\n", "\r\n"]) {
    const multiline = question.replace("{@body const ", "{@body\n  const ").replace(/\n/g, newline);
    const parsed = extractRichQuestions(multiline);
    assert.equal(Object.keys(parsed.questions).length, 1, "Valid multiline declaration not recognized");
    const rendered = html(multiline, { onIssueNewTurn() {} });
    assert.match(rendered, /<fieldset class="rich-question"/);
    assert.match(rendered, /checked="" value="B"/);
    assert.doesNotMatch(rendered, /\{@body/);

    const fenced = "~~~mdx" + newline + multiline + newline + "~~~";
    const indented = multiline.trim().replace(/^/gm, "    ");
    const quoted = "> ~~~mdx" + newline +
      "> " + multiline.trim().replace(/\r?\n/g, newline + "> ") +
      newline + "> ~~~";
    const escaped = multiline.replace("{@body", "\\{@body");
    for (const [name, source] of [
      ["fenced", fenced],
      ["indented", indented],
      ["blockquote fenced", quoted],
      ["escaped", escaped],
    ]) {
      const extracted = extractRichQuestions(source);
      assert.equal(Object.keys(extracted.questions).length, 0,
        name + " multiline code must stay inert");
      assert.doesNotMatch(html(source, { onIssueNewTurn() {
        throw Error("Inert multiline code must not send");
      }}), /<fieldset class="rich-question"/, name + " unexpectedly mounted a question");
    }
  }
});

test("multiline body rejects additional executable declarations", () => {
  const unsupported = question.replace("{@body const ", "{@body\n  alert(1);\n  const ");
  assert.equal(Object.keys(extractRichQuestions(unsupported).questions).length, 0);
  assert.doesNotMatch(html(unsupported, { onIssueNewTurn() {} }),
    /<fieldset class="rich-question"/);
});

test("custom rich controls parse all four choice messages without evaluating JS", () => {
  const parsed = extractRichQuestions(customQuestion);
  assert.equal(Object.keys(parsed.questions).length, 1, "Expected one validated custom question");
  const data = parsed.questions["0"];
  assert.equal(data.initial, "A");
  assert.deepEqual(data.options.map(option => option.value), ["A", "B", "C", "D"]);
  assert.match(data.options[0].label, /<text size="sm" color="secondary">/);
  assert.match(data.button, /<icon name="arrow-right" inline\/>/);
  for (const [choice, text] of Object.entries({
    A: "Grill Me Q1: A — minimum interval between request starts.",
    B: "Grill Me Q1: B — cooldown after each completed request.",
    C: "Grill Me Q1: C — delay only after rate limiting.",
    D: "Grill Me Q1: D — I want different semantics.",
  })) assert.equal(data.messages[choice], text);
});

test("custom rich controls render allowed label text, arrow icon and inert read-only button", () => {
  const rendered = html(customQuestion);
  assert.equal((rendered.match(/type="radio"/g) || []).length, 4);
  assert.match(rendered, /Minimum interval between request starts/);
  assert.match(rendered, /rich-tone-secondary/);
  assert.match(rendered, /aria-label="arrow-right"/);
  assert.match(rendered, /Submission unavailable in this view/);
  assert.match(rendered, /<button[^>]*disabled/);
});

test("unsupported content nested in custom controls rejects action authority", () => {
  for (const edited of [
    customQuestion.replace('<text size="sm"', '<text onClick={alert(1)} size="sm"'),
    customQuestion.replace('<icon name="arrow-right" inline/>', '<icon name="arrow-right" onClick={alert(1)}/>'),
    customQuestion.replace('choice==="A"', 'globalThis.alert(1)==="A"'),
    customQuestion.replace('choice==="A"', 'choice["constructor"]==="A"'),
  ]) {
    assert.equal(Object.keys(extractRichQuestions(edited).questions).length, 0);
    assert.doesNotMatch(html(edited), /<fieldset class="rich-question"/);
  }
});
