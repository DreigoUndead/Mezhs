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
  scriptNames = [], providerResourceName = resourceNames[0], providerModuleId = "lXEa",
  prepareResponse = null, pageUrl = "https://chatgpt.com/", readyState = "complete" }) {
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
    TextDecoder, URL, location: new URL(pageUrl),
    document: { readyState, scripts: scriptNames.map(src => ({ src })) },
    performance: { getEntriesByType: () => resourceVisible
      ? resourceNames.map(name => ({ name }))
      : [] },
    __testImport: async url => {
      imports.push(url);
      return url === providerResourceName ? { __webpack_require__: loader } : {};
    },
    fetch: async (url, options) => {
      calls.push({ url, ...options });
      return prepareResponse || new Response(JSON.stringify(requirements), {
        status, headers: { "content-type": "application/json" }
      });
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

test("script tags supply ChatGPT asset candidates when Resource Timing is empty", async () => {
  const runtimeAsset = "https://chatgpt.com/cdn/assets/4813494d-currentbuild.js";
  const { window, imports, calls } = testBrowser({
    requirements: { token: "issued" },
    returnedHeaders: { "OpenAI-Sentinel-Chat-Requirements-Token": "issued" },
    resourceNames: [],
    scriptNames: [
      "https://attacker.example/cdn/assets/fake.js",
      "https://chatgpt.com/other/path/manifest.js",
      "https://chatgpt.com/cdn/assets/entry.client-currentbuild.js",
      runtimeAsset,
      runtimeAsset
    ],
    providerResourceName: runtimeAsset
  });
  const headers = await getNativeHeaders(window, {}, "access-secret");
  assert.equal(headers["OpenAI-Sentinel-Chat-Requirements-Token"], "issued");
  assert.deepEqual(imports, [
    "https://chatgpt.com/cdn/assets/entry.client-currentbuild.js",
    runtimeAsset
  ], "only unique ChatGPT runtime assets may be imported");
  assert.equal(calls.length, 1, "only one requirements prepare request");
});

test("untrusted script URLs cannot replace missing ChatGPT runtime", async () => {
  const { window, imports, calls } = testBrowser({
    requirements: { token: "issued" },
    returnedHeaders: { "OpenAI-Sentinel-Chat-Requirements-Token": "issued" },
    resourceNames: [],
    scriptNames: ["https://untrusted.example/cdn/assets/manifest.js"]
  });
  await assert.rejects(
    getNativeHeaders(window, {}, "access-secret"),
    /not observed in this document/
  );
  assert.equal(imports.length, 0);
  assert.equal(calls.length, 0);
});

test("failed discovery reports only fixed asset categories from the current renderer", async () => {
  const { window, calls, imports } = testBrowser({
    resourceNames: ["https://chatgpt.com/unauth-mweb/runtime.js", "https://chatgpt.com/cdn/assets/theme.css"],
    scriptNames: ["https://chatgpt.com/unauth-mweb/app.js",
      "https://malicious.example/private-secret.js"],
    pageUrl: "https://chatgpt.com/",
    readyState: "complete"
  });
  await assert.rejects(
    getNativeHeaders(window, {}, "private-auth-secret"),
    error => {
      assert.match(error.message, /not observed in this document/);
      assert.match(error.message,
        /\[page=root,ready=complete,scripts=unauth_mweb,resources=cdn_assets,jsMask=6\]/);
      assert.doesNotMatch(error.message, /private-secret|malicious\.example|private-auth-secret/);
      return true;
    }
  );
  assert.equal(imports.length, 0, "unrecognized assets are not imported");
  assert.equal(calls.length, 0, "no Sentinel request without verified provider");
});

test("native runtime diagnostics do not classify non-JavaScript suffixes as scripts", async () => {
  const { window, calls, imports } = testBrowser({
    resourceNames: [
      "https://chatgpt.com/cdn/assets/not-a-script-jsonjs",
      "https://chatgpt.com/other/not-a-script-xjs"
    ],
    scriptNames: ["https://untrusted.example/not-a-script-xjs"]
  });
  await assert.rejects(
    getNativeHeaders(window, {}, "synthetic-secret"),
    error => {
      assert.match(error.message, /jsMask=0/);
      assert.match(error.message, /not observed in this document/);
      assert.doesNotMatch(error.message, /synthetic-secret/);
      return true;
    }
  );
  assert.equal(imports.length, 0);
  assert.equal(calls.length, 0);
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

test("native Sentinel preparation rejects and cancels oversized successful response", async () => {
  let cancelled = false;
  let readCount = 0;
  const body = new ReadableStream({
    pull(controller) {
      readCount++;
      controller.enqueue(new Uint8Array(256 * 1024));
    },
    cancel() { cancelled = true; }
  });
  const { window, calls } = testBrowser({
    requirements: {},
    returnedHeaders: { "OpenAI-Sentinel-Chat-Requirements-Token": "not-used" },
    prepareResponse: new Response(body, { status: 200 })
  });
  await assert.rejects(
    getNativeHeaders(window, {}, "private-token"),
    /ChatGPT requirements prepare response exceeded 1 MiB/
  );
  assert.equal(cancelled, true, "unread source must be cancelled");
  assert.ok(readCount <= 8, "must stop reading near the 1 MiB limit");
  assert.equal(calls.length, 1, "native preparation must not be replayed");
});
