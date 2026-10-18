const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { inspectRspackExportsScript: makeScript } = require("../electron/diagnostics");
const source = fs.readFileSync(path.join(__dirname, "../electron/diagnostics.js"), "utf8");

test("Rspack export inspection reads only requested function source and never invokes it", async () => {
  let invoked = 0;
  function c(x, y, z) {
    invoked++;
    throw new Error("This function must not run: chatReq proofToken turnstileToken");
  }
  function d(args) {
    invoked++;
    throw new Error("This function must not run: chatReq proofToken turnstileToken");
  }
  const loader = { c: { lXE: { exports: { b: c, f: d, unrelated: "secret-session-token" } } } };
  const script = makeScript("lXE", ["b", "f"]);
  assert.doesNotThrow(() => new Function(`return ${script};`));
  const runnable = script.replace(
    "await import(resources[resources.length - 1])",
    "await __testImport(resources[resources.length - 1])"
  );
  const result = await vm.runInNewContext(runnable, {
    performance: { getEntriesByType: () => [{ name: "https://chatgpt.com/cdn/assets/633146.123abc.js" }] },
    __testImport: async () => ({ __webpack_require__: loader })
  });
  assert.equal(result.moduleId, "lXE");
  assert.deepEqual(Array.from(result.availableExportKeys), ["b", "f", "unrelated"]);
  assert.deepEqual(Array.from(result.results).map(x => x.exportKey), ["b", "f"]);
  assert.equal(result.results[0].name, "c");
  assert.equal(result.results[0].arity, 3);
  assert.equal(result.results[1].name, "d");
  assert.equal(result.results[1].arity, 1);
  assert.ok(result.results[0].markerContexts.chatReq.includes("chatReq"));
  assert.ok(result.results[1].markerContexts.proofToken.includes("proofToken"));
  assert.equal(invoked, 0);
  assert.doesNotMatch(JSON.stringify(result), /secret-session-token/);
});

test("Rspack export diagnostics are only exposed to the existing diagnostic endpoint", () => {
  const host = fs.readFileSync(path.join(__dirname, "../src/Mezhs.Integration.Browser/BrowserDiagnosticsModule.cs"), "utf8");
  assert.match(host, /"inspectRspackExports"/);
  assert.match(source, /case 'inspectRspackExports'/);
  assert.match(source, /browserModule\?\.name !== 'ChatGPT'/);
  assert.match(source, /keys.length > 4/);
});
