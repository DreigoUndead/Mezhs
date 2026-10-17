const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { inspectRspackExportsPage } = require("../electron/chatgpt-rspack-diagnostics");
const { invokeDiagnostic, inspectDocumentPage } = require("../electron/diagnostics");

test("Rspack diagnostics inspect functions without invoking inspected exports", async () => {
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
  const script = "(" + inspectRspackExportsPage.toString() + ")('lXE',['b','f'])";
  assert.doesNotThrow(() => new Function("return " + script));
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
  assert.ok(result.results[0].markerContexts.chatReq.includes("chatReq"));
  assert.ok(result.results[1].markerContexts.proofToken.includes("proofToken"));
  assert.equal(invoked, 0);
  assert.doesNotMatch(JSON.stringify(result), /secret-session-token/);
});

test("Rspack inspection is only available through ChatGPT diagnostics", async () => {
  const context = {
    window: { webContents: {} },
    browserModule: { name: "Grok" },
    sleep: async () => {}
  };
  await assert.rejects(
    invokeDiagnostic(context, "inspectRspackExports", { moduleId: "lXE", exportKeys: ["f"] }),
    /limited to ChatGPT/
  );
  context.browserModule.name = "ChatGPT";
  await assert.rejects(
    invokeDiagnostic(context, "inspectRspackExports", { moduleId: "lXE", exportKeys: ["f", "!!"] }),
    /Invalid Rspack/
  );
});

test("Normal renderer diagnostic functions do not disclose password values", () => {
  const password = {
    type: "password",
    tagName: "INPUT",
    innerText: "",
    textContent: "",
    value: "private-password",
    disabled: false,
    getAttribute: () => null,
    getBoundingClientRect: () => ({ x: 1, y: 2, width: 100, height: 20 })
  };
  const context = {
    document: {
      title: "Test",
      activeElement: password,
      querySelectorAll: () => [password]
    },
    location: { origin: "https://example.test", pathname: "/chat" },
    innerWidth: 1000,
    innerHeight: 700,
    getComputedStyle: () => ({ visibility: "visible", display: "block", opacity: "1" })
  };
  const snapshot = vm.runInNewContext("(" + inspectDocumentPage.toString() + ")()", context);
  assert.equal(snapshot.activeElement.text, "");
  assert.equal(snapshot.elements[0].text, "");
  assert.equal(snapshot.url, "https://example.test/chat");
});

test("Electron routes explicit diagnostic operations, not provider-name prefixes", () => {
  const main = fs.readFileSync(path.resolve(__dirname, "../electron/main.js"), "utf8");
  const transport = fs.readFileSync(path.resolve(__dirname,
    "../transports/Mezhs.Browser.Electron/ElectronBrowserTransport.cs"), "utf8");
  assert.match(main, /kind === "diagnostic"/);
  assert.match(main, /kind !== "provider"/);
  assert.doesNotMatch(main, /startsWith\("\$diagnostics\/"\)/);
  assert.match(transport, /InvokeDiagnosticAsync<TResult>/);
  assert.match(transport, /new \{ kind, operation, arguments \}/);
});
