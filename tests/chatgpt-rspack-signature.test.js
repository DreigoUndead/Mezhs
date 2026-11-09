const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const vm = require("node:vm");

const { nativeChatRequirementsHeaders: getNativeHeaders } = require(
  path.resolve(__dirname, "../integrations/Mezhs.Integrations.ChatGpt/browser/modules/account-transport.js")
);
const { inspectRspackExportsScript } = require(
  path.resolve(__dirname, "../integrations/Mezhs.Integrations.ChatGpt/browser/modules/rspack-runtime.js")
);

function browser(options = {}) {
  let captures = 0;
  let preparations = 0;
  let uuid = 0;
  const requests = [];
  const provider = async callback => {
    const requirementsKey = "key-" + ++preparations;
    const chatRequirements = await callback(requirementsKey);
    const proofToken = null;
    const turnstileToken = null;
    return {
      chatRequirements, requirementsKey, proofToken, turnstileToken,
      headers: options.headers ?? {
        "OpenAI-Sentinel-Chat-Requirements-Token": "server-" + preparations,
        "OpenAI-Sentinel-Proof-Token": "proof",
        "Unrelated-Header": "must-not-forward"
      }
    };
  };
  function loader() {}
  loader.c = options.cache || {
    "renamed-coordinator": { exports: { f: provider, secret: "never-return" } }
  };
  const registry = [[[123], { "loaded-module": function loadedModule() {} }]];
  registry.push = function rspackChunkPush(data) {
    captures++;
    if (typeof data[2] === "function") data[2](loader);
  };
  const globals = {
    webpackChunkchatgpt: registry,
    crypto: { randomUUID: () => "test-" + ++uuid },
    performance: {
      getEntriesByType: () => { throw new Error("Production must not read Resource Timing."); }
    },
    fetch: async (url, args) => {
      requests.push({ url, ...args });
      return {
        ok: (options.status ?? 200) === 200,
        status: options.status ?? 200,
        json: async () => ({ token: "server-" + preparations })
      };
    }
  };
  const context = vm.createContext(globals);
  const window = { webContents: {
    executeJavaScript: async source => {
      assert.doesNotThrow(() => new Function("return " + source));
      return vm.runInContext(source, context);
    }
  }};
  return {
    context, globals, window, registry, loader, provider, requests,
    get captures() { return captures; },
    get preparations() { return preparations; }
  };
}

test("empty Resource Timing still uses a semantic coordinator and filters headers", async () => {
  const b = browser();
  const result = await getNativeHeaders(b.window, {}, "access-token");
  assert.equal(result["OpenAI-Sentinel-Chat-Requirements-Token"], "server-1");
  assert.equal(result["OpenAI-Sentinel-Proof-Token"], "proof");
  assert.equal(result["Unrelated-Header"], undefined);
  assert.equal(b.captures, 1);
  assert.equal(b.preparations, 1);
  assert.equal(b.requests.length, 1);
  assert.equal(b.requests[0].url, "/backend-api/sentinel/chat-requirements/prepare");
  assert.equal(b.requests[0].method, "POST");
  assert.equal(b.requests[0].credentials, "include");
  assert.equal(b.requests[0].headers.Authorization, "Bearer access-token");
  assert.deepEqual(JSON.parse(b.requests[0].body), { p: "key-1" });
});

test("diagnostic first followed by two Account turns uses one capture and fresh prepares", async () => {
  const b = browser();
  const info = await b.window.webContents.executeJavaScript(
    inspectRspackExportsScript("renamed-coordinator", ["f", "secret"]));
  assert.equal(info.moduleId, "renamed-coordinator");
  assert.deepEqual(Array.from(info.availableExportKeys), ["f", "secret"]);
  assert.equal(info.results[0].arity, 1);
  assert.equal(info.results[1].type, "string");
  assert.doesNotMatch(JSON.stringify(info), /never-return/);
  const first = await getNativeHeaders(b.window, {}, "token");
  const second = await getNativeHeaders(b.window, {}, "token");
  assert.equal(first["OpenAI-Sentinel-Chat-Requirements-Token"], "server-1");
  assert.equal(second["OpenAI-Sentinel-Chat-Requirements-Token"], "server-2");
  assert.equal(b.captures, 1);
  assert.equal(b.preparations, 2);
  assert.deepEqual(b.requests.map(x => JSON.parse(x.body).p), ["key-1", "key-2"]);
});

test("missing coordinator fails before Sentinel preparation", async () => {
  const b = browser({ cache: { other: { exports: { x: function x() {} } } } });
  await assert.rejects(getNativeHeaders(b.window, {}, "token"), /coordinator is unavailable/);
  assert.equal(b.requests.length, 0);
});

