const test = require("node:test");
const assert = require("node:assert/strict");
const { normalizeProviderReply } = require("../electron/provider-reply");

test("provider HTML retains semantic headings, bold, code, links, lists and tables", () => {
  const result = normalizeProviderReply({
    format: "html", text: "Summary result",
    html: '<h2>Summary</h2><p>A <strong>bold</strong> point and <a href="https://example.com">source</a>.</p>' +
      '<ul><li>First</li><li>Second</li></ul>' +
      '<pre><code>const n = 1;\n</code></pre>' +
      '<table><thead><tr><th>Name</th><th>Result</th></tr></thead>' +
      '<tbody><tr><td>one</td><td>pass</td></tr></tbody></table>'
  });
  assert.match(result.text, /## Summary/);
  assert.match(result.text, /\*\*bold\*\*/);
  assert.match(result.text, /\[source\]\(https:\/\/example\.com\)/);
  assert.match(result.text, /First/);
  assert.match(result.text, /```/);
  assert.match(result.text, /\| Name \| Result \|/);
  assert.equal(Object.prototype.hasOwnProperty.call(result, "html"), false);
});

test("plain text escapes raw Markdown while retaining newlines", () => {
  const result = normalizeProviderReply({ text: "# Not a heading\n**Not bold**", format: "plain" });
  assert.match(result.text, /\\#/);
  assert.match(result.text, /\\\*\\\*/);
  assert.match(result.text, /\n/);
});

test("rich response removes non-content elements", () => {
  const result = normalizeProviderReply({
    format: "html", text: "Answer",
    html: '<p>Answer</p><button>Copy</button><script>alert(1)</script><style>.foo{}</style>'
  });
  assert.equal(result.text, "Answer");
});

test("explicit Markdown is passed through and existing transport replies are unchanged", () => {
  const markdown = { text: "## Hello", format: "markdown" };
  assert.equal(normalizeProviderReply(markdown), markdown);
  const old = { text: "legacy", chatUrl: "https://grok.com/c/x" };
  assert.equal(normalizeProviderReply(old), old);
});
