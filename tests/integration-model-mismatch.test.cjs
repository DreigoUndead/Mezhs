const { test } = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const Module = require("node:module");
const { createRequire } = Module;

const web = path.resolve(__dirname, "../src/Mezhs.Agent.Web");
const requireWeb = createRequire(path.join(web, "package.json"));
const esbuild = requireWeb("esbuild");
const React = requireWeb("react");
const { renderToStaticMarkup } = requireWeb("react-dom/server");

const result = esbuild.buildSync({
  entryPoints: [path.join(web, "node_modules/@mezhs/web-lib/src/ChatSurface.tsx")],
  preserveSymlinks: true,
  bundle: true,
  platform: "node",
  format: "cjs",
  jsx: "automatic",
  write: false,
  loader: { ".css": "text" },
  external: ["react", "react/jsx-runtime", "react-dom"]
});
const compiled = new Module(path.join(web, "model-mismatch-tests.cjs"));
compiled.filename = path.join(web, "model-mismatch-tests.cjs");
compiled.paths = Module._nodeModulePaths(web);
compiled._compile(result.outputFiles[0].text, compiled.filename);
const { returnedModelMismatch, ChatTranscript } = compiled.exports;

const user = {
  messageId: "request-id", connectionId: "account", role: "user", origin: "human",
  model: "gpt-fast", content: "hello", createdAt: "2026-11-15T12:00:00Z", status: "Completed"
};
const reply = {
  messageId: "answer-id", connectionId: "account", role: "assistant", origin: "assistant",
  parentMessageId: "request-id", model: "gpt-deep",
  content: "answer", createdAt: "2026-11-15T12:00:01Z", status: "Completed"
};

test("model mismatches warn at reply lineage without asserting executed-model verification", () => {
  const messages = [user, reply];
  assert.equal(returnedModelMismatch(reply, messages),
    "Model mismatch: requested gpt-fast; integration returned gpt-deep.");
  const html = renderToStaticMarkup(React.createElement(ChatTranscript, {
    messages, historyReady: true
  }));
  assert.match(html, /role="status"/);
  assert.match(html, /Model mismatch: requested gpt-fast; integration returned gpt-deep\./);
  assert.match(html, /may be the current\/selected model rather than provider-confirmed execution/);
});

test("model mismatches never compare other connections, default requests or missing ancestry", () => {
  assert.equal(returnedModelMismatch({ ...reply, connectionId: "other" }, [user, reply]), null);
  assert.equal(returnedModelMismatch(reply, [{ ...user, model: "default" }, reply]), null);
  assert.equal(returnedModelMismatch({ ...reply, model: "GPT-FAST" }, [user, reply]), null);
  assert.equal(returnedModelMismatch({ ...reply, parentMessageId: null }, [user, reply]), null);
  assert.equal(returnedModelMismatch(reply, [reply]), null);
  assert.equal(returnedModelMismatch({ ...user, model: "gpt-deep" }, [user, reply]), null);
});
