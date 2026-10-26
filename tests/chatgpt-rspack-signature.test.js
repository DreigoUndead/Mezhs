const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const { nativeChatRequirementsHeaders: getNativeHeaders } = require(
  path.resolve(__dirname, "../integrations/Mezhs.Integrations.ChatGpt/browser/modules/account-transport.js")
);

function testBrowser({ requirements, returnedHeaders, status = 200, providerAvailable = true,
  resourceNames = ["https://chatgpt.com/cdn/assets/manifest-currentbuild.js"],
  providerResourceName = resourceNames[0], providerModuleId = "lXEa" }) {
  const calls = [];
  const imports = [];
  let resourceVisible = true;
  const provider = async callback => {
    const requirementsKey = "gAAAAAC-browser-generated";
    const chatRequirements = await callback(requirementsKey);
    const proofToken = null;
    const turnstileToken = null;
    assert.deepEqual(JSON.parse(JSON.stringify(chatRequirements)), requirements);
    return { chatRequirements, headers: returnedHeaders, proofToken, requirementsKey, turnstileToken };
  };
  const loader = {
    c: { [providerModuleId]: { exports: providerAvailable ? { f: provider } : {} } }
  };
  // One context represents one renderer document. Consecutive script executions
  // must share the same global object even when Resource Timing entries disappear.
  const context = vm.createContext({
    performance: { getEntriesByType: () => resourceVisible
      ? resourceNames.map(name => ({ name }))
      : [] },
    __testImport: async url => {
      imports.push(url);
      return url === providerResourceName ? { __webpack_require__: loader } : {};
    },
    fetch: async (url, options) => {
      calls.push({ url, ...options });
      return {
        ok: status === 200,
        status,
        json: async () => requirements
      };
    }
  });
  const window = { webContents: { executeJavaScript: async script => {
    assert.doesNotThrow(() => new Function(`return ${script};`));
    const runnable = script.replace("await import(runtimeUrl)", "await __testImport(runtimeUrl)");
    return vm.runInContext(runnable, context);
  } } };
  return { window, calls, imports, setResourceVisible: value => { resourceVisible = value; } };
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

test("native requirements runtime discovery supports current ChatGPT asset names", async () => {
  const expected = { token: "issued" };
  const { window, imports } = testBrowser({
    requirements: expected,
    returnedHeaders: { "OpenAI-Sentinel-Chat-Requirements-Token": "issued" },
    resourceNames: [
      "https://chatgpt.com/cdn/assets/entry.client-currentbuild.js",
      "https://chatgpt.com/cdn/assets/4813494d-currentbuild.js"
    ],
    providerResourceName: "https://chatgpt.com/cdn/assets/4813494d-currentbuild.js"
  });
  const headers = await getNativeHeaders(window, {}, "access-secret");
  assert.equal(headers["OpenAI-Sentinel-Chat-Requirements-Token"], "issued");
  assert.deepEqual(imports, [
    "https://chatgpt.com/cdn/assets/entry.client-currentbuild.js",
    "https://chatgpt.com/cdn/assets/4813494d-currentbuild.js"
  ]);
});

test("native requirements discovery supports semantic fallback after module renames", async () => {
  const { window } = testBrowser({
    requirements: { token: "issued" },
    returnedHeaders: { "OpenAI-Sentinel-Chat-Requirements-Token": "issued" },
    providerModuleId: "futureRenamedModule"
  });
  const headers = await getNativeHeaders(window, {}, "access-secret");
  assert.equal(headers["OpenAI-Sentinel-Chat-Requirements-Token"], "issued");
});

test("native requirements discovery retains the legacy module id", async () => {
  const { window } = testBrowser({
    requirements: { token: "issued" },
    returnedHeaders: { "OpenAI-Sentinel-Chat-Requirements-Token": "issued" },
    providerModuleId: "lXE"
  });
  const headers = await getNativeHeaders(window, {}, "access-secret");
  assert.equal(headers["OpenAI-Sentinel-Chat-Requirements-Token"], "issued");
});

test("cached provider survives Resource Timing loss in the same renderer document", async () => {
  const expected = { token: "issued" };
  const { window, calls, imports, setResourceVisible } = testBrowser({
    requirements: expected,
    returnedHeaders: { "OpenAI-Sentinel-Chat-Requirements-Token": "issued" }
  });
  const first = await getNativeHeaders(window, {}, "access-secret");
  assert.equal(first["OpenAI-Sentinel-Chat-Requirements-Token"], "issued");
  assert.equal(imports.length, 1);
  setResourceVisible(false);
  const second = await getNativeHeaders(window, {}, "access-secret");
  assert.equal(second["OpenAI-Sentinel-Chat-Requirements-Token"], "issued");
  assert.equal(imports.length, 1, "same-document cache must not import the bundle again");
  assert.equal(calls.length, 2, "each request must obtain fresh requirements");
  assert.equal(calls[0].body, calls[1].body);
});

test("missing runtime on a new renderer document still fails closed", async () => {
  const { window, calls, imports, setResourceVisible } = testBrowser({
    requirements: { token: "issued" },
    returnedHeaders: { "OpenAI-Sentinel-Chat-Requirements-Token": "issued" }
  });
  setResourceVisible(false);
  await assert.rejects(
    getNativeHeaders(window, {}, "access-secret"),
    /not observed in this document \(resourceEntries=0, cachedProvider=false\)/
  );
  assert.equal(imports.length, 0);
  assert.equal(calls.length, 0);
});

test("native requirements fail closed when frontend provider is unavailable", async () => {
  const { window, calls } = testBrowser({ requirements: {}, providerAvailable: false });
  await assert.rejects(
    getNativeHeaders(window, {}, "access-secret"),
    /native requirements provider export is unavailable/
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
