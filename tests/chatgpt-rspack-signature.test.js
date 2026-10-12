const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(
  path.resolve(__dirname, "../integrations/Mezhs.Integrations.ChatGpt/browser/chatgpt.ts"),
  "utf8"
);
const start = source.indexOf("async function nativeChatRequirementsHeaders(");
const end = source.indexOf("async function accessToken(", start);
assert.ok(start >= 0 && end > start);
const getNativeHeaders = vm.runInNewContext(
  "const accountIds = new WeakMap();\n" +
  source.slice(start, end) + "\nnativeChatRequirementsHeaders"
);

function testBrowser({ requirements, returnedHeaders, status = 200, providerAvailable = true }) {
  const calls = [];
  const provider = async callback => {
    const received = await callback("gAAAAAC-browser-generated");
    assert.deepEqual(JSON.parse(JSON.stringify(received)), requirements);
    return { chatRequirements: received, headers: returnedHeaders };
  };
  const loader = { c: { lXE: { exports: providerAvailable ? { f: provider } : {} } } };
  const window = { webContents: { executeJavaScript: async script => {
    assert.doesNotThrow(() => new Function(`return ${script};`));
    const runnable = script.replace("await import(runtimeUrl)", "await __testImport(runtimeUrl)");
    return vm.runInNewContext(runnable, {
      performance: { getEntriesByType: () => [{
        name: "https://chatgpt.com/cdn/assets/633146.abc123.js"
      }] },
      __testImport: async () => ({ __webpack_require__: loader }),
      fetch: async (url, options) => {
        calls.push({ url, ...options });
        return {
          ok: status === 200,
          status,
          json: async () => requirements
        };
      }
    });
  } } };
  return { window, calls };
}

test("native requirements coordinator receives browser key and sends it to prepare", async () => {
  const expected = { token: "server-issued-token", proofofwork: { required: false } };
  const { window, calls } = testBrowser({
    requirements: expected,
    returnedHeaders: {
      "OpenAI-Sentinel-Chat-Requirements-Token": "server-issued-token",
      "OpenAI-Sentinel-Proof-Token": "server-issued-proof",
      "Other-Header": "should-not-be-forwarded"
    }
  });
  const headers = await getNativeHeaders(window, {}, "access-secret");
  assert.equal(headers["OpenAI-Sentinel-Chat-Requirements-Token"], "server-issued-token");
  assert.equal(headers["OpenAI-Sentinel-Proof-Token"], "server-issued-proof");
  assert.equal(headers["Other-Header"], undefined);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, "/backend-api/sentinel/chat-requirements/prepare");
  assert.equal(calls[0].method, "POST");
  assert.equal(calls[0].credentials, "include");
  assert.equal(calls[0].headers.Authorization, "Bearer access-secret");
  assert.deepEqual(JSON.parse(calls[0].body), { p: "gAAAAAC-browser-generated" });
});

test("native requirements fail closed when frontend provider is unavailable", async () => {
  const { window, calls } = testBrowser({ requirements: {}, providerAvailable: false });
  await assert.rejects(
    getNativeHeaders(window, {}, "access-secret"),
    /native requirements provider lXE.f is unavailable/
  );
  assert.equal(calls.length, 0);
});

test("native requirements preparation propagates HTTP failures without replay", async () => {
  const { window, calls } = testBrowser({ requirements: {}, status: 403 });
  await assert.rejects(
    getNativeHeaders(window, {}, "access-secret"),
    /requirements prepare failed with HTTP 403/
  );
  assert.equal(calls.length, 1);
});

test("native requirements fail closed for missing final requirements headers", async () => {
  const { window, calls } = testBrowser({ requirements: { prepare_token: "server" }, returnedHeaders: {} });
  await assert.rejects(
    getNativeHeaders(window, {}, "access-secret"),
    /did not produce a requirements token/
  );
  assert.equal(calls.length, 1);
});
