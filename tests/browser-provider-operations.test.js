const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const chatgpt = require("../integrations/Mezhs.Integrations.ChatGpt/browser/modules/entry.js");

function jsonResponse(value, status = 200) {
  return new Response(JSON.stringify(value), { status,
    headers: { "content-type": "application/json" } });
}
function mockSession(fetch) {
  return { fetch, cookies: { get: async () => [], set: async () => {} } };
}

test("browser operations use one Electron host with ordered scoped events", () => {
  const electron = fs.readFileSync(path.join(root, "electron/main.js"), "utf8");
  const transport = fs.readFileSync(path.join(root,
    "transports/Mezhs.Browser.Electron/ElectronBrowserTransport.cs"), "utf8");
  const contract = fs.readFileSync(path.join(root,
    "transports/Mezhs.Browser.Abstractions/IChatBrowserTransport.cs"), "utf8");
  assert.match(electron, /requestUrl\.pathname === "\/invoke"/);
  assert.match(electron, /queueProviderOperation\(body\)/);
  assert.match(electron, /acknowledgeOperationEvents/);
  assert.match(electron, /queueOperationEvent/);
  assert.match(electron, /request\.arguments\?\.turnMessageId/);
  assert.match(transport, /OperationPollInterval = TimeSpan\.FromSeconds\(2\)/);
  assert.match(transport, /InvokeWithEventsAsync/);
  assert.match(contract, /BrowserStreamProtocolException/);
  assert.match(contract, /BrowserOperationProgress/);
  assert.doesNotMatch(contract, /BrowserWebRequest|BrowserWebResponse/);
});

test("authorization rejects stale backend tokens rather than trusting session cookie", async () => {
  let backendStatus = 401;
  const session = mockSession(async url => {
    const pathname = new URL(String(url)).pathname;
    if (pathname === "/api/auth/session")
      return jsonResponse({ accessToken: "stale-token" });
    if (pathname === "/backend-api/models")
      return jsonResponse({}, backendStatus);
    throw new Error("Unexpected URL");
  });
  assert.equal(await chatgpt.isAuthorized({ webContents: { session } }), false);
  backendStatus = 200;
  assert.equal(await chatgpt.isAuthorized({ webContents: { session } }), true);
});

test("ChatGPT account JavaScript contains only native hook and browser session policy", () => {
  assert.equal(chatgpt.operations.newChat, undefined);
  assert.equal(chatgpt.operations.send, undefined);
  assert.equal(chatgpt.operations.typedHttp, undefined);
  assert.equal(typeof chatgpt.operations.sendPrompt, "function");
  assert.equal(typeof chatgpt.http.fetch, "function");
  assert.equal(typeof chatgpt.http.nativeHooks.chatgptTurn, "function");
  const entry = fs.readFileSync(path.join(root,
    "integrations/Mezhs.Integrations.ChatGpt/browser/modules/entry.js"), "utf8");
  assert.doesNotMatch(entry, /sendAccountMessage|require\(['"].*account-messages/);
  const protocol = fs.readFileSync(path.join(root,
    "integrations/Mezhs.Integrations.ChatGpt/ChatGptAccountProtocol.cs"), "utf8");
  assert.match(protocol, /CallStreamAsync/);
  assert.match(protocol, /ChatGptConversationState\.WaitAsync/);
  assert.match(protocol, /turnMessageId: request\.MessageId/);
  assert.match(protocol, /ChatGptAccountFiles\.UploadAsync/);
});

test("browser-native Sentinel and conduit preparation run once on exact turn", async () => {
  const requests = [];
  const session = mockSession(async (url, options = {}) => {
    const target = new URL(String(url));
    requests.push({ path: target.pathname, method: options.method, headers: options.headers });
    if (target.pathname === "/api/auth/session")
      return jsonResponse({ accessToken: "private-token", user: { id: "account-1" } });
    if (target.pathname === "/backend-api/f/conversation/prepare")
      return jsonResponse({ conduit_token: "server-conduit" });
    throw new Error("Unexpected network request.");
  });
  let scriptCount = 0;
  const window = { webContents: { executeJavaScript: async source => {
    scriptCount++;
    assert.match(source, /lXE/);
    assert.doesNotMatch(source, /document\.querySelector/);
    return { forceLogin: false, headers: {
      "openai-sentinel-chat-requirements-token": "native-requirements",
      "openai-sentinel-proof-token": "native-proof"
    } };
  } } };
  const args = { clientSessionId: "b534dc12-6ee8-4f33-886b-dc112c04801a",
    prepare: { action: "next", model: "chosen", partial_query: { content: { parts: ["hello"] } } } };
  const headers = await chatgpt.http.nativeHooks.chatgptTurn({
    window, session, endpoint: "/backend-api/f/conversation", args,
    signal: new AbortController().signal
  });
  assert.equal(scriptCount, 1);
  assert.equal(headers["x-conduit-token"], "server-conduit");
  assert.equal(headers["openai-sentinel-chat-requirements-token"], "native-requirements");
  assert.equal(headers["x-openai-target-path"], "/backend-api/f/conversation");
  assert.ok(requests.some(x => x.path === "/backend-api/f/conversation/prepare" &&
    x.method === "POST"));
  assert.equal(requests.filter(x => x.path === "/backend-api/f/conversation").length, 0);
  await assert.rejects(chatgpt.http.nativeHooks.chatgptTurn({
    window, session, endpoint: "/backend-api/files", args
  }), /Invalid ChatGPT native turn preparation/);
});

test("account native modules do not use DOM composer or debugger submission", () => {
  for (const name of ["account-transport.js", "entry.js"]) {
    const source = fs.readFileSync(path.join(root,
      "integrations/Mezhs.Integrations.ChatGpt/browser/modules", name), "utf8");
    assert.doesNotMatch(source, /document\.querySelector|sendInputEvent|Network\.requestWillBeSent/);
  }
});
