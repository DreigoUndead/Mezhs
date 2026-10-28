// Only the provider boundary knows whether text is raw Markdown, a rendered
// HTML response or unformatted text. Chat storage/clients receive Markdown.
function normalizeProviderReply(result) {
  if (!result || typeof result !== "object" || typeof result.text !== "string")
    return result;
  if (result.format === "markdown") return result;
  if (result.format !== "plain" && result.format !== "html") return result;

  // Other providers can still run against an existing Electron installation
  // even before the user installs this optional response-conversion dependency.
  const TurndownService = require("turndown");
  const { gfm } = require("turndown-plugin-gfm");
  const converter = new TurndownService({ codeBlockStyle: "fenced", headingStyle: "atx" });
  converter.use(gfm);
  // Strip non-answer code/styles, not arbitrary elements that may contain
  // meaningful response content (e.g. buttons, task checkboxes or SVG labels).
  // Provider-specific chrome should be excluded by the provider extractor.
  converter.remove(["script", "style"]);

  // HTML conversion is only performed when the provider has returned actual
  // response markup. We do not invent structure from flattened text.
  let text = "";
  if (typeof result.html === "string" && result.html.trim())
    text = converter.turndown(result.html).trim();

  if (!text) {
    const escaped = result.text
      .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
    text = converter.turndown("<p>" + escaped.replace(/\r\n?|\n/g, "<br>") + "</p>").trim();
  }
  const { html, format, ...rest } = result;
  return { ...rest, text };
}

module.exports = { normalizeProviderReply };
