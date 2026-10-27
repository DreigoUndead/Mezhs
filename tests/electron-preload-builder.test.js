const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { EventEmitter } = require("node:events");
const { createBrowserPreload } = require("../electron/preload-builder");

const root = path.resolve(__dirname, "..");
const chatGptEntry = path.join(root, "integrations", "Mezhs.Integrations.ChatGpt", "browser", "modules", "entry.js");
const grokEntry = path.join(root, "integrations", "Mezhs.Integrations.Grok", "browser", "grok.ts");

function grokModule() {
  const localModule = { exports: {} };
  new Function("module", fs.readFileSync(grokEntry, "utf8"))(localModule);
  return localModule.exports;
}

function loadSandbox(preload, sandboxed = true) {
  const ipcRenderer = new EventEmitter();
  const replies = [];
  const shims = [];
  ipcRenderer.send = (channel, payload) => replies.push({ channel, payload });
  const context = vm.createContext({
    require: name => {
      assert.equal(name, "electron", "sandboxed preloads must not import Node modules");
      return {
        ipcRenderer,
        webFrame: { executeJavaScript: async source => { shims.push(source); } }
      };
    },
    process: { sandboxed },
    console: { error: error => { throw new Error(error); } },
    setTimeout,
    Response,
    navigator: { language: "en-US" },
    fetch: async url => {
      assert.equal(url, "https://grok.com/rest/modes");
      return new Response(JSON.stringify({
        modes: [{ id: "fast", title: "Fast", availability: { available: {} } }],
        defaultModeId: "fast"
      }));
    }
  });
  vm.runInContext(fs.readFileSync(preload.path, "utf8"), context, {
    filename: "generated-sandbox-preload.js"
  });
  async function invoke(operation, args = {}) {
    replies.length = 0;
    const callback = ipcRenderer.listeners("mezhs:page-operation")[0];
    assert.equal(typeof callback, "function");
    await callback({}, { responseChannel: "reply:test", operation, args });
    assert.equal(replies.length, 1);
    assert.equal(replies[0].channel, "reply:test");
    return replies[0].payload;
  }
  return { invoke, context, shims };
}

test("ChatGPT guest page module loads with no unrestricted Node imports", async () => {
  const entry = require(chatGptEntry);
  const preload = createBrowserPreload(chatGptEntry, entry);
  try {
    const runtime = loadSandbox(preload);
    assert.equal(runtime.shims.length, 1);
    assert.deepEqual([...vm.runInContext("Object.keys(pageOperations)", runtime.context)], ["sendPrompt"]);
    const failure = await runtime.invoke("nonexistent");
    assert.equal(failure.ok, false);
    assert.match(failure.error, /not available/);
  } finally {
    preload.cleanup();
    assert.equal(fs.existsSync(preload.path), false);
  }
});

test("Grok standalone page source retains helper closures under sandboxing", async () => {
  const preload = createBrowserPreload(grokEntry, grokModule());
  try {
    const runtime = loadSandbox(preload);
    assert.deepEqual([...vm.runInContext("Object.keys(pageOperations)", runtime.context)],
      ["models", "selectModel", "sendPrompt"]);
    const response = await runtime.invoke("models");
    assert.equal(response.ok, true);
    assert.deepEqual(JSON.parse(JSON.stringify(response.result)), [{ id: "fast", name: "Fast" }]);
  } finally {
    preload.cleanup();
  }
});

test("Providers without page operations receive an empty sandboxed preload", async () => {
  const entry = path.join(root, "integrations", "Mezhs.Integrations.Gemini", "browser", "gemini.ts");
  const preload = createBrowserPreload(entry, { pageOperations: {} });
  try {
    const runtime = loadSandbox(preload);
    assert.deepEqual([...vm.runInContext("Object.keys(pageOperations)", runtime.context)], []);
    const response = await runtime.invoke("models");
    assert.equal(response.ok, false);
  } finally {
    preload.cleanup();
  }
});

test("Missing renderer-only page source fails closed and non-sandboxed renderer is rejected", () => {
  assert.throws(() => createBrowserPreload(chatGptEntry, {
    pageOperations: { sendPrompt() {} }
  }), /require a renderer-safe/);
  const preload = createBrowserPreload(chatGptEntry, require(chatGptEntry));
  try {
    assert.throws(() => loadSandbox(preload, false), /requires an enabled renderer sandbox/);
  } finally {
    preload.cleanup();
  }
});