test("ambiguous coordinators fail closed before Sentinel preparation", async () => {
  const b = browser();
  const cloned = async callback => {
    const requirementsKey = "unused";
    const chatRequirements = await callback(requirementsKey);
    const proofToken = null;
    const turnstileToken = null;
    return { chatRequirements, proofToken, turnstileToken, requirementsKey };
  };
  b.loader.c.second = { exports: { f: cloned } };
  await assert.rejects(getNativeHeaders(b.window, {}, "token"), /coordinator is ambiguous/);
  assert.equal(b.requests.length, 0);
});

test("duplicate coordinator identity across loaders is accepted", async () => {
  const b = browser();
  function loader() {}
  loader.c = { duplicate: { exports: { f: b.provider } } };
  const registry = [[[11], { x: function x() {} }]];
  registry.push = function rspackPush(data) { data[2](loader); };
  b.globals.webpackChunkother = registry;
  const result = await getNativeHeaders(b.window, {}, "token");
  assert.equal(result["OpenAI-Sentinel-Chat-Requirements-Token"], "server-1");
});

test("missing registry and native arrays are not mutated", async () => {
  const b = browser();
  delete b.globals.webpackChunkchatgpt;
  const ordinary = [[[123], { test: function test() {} }]];
  b.globals.webpackChunkordinary = ordinary;
  await assert.rejects(getNativeHeaders(b.window, {}, "token"), /chunk registry was not observed/);
  assert.equal(ordinary.length, 1);
  assert.equal(b.requests.length, 0);
});

test("global accessors and custom-push decoys are not invoked", async () => {
  const b = browser();
  let accessed = 0;
  Object.defineProperty(b.globals, "webpackChunkgetter", {
    get() { accessed++; throw new Error("getter invoked"); },
    configurable: true
  });
  const decoy = [["not a processed chunk"]];
  decoy.push = function unrelatedPush() { throw new Error("decoy mutated"); };
  b.globals.webpackChunkdecoy = decoy;
  const result = await getNativeHeaders(b.window, {}, "token");
  assert.equal(result["OpenAI-Sentinel-Chat-Requirements-Token"], "server-1");
  assert.equal(accessed, 0);
  assert.equal(decoy.length, 1);
  assert.equal(b.captures, 1);
});

test("module, export and function-metadata getters are not executed", async () => {
  const b = browser();
  let invoked = 0;
  const getter = () => { invoked++; throw Error("getter invoked"); };
  Object.defineProperty(b.loader.c, "getter-record", { enumerable: true, get: getter });
  const getterExports = {};
  Object.defineProperty(getterExports, "f", { enumerable: true, get: getter });
  b.loader.c.getterExports = { exports: getterExports };
  Object.defineProperty(b.provider, "name", { get: getter, configurable: true });
  const result = await getNativeHeaders(b.window, {}, "token");
  assert.equal(result["OpenAI-Sentinel-Chat-Requirements-Token"], "server-1");
  const info = await b.window.webContents.executeJavaScript(
    inspectRspackExportsScript("getterExports", ["f"]));
  assert.equal(info.results[0].type, "undefined");
  assert.equal(invoked, 0);
});

test("forged or malformed shared loader cache fails without mutating the registry", async () => {
  const b = browser();
  Object.defineProperty(b.globals, Symbol.for("mezhs.chatgpt.rspackLoaders.v1"), {
    value: { version: 1, loaders: [b.loader] }
  });
  await assert.rejects(getNativeHeaders(b.window, {}, "token"), /shared loader cache is invalid/);
  assert.equal(b.captures, 0);
  assert.equal(b.requests.length, 0);
});

test("module scan limits fail before selection; 10,185 modules are supported", async () => {
  const cache = {};
  for (let i = 0; i < 10184; i++)
    cache["module" + i] = { exports: { x: i } };
  const b = browser({ cache });
  cache.provider = { exports: { f: b.provider } };
  await getNativeHeaders(b.window, {}, "token");
  assert.equal(b.requests.length, 1);
  for (let i = 10185; i <= 20000; i++)
    cache["module" + i] = { exports: {} };
  await assert.rejects(getNativeHeaders(b.window, {}, "token"), /module scan limit exceeded/);
  assert.equal(b.requests.length, 1);
});

test("failed prepare and absent requirements tokens never succeed", async () => {
  const failed = browser({ status: 403 });
  await assert.rejects(getNativeHeaders(failed.window, {}, "token"), /prepare failed with HTTP 403/);
  assert.equal(failed.requests.length, 1);
  const missing = browser({ headers: {} });
  await assert.rejects(getNativeHeaders(missing.window, {}, "token"), /did not produce a requirements token/);
  assert.equal(missing.requests.length, 1);
});
