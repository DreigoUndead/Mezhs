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
  assert.match(output, /&lt;row gap=\{globalThis\.hack\(\)\}/);
  assert.doesNotMatch(output, /class="rich-row"|<unknown>/);
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










test("APUS trace card renders titles, divider, and compact nested layout structure", () => {
  const input = [
    '<box border radius="lg" padding={3} gap={2}>',
    '  <row justify="between" align="center"><text weight="bold">APUS.Web — GET /</text><badge color="success">18 spans</badge></row>',
    '  <grid columns={3} gap={2}>',
    '    <grid-item><col gap={1}><text size="xs">Request duration</text><title size="xl" tabularNums>120 ms</title></col></grid-item>',
    '    <grid-item><col gap={1}><text size="xs">Trace depth</text><title size="xl" tabularNums>6</title></col></grid-item>',
    '    <grid-item><col gap={1}><text size="xs">Resources</text><title size="xl" tabularNums>1</title></col></grid-item>',
    '  </grid>',
    '  <divider color="subtle"/>',
    '  <text>Selected operations in this request</text>',
    '  <col gap={2}>',
    '    {#each [{name:"GET /",time:"120 ms"},{name:"LoggingMiddleware.InvokeAsync",time:"120 ms"},',
    '     {name:"MVC Home.Index",time:"110 ms"}] as s,i}',
    '      <row key={i} align="center" gap={2}><text>{s.name}</text><text color="secondary" size="xs">{s.time}</text></row>',
    '    {/each}',
    '  </col>',
    '</box>',
  ].join("\n");
  const output = html(input);
  assert.equal((output.match(/class="rich-title"/g) || []).length, 3);
  assert.match(output, /class="rich-title"[^>]*font-size:1\.55em[^>]*font-variant-numeric:tabular-nums/);
  assert.match(output, /<hr class="rich-divider"\/>/);
  assert.match(output, /120 ms/);
  assert.equal((output.match(/class="rich-row"/g) || []).length, 4);
  assert.doesNotMatch(output, /&lt;title|&lt;divider|&lt;row|\{#each/);
});

test("DIL telemetry example expands three entries into a styled grid", () => {
  const sample = [
    '<box border radius="lg" padding={3} gap={2}>',
    '  <box background="surface-secondary" radius="md" padding={3} align="center">**eFlex Application**</box>',
    '  <grid columns={3} gap={2}>',
    '    {#each [{name:"Logs",source:"LogProvider",path:"/v1/logs"},',
    '            {name:"Traces",source:"Initialize.cs",path:"/v1/traces"},',
    '            {name:"Metrics",source:"Initialize.cs",path:"/v1/metrics"}] as signal}',
    '      <grid-item><col align="center" gap={2}>',
    '        <icon name="arrow-down" color="secondary"/>',
    '        <box border radius="md" padding={2} width="100%" align="center" gap={1}>',
    '          <text weight="medium" size="sm">{signal.name}</text>',
    '          <text size="2xs" color="secondary" textAlign="center">{signal.source}</text>',
    '        </box>',
    '        <text>{signal.path}</text>',
    '      </col></grid-item>',
    '    {/each}',
    '  </grid>',
    '</box>',
  ].join("\n");
  const output = html(sample);
  assert.match(output, /class="rich-grid"/);
  assert.match(output, /grid-template-columns:repeat\(3,minmax\(0,1fr\)\)/);
  assert.equal((output.match(/class="rich-grid-item"/g) || []).length, 3);
  assert.match(output, /Logs/);
  assert.match(output, /Traces/);
  assert.match(output, /Metrics/);
  assert.match(output, /\/v1\/logs/);
  assert.match(output, /\/v1\/traces/);
  assert.match(output, /\/v1\/metrics/);
  assert.match(output, /background:#eeebe5/);
  assert.match(output, /<svg/);
  assert.doesNotMatch(output, /\{#each|\{signal\.|&lt;grid/);
});

test("template values cannot inject HTML or execute JavaScript", () => {
  const source = '<grid>{#each [{name:"<img src=x onerror=alert(1)>"}] as signal}<grid-item>{signal.name}</grid-item>{/each}</grid>';
  const output = html(source);
  assert.match(output, /&lt;img/);
  assert.doesNotMatch(output, /<img\b|onerror="/);
  assert.match(output, /class="rich-grid-item"/);
});

test("unsafe loop expressions remain literal instead of being evaluated", () => {
  const source = '<grid>{#each [globalThis.alert("bad")] as signal}<grid-item>{signal}</grid-item>{/each}</grid>';
  const output = html(source);
  assert.doesNotMatch(output, /class="rich-grid-item"/);
  assert.match(output, /globalThis\.alert/);
});

test("nested loops only read own data fields and support literal values", () => {
  const source = [
    '<grid>{#each [{label:"A",children:[{text:"One"},{text:"Two"}]}] as outer}',
    '<grid-item>{outer.label}',
    '{#each [{label:"inside"}] as item}<text>{item.label}</text>{/each}',
    '</grid-item>{/each}</grid>'
  ].join("");
  const output = html(source);
  assert.match(output, /inside/);
  assert.match(output, /A/);
  assert.equal((output.match(/class="rich-grid-item"/g) || []).length, 1);
});

test("fenced DIL examples stay code and do not expand", () => {
  const source = ['~~~dil', '{#each [{name:"Foo"}] as signal}', '{signal.name}', '{/each}', '~~~'].join("\n");
  const output = html(source);
  assert.match(output, /<pre><code/);
  assert.match(output, /#each/);
  assert.match(output, /signal\.name/);
});


test("indexed tracing DIL loops render computed indent, border, and tabular spans", () => {
  const input = [
    "## Tracing fix completed and validated",
    "",
    '<badge color="success">PASS — Connected HTTP tracing</badge>',
    "",
    "The isolated integration test confirms one HTTP trace.",
    "",
    '<box border radius="lg" padding={3} gap={2}>',
    '<text color="secondary" size="xs" weight="medium">Measured integration-test trace</text>',
    '<box gap={2}>',
    '{#each [{name:"HTTP GET /traceprobe",time:"210.85 ms",depth:0},',
    '{name:"HTTP middleware",time:"154.39 ms",depth:1},',
    '{name:"MVC TraceProbe.Get",time:"99.17 ms",depth:2},',
    '{name:"Controller.Get",time:"55.09 ms",depth:3},',
    '{name:"Query (simulated)",time:"23.36 ms",depth:4}] as s,i}',
    '<row key={i} align="center" gap={2}>',
    '<box width={`${s.depth*13}px`}/>',
    '<box flex="1" border={{left:{size:2,color:i===0?"strong":"subtle"}}} padding={{left:2}}>',
    '<row justify="between" align="center">',
    '<text size="sm">{s.name}</text>',
    '<text color="secondary" size="xs" tabularNums>{s.time}</text>',
    '</row></box></row>',
    '{/each}</box></box>',
    "",
    "## Changes made",
  ].join("\n");
  const output = html(input);
  assert.match(output, /<h2>Tracing fix completed and validated<\/h2>/);
  assert.match(output, /class="rich-badge rich-tone-success"/);
  assert.match(output, /PASS — Connected HTTP tracing/);
  for (const value of ["HTTP GET /traceprobe", "HTTP middleware", "MVC TraceProbe.Get",
    "Controller.Get", "Query (simulated)", "210.85 ms", "23.36 ms", "Changes made"])
    assert.ok(output.includes(value), "missing " + value);
  assert.equal((output.match(/class="rich-row"/g) || []).length, 10);
  assert.match(output, /width:52px/);
  assert.match(output, /border-left:2px solid/);
  assert.match(output, /padding-left:8px/);
  assert.match(output, /font-variant-numeric:tabular-nums/);
  assert.doesNotMatch(output, /\{#each|\{s\.name\}|\{s\.time\}|&lt;box|&lt;row/);
});

test("bad rich block cannot disable earlier supported badges", () => {
  const input = '<badge color="success">PASS</badge>\n\n<box border={{left:{size:2,color:evil()}}}>Unsafe fragment';
  const output = html(input);
  assert.match(output, /class="rich-badge rich-tone-success"/);
  assert.match(output, /evil\(\)/);
  assert.doesNotMatch(output, /<script|onerror=/);
});

test("template expression calls and prototype access stay inert", () => {
  const input = [
    '<box>{#each [{name:"ok", depth:1}] as s,i}',
    '<row key={i}><box width={`${s.depth.constructor(200)}px`}/><text>{s.name}</text></row>',
    '{/each}</box>'
  ].join("\n");
  const output = html(input);
  assert.doesNotMatch(output, /width:200px/);
  assert.match(output, /constructor/);
});


test("standalone object-valued layout properties use the restricted evaluator", () => {
  const output = html('<box border={{left:{size:2,color:"subtle"}}} padding={{left:2}}>Standalone</box>');
  assert.match(output, /class="rich-box"/);
  assert.match(output, /border-left:2px solid/);
  assert.match(output, /padding-left:8px/);
  assert.match(output, /Standalone/);
  assert.doesNotMatch(output, /&lt;box/);
});






test("inline code samples do not expand loops or JSX attribute expressions", () => {
  const source = [
    'Example: \`{#each [{name:"Sample"}] as s}{s.name}{/each}\`.',
    '',
    'Example: \`<box padding={{left:2}}>literal</box>\`.',
    '',
    '<badge color="success">Actual badge</badge>',
  ].join("\n");
  const output = html(source);
  assert.match(output, /<code>\{#each \[\{name:&quot;Sample&quot;\}\] as s\}\{s\.name\}\{\/each\}<\/code>/);
  assert.match(output, /<code>&lt;box padding=\{\{left:2\}\}&gt;literal&lt;\/box&gt;<\/code>/);
  assert.match(output, /class="rich-badge rich-tone-success"/);
});
