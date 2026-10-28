const TurndownService = require("turndown");
const { gfm } = require("turndown-plugin-gfm");

const converter = new TurndownService({ codeBlockStyle: "fenced", headingStyle: "atx" });
converter.use(gfm);

// The Electron boundary converts provider HTML/plain text to stored Markdown.
function normalizeProviderReply(result) {
  if (!result || typeof result !== "object" || typeof result.text !== "string")
    return result;
  if (result.format !== "plain" && result.format !== "html") return result;


  // HTML conversion is only performed when the provider has returned actual
  // response markup. We do not invent structure from flattened text.
  let text = "";
  if (typeof result.html === "string" && result.html.trim())
    text = converter.turndown(result.html).trim();

  if (!text) {
    const escaped = result.text
      .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
    text = converter.turndown("<p>" + escaped.replace(/\r\n?|\n/g, "<br>") + "</p>").trim();
  }
  const { html, format, ...rest } = result;
  return { ...rest, text };
}

module.exports = { normalizeProviderReply };
