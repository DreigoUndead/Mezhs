const test = require("node:test");
const assert = require("node:assert/strict");
const vm = require("node:vm");
const { EventEmitter } = require("node:events");
const {
  invokeDiagnostic,
  captureDiagnosticNetwork,
  diagnosticElementSnapshot
} = require("../electron/diagnostics");

test("Snapshot runs as a named browser function and redacts passwords", () => {
  const element = (tag, text, type) => ({
    tagName: tag,
    type,
    innerText: text,
    getAttribute: () => null,
    getBoundingClientRect: () => ({ x: 3, y: 4, width: 20, height: 10 })
  });
  const password = element("INPUT", "s3cr3t", "password");
  const hidden = element("BUTTON", "hidden", "button");
  const ordinary = element("BUTTON", "send", "button");
  const source = diagnosticElementSnapshot();
  assert.match(source, /function diagnosticElementSnapshotPage\(/);
  const result = vm.runInNewContext(source, {
    document: { title: "Test", activeElement: password, querySelectorAll: () => [ordinary, hidden, password] },
    location: { origin: "https://chatgpt.com", pathname: "/c/abc" },
    innerWidth: 1000,
    innerHeight: 800,
    getComputedStyle: e => ({ visibility: e === hidden ? "hidden" : "visible", display: "block", opacity: "1" })
  });
  assert.equal(result.url, "https://chatgpt.com/c/abc");
  assert.equal(result.activeElement.text, "");
  assert.equal(result.elements.length, 2);
  assert.equal(result.elements[0].text, "send");
  assert.equal(result.elements[1].text, "");
  assert.doesNotMatch(JSON.stringify(result), /s3cr3t/);
});

test("Point diagnostics use a named browser function with serialized coordinates", async () => {
  let script;
  const window = { webContents: { executeJavaScript: s => { script = s; return s; } } };
  await invokeDiagnostic("inspectPoint", { x: 12.5, y: 24 }, { window, browserModule: {}, sleep: async () => {} });
  assert.match(script, /function inspectPointPage\(/);
  const point = [];
  const result = vm.runInNewContext(script, {
    document: { elementFromPoint: (x, y) => {
      point.push(x, y);
      return {
        tagName: "BUTTON", innerText: "Launch", type: "button",
        getAttribute: () => null,
        getBoundingClientRect: () => ({ x: 9, y: 10, width: 11, height: 12 })
      };
    } }
  });
  assert.deepEqual(point, [12.5, 24]);
  assert.equal(result.tag, "button");
  assert.equal(result.text, "Launch");
  await assert.rejects(
    invokeDiagnostic("inspectPoint", { x: "not a number", y: 1 }, { window, browserModule: {}, sleep: async () => {} }),
    /finite x and y/
  );
});

test("Rspack diagnostics validate the selected provider and requested exports before accessing the page", async () => {
  const window = { webContents: { executeJavaScript: () => { throw new Error("unexpected"); } } };
  await assert.rejects(
    invokeDiagnostic("inspectRspackExports", { moduleId: "lXE", exportKeys: ["f"] },
      { window, browserModule: { name: "Other" } }),
    /limited to ChatGPT/
  );
  await assert.rejects(
    invokeDiagnostic("inspectRspackExports", { moduleId: "lXE", exportKeys: ["constructor", "f", "a", "b", "c"] },
      { window, browserModule: { name: "ChatGPT" } }),
    /Invalid Rspack module/
  );
});

test("A failed trace detaches the debugger and removes its listener", async () => {
  class Debugger extends EventEmitter {
    attached = false;
    isAttached() { return this.attached; }
    attach() { this.attached = true; }
    detach() { this.attached = false; }
    async sendCommand() {}
  }
  const debuggerClient = new Debugger();
  const failed = captureDiagnosticNetwork(() => { throw new Error("action failed"); }, 0,
    { window: { webContents: { debugger: debuggerClient } }, sleep: async () => {} });
  await assert.rejects(failed, /action failed/);
  assert.equal(debuggerClient.isAttached(), false);
  assert.equal(debuggerClient.listenerCount("message"), 0);
});
