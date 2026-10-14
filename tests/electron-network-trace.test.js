const test = require("node:test");
const assert = require("node:assert/strict");
const { EventEmitter } = require("node:events");
const { captureDiagnosticNetwork } = require("../electron/diagnostics");

function diagnosticHarness() {
  class Debugger extends EventEmitter {
    attached = false;
    isAttached() { return this.attached; }
    attach(version) { assert.equal(version, "1.3"); this.attached = true; }
    detach() { this.attached = false; }
    async sendCommand(method) { assert.equal(method, "Network.enable"); return {}; }
  }
  const debuggerClient = new Debugger();
  const context = {
    window: { webContents: { debugger: debuggerClient } },
    sleep: async () => {}
  };
  const capture = (action, waitMs, options) =>
    captureDiagnosticNetwork(context, action, waitMs, options);
  return { capture, debuggerClient };
}

test("Manual network tracing records only header names, relevant paths and status", async () => {
  const { capture, debuggerClient } = diagnosticHarness();
  const captured = await capture(() => {
    debuggerClient.emit("message", {}, "Network.requestWillBeSentExtraInfo", {
      requestId: "r1",
      headers: {
        Cookie: "secret-cookie",
        "OpenAI-Sentinel-Chat-Requirements-Token": "secret-token"
      }
    });
    debuggerClient.emit("message", {}, "Network.requestWillBeSent", {
      requestId: "r1",
      type: "Fetch",
      request: {
        method: "POST",
        url: "https://chatgpt.com/backend-api/f/conversation?secret=hidden",
        headers: {
          Authorization: "Bearer secret",
          "Content-Type": "application/json"
        },
        postData: '{"prompt":"private prompt"}'
      }
    });
    debuggerClient.emit("message", {}, "Network.responseReceived", {
      requestId: "r1",
      response: { status: 200, headers: { "Set-Cookie": "private" } }
    });
    debuggerClient.emit("message", {}, "Network.requestWillBeSent", {
      requestId: "offsite",
      request: {
        method: "GET",
        url: "https://elsewhere.invalid/backend-api/f/conversation",
        headers: {}
      }
    });
  }, 0, { traceHeaders: true });

  assert.equal(captured.length, 1);
  assert.equal(captured[0].method, "POST");
  assert.equal(captured[0].status, 200);
  assert.equal(captured[0].url, "https://chatgpt.com/backend-api/f/conversation");
  assert.deepEqual(captured[0].headerNames, [
    "Authorization",
    "Content-Type",
    "Cookie",
    "OpenAI-Sentinel-Chat-Requirements-Token"
  ]);
  const diagnostic = JSON.stringify(captured);
  assert.doesNotMatch(diagnostic, /secret|private prompt|Set-Cookie|Bearer/);
  assert.equal(debuggerClient.isAttached(), false);
});

test("Existing click diagnostics preserve path/status data without capturing headers", async () => {
  const { capture, debuggerClient } = diagnosticHarness();
  const captured = await capture(() => {
    debuggerClient.emit("message", {}, "Network.requestWillBeSent", {
      requestId: "r2",
      type: "Fetch",
      request: {
        method: "GET",
        url: "https://other.invalid/static?key=secret",
        headers: { Authorization: "secret" }
      }
    });
    debuggerClient.emit("message", {}, "Network.responseReceived", {
      requestId: "r2",
      response: { status: 204 }
    });
  }, 0);
  assert.equal(captured.length, 1);
  assert.equal(captured[0].status, 204);
  assert.equal(captured[0].url, "https://other.invalid/static");
  assert.equal(captured[0].headerNames, undefined);
});

test("A failed trace always detaches the debugger it attached", async () => {
  const { capture, debuggerClient } = diagnosticHarness();
  await assert.rejects(capture(async () => {
    throw new Error("action failed");
  }, 0), /action failed/);
  assert.equal(debuggerClient.isAttached(), false);
  assert.equal(debuggerClient.listenerCount("message"), 0);
});
