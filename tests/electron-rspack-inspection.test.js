const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const { invokeDiagnostic } = require("../electron/diagnostics");
const chatgpt = require("../integrations/Mezhs.Integrations.ChatGpt/browser/modules/entry.js");
const diagnosticsSource = fs.readFileSync(path.join(__dirname, "../electron/diagnostics.js"), "utf8");

test("Rspack export inspection reads only requested metadata, never exports or functions", async () => {
  let invoked = 0;
  function c(x, y, z) {
    invoked++;
    throw new Error("Never invoke: chatReq proofToken turnstileToken");
  }
  function d(args) {
    invoked++;
    throw new Error("Never invoke: chatReq proofToken turnstileToken");
  }
  function loader() {}
  loader.c = { lXE: { exports: { b: c, f: d, unrelated: "secret-session-token" } } };
  const registry = [[[123], { other: function other() {} }]];
  let captures = 0;
  registry.push = function rspackPush(data) {
    captures++;
    data[2](loader);
  };
  const context = vm.createContext({
    webpackChunkchatgpt: registry,
    crypto: { randomUUID: () => "test-1" },
    performance: { getEntriesByType: () => { throw Error("Resource Timing read"); } }
  });
  const window = { webContents: {
    executeJavaScript: async script => vm.runInContext(script, context)
  } };
  const result = await invokeDiagnostic(
    "inspectRspackExports", { moduleId: "lXE", exportKeys: ["b", "f"] },
    { window, browserModule: chatgpt }
  );
  assert.equal(result.moduleId, "lXE");
  assert.deepEqual(Array.from(result.availableExportKeys), ["b", "f", "unrelated"]);
  assert.deepEqual(Array.from(result.results).map(x => x.exportKey), ["b", "f"]);
  assert.equal(result.results[0].name, "c");
  assert.equal(result.results[0].arity, 3);
  assert.equal(result.results[1].name, "d");
  assert.equal(result.results[1].arity, 1);
  assert.ok(result.results[0].markerContexts.chatReq.includes("chatReq"));
  assert.equal(captures, 1);
  assert.equal(invoked, 0);
  assert.doesNotMatch(JSON.stringify(result), /secret-session-token/);
});

test("timing clear requires opt-in and returns only sanitized counts", async () => {
  const entries = [{ name: "https://chatgpt.com/cdn/assets/manifest-a.js" },
    { name: "https://example.com/private?secret=abc" }];
  const context = vm.createContext({
    performance: {
      getEntriesByType: () => entries,
      clearResourceTimings: () => { entries.length = 0; }
    }
  });
  const window = { webContents: {
    executeJavaScript: async script => vm.runInContext(script, context)
  } };
  await assert.rejects(invokeDiagnostic("rspackTiming", {}, { window, browserModule: chatgpt }),
    /clear=true/);
  const result = await invokeDiagnostic("rspackTiming", { clear: true },
    { window, browserModule: chatgpt });
  assert.equal(result.cleared, true);
  assert.equal(result.before.resourceEntries, 2);
  assert.equal(result.before.runtimeAssets, 1);
  assert.equal(result.after.resourceEntries, 0);
  assert.equal(result.after.runtimeAssets, 0);
  assert.doesNotMatch(JSON.stringify(result), /https|private|secret/);
});

test("Rspack inspection and timing remain exclusive to loopback browser diagnostics", () => {
  const host = fs.readFileSync(path.join(__dirname,
    "../src/Mezhs.Integration.Browser/BrowserDiagnosticsModule.cs"), "utf8");
  assert.match(host, /"inspectRspackExports"/);
  assert.match(host, /"rspackTiming"/);
  assert.match(diagnosticsSource, /case 'inspectRspackExports'/);
  assert.match(diagnosticsSource, /browserModule\?\.name !== 'ChatGPT'/);
  assert.match(diagnosticsSource, /keys.length > 4/);
});
