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

test("HTML conversion keeps answer text inside controls and form elements", () => {
  const result = normalizeProviderReply({
    format: "html", text: "Answer",
    html: '<p>Answer</p><button>Chosen answer</button>' +
      '<form><p>Form explanation</p><textarea>Important details</textarea></form>'
  });
  assert.match(result.text, /Answer/);
  assert.match(result.text, /Chosen answer/);
  assert.match(result.text, /Form explanation/);
  assert.match(result.text, /Important details/);
});

test("GFM checkbox state survives HTML-to-Markdown conversion", () => {
  const result = normalizeProviderReply({
    format: "html", text: "Done Next",
    html: '<ul><li><input type="checkbox" checked> Done</li>' +
      '<li><input type="checkbox"> Next</li></ul>'
  });
  assert.ok(result.text.includes("[x]") && result.text.includes("Done"), result.text);
  assert.ok(result.text.includes("[ ]") && result.text.includes("Next"), result.text);
});

test("visible SVG text labels are not explicitly deleted", () => {
  const result = normalizeProviderReply({
    format: "html", text: "Revenue 120",
    html: '<p>Summary</p><svg><title>Revenue chart</title><text>Revenue 120</text></svg>'
  });
  assert.match(result.text, /Revenue 120/);
});

test("explicit Markdown is passed through and existing transport replies are unchanged", () => {
  const markdown = { text: "## Hello", format: "markdown" };
  assert.equal(normalizeProviderReply(markdown), markdown);
  const old = { text: "legacy", chatUrl: "https://grok.com/c/x" };
  assert.equal(normalizeProviderReply(old), old);
});
