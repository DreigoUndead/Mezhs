const test = require("node:test");
const assert = require("node:assert/strict");
const { EventEmitter } = require("node:events");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const root = path.resolve(__dirname, "..");

function loadChatGptModule() {
  const source = fs.readFileSync(
    path.join(root, "integrations", "Mezhs.Integrations.ChatGpt", "browser", "chatgpt.ts"),
    "utf8"
  );
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "mezhs-chatgpt-test-"));
  const file = path.join(directory, "chatgpt.cjs");
  fs.writeFileSync(file, source);
  return require(file);
}

function jsonResponse(value, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "content-type": "application/json" }
  });
}

function textResponse(value, status = 200, contentType = "text/plain") {
  return new Response(value, {
    status,
    headers: { "content-type": contentType }
  });
}

function mockSession(fetch, deviceId = null) {
  return {
    fetch,
    cookies: {
      get: async () => deviceId ? [{ value: deviceId }] : []
    }
  };
}

function completedConversation(conversationId, projectId = null) {
  return {
    conversation_id: conversationId,
    gizmo_id: projectId,
    current_node: "assistant-1",
    mapping: {
      "assistant-1": {
        parent: null,
        message: {
          id: "assistant-1",
          author: { role: "assistant" },
          status: "finished_successfully",
          content: { parts: ["answer"] }
        }
      }
    }
  };
}

test("browser transport has one named provider-operation bridge", () => {
  const electron = fs.readFileSync(path.join(root, "electron", "main.js"), "utf8");
  const contract = fs.readFileSync(
    path.join(root, "transports", "Mezhs.Browser.Abstractions", "IChatBrowserTransport.cs"),
    "utf8"
  );

  assert.match(electron, /request\.url === "\/invoke"/);
  assert.doesNotMatch(electron, /request\.url === "\/prompt"/);
  assert.doesNotMatch(electron, /request\.url === "\/fetch"/);
  assert.match(contract, /InvokeAsync<TResult>/);
  assert.doesNotMatch(contract, /SendPromptAsync|SendWebRequestAsync|BrowserWebRequest|BrowserWebResponse/);
});

test("ChatGPT getProjects uses the private API and follows pagination", async () => {
  const chatgpt = loadChatGptModule();
  const calls = [];
  const session = mockSession(async (url, options = {}) => {
    const target = new URL(String(url));
    calls.push({ target, options });

    if (target.pathname === "/api/auth/session")
      return jsonResponse({ accessToken: "token" });

    if (target.pathname === "/backend-api/gizmos/snorlax/sidebar") {
      assert.equal(options.headers.Authorization, "Bearer token");
      if (!target.searchParams.has("cursor")) {
        return jsonResponse({
          items: [
            { gizmo: { gizmo: { id: "g-p-one", display: { name: "One" } } } },
            { gizmo: { gizmo: { id: "not-a-project", display: { name: "Ignore" } } } }
          ],
          cursor: "next"
        });
      }
      assert.equal(target.searchParams.get("cursor"), "next");
      return jsonResponse({
        items: [{ gizmo: { id: "g-p-two", display: { name: "Two" } } }],
        cursor: null
      });
    }

    throw new Error(`Unexpected request ${target}`);
  });

  const result = await chatgpt.operations.getProjects({ session, args: {}, sleep: async () => {} });
  assert.deepEqual(result, [
    { id: "g-p-one", name: "One" },
    { id: "g-p-two", name: "Two" }
  ]);
  assert.equal(calls.filter(call => call.target.pathname === "/backend-api/gizmos/snorlax/sidebar").length, 2);
});

test("ChatGPT newChat creates the first turn inside the selected project", async () => {
  const chatgpt = loadChatGptModule();
  let conversationPayload;
  let requirementsPayload;
  let conversationHeaders;
  const session = mockSession(async (url, options = {}) => {
    const target = new URL(String(url));

    if (target.pathname === "/api/auth/session")
      return jsonResponse({ accessToken: "token" });

    if (target.pathname === "/backend-api/sentinel/chat-requirements") {
      requirementsPayload = JSON.parse(options.body);
      return jsonResponse({ token: "sentinel" });
    }

    if (target.pathname === "/backend-api/conversation" && options.method === "POST") {
      conversationPayload = JSON.parse(options.body);
      conversationHeaders = options.headers;
      return textResponse('data: {"conversation_id":"conv-1"}\n\ndata: [DONE]\n\n', 200, "text/event-stream");
    }

    if (target.pathname === "/backend-api/conversation/conv-1")
      return jsonResponse(completedConversation("conv-1", "g-p-mezhs"));

    throw new Error(`Unexpected request ${target}`);
  }, "device-1");

  const result = await chatgpt.operations.newChat({
    session,
    args: {
      prompt: "hello",
      projectId: "g-p-mezhs",
      conversationId: null,
      parentMessageId: null,
      files: []
    },
    sleep: async () => {}
  });

  assert.equal(requirementsPayload.conversation_mode_kind, "gizmo_interaction");
  assert.deepEqual(conversationPayload.conversation_mode, {
    kind: "gizmo_interaction",
    gizmo_id: "g-p-mezhs"
  });
  assert.equal(conversationPayload.messages[0].content.parts.at(-1), "hello");
  assert.equal("conversation_id" in conversationPayload, false);
  assert.equal(conversationHeaders["Openai-Sentinel-Chat-Requirements-Token"], "sentinel");
  assert.equal(conversationHeaders["Oai-Device-Id"], "device-1");
  assert.equal(result.conversationId, "conv-1");
  assert.equal(result.parentMessageId, "assistant-1");
  assert.equal(result.projectId, "g-p-mezhs");
  assert.equal(result.text, "answer");
});

test("ChatGPT send continues the existing conversation without overriding its mode", async () => {
  const chatgpt = loadChatGptModule();
  let conversationPayload;
  const session = mockSession(async (url, options = {}) => {
    const target = new URL(String(url));

    if (target.pathname === "/api/auth/session")
      return jsonResponse({ accessToken: "token" });

    if (target.pathname === "/backend-api/sentinel/chat-requirements")
      return jsonResponse({ token: "sentinel" });

    if (target.pathname === "/backend-api/conversation" && options.method === "POST") {
      conversationPayload = JSON.parse(options.body);
      return textResponse('data: {"conversation_id":"conv-existing"}\n\n', 200, "text/event-stream");
    }

    if (target.pathname === "/backend-api/conversation/conv-existing")
      return jsonResponse(completedConversation("conv-existing", "g-p-mezhs"));

    throw new Error(`Unexpected request ${target}`);
  });

  const result = await chatgpt.operations.send({
    session,
    args: {
      prompt: "continue",
      conversationId: "conv-existing",
      parentMessageId: "assistant-old",
      files: []
    },
    sleep: async () => {}
  });

  assert.equal(conversationPayload.conversation_id, "conv-existing");
  assert.equal(conversationPayload.parent_message_id, "assistant-old");
  assert.equal("conversation_mode" in conversationPayload, false);
  assert.equal(result.projectId, "g-p-mezhs");
});

test("ChatGPT account send reports the required Sentinel challenge types", async () => {
  const cases = [
    [{ proofofwork: { required: true } }, "proof-of-work"],
    [{ arkose: { required: true } }, "Arkose"],
    [{ turnstile: { required: true } }, "Turnstile"],
    [{ so: { required: true } }, "so"],
    [
      { proofofwork: { required: true }, turnstile: { required: true } },
      "proof-of-work, Turnstile"
    ]
  ];

  for (const [required, expected] of cases) {
    const chatgpt = loadChatGptModule();
    let conversationCalled = false;
    let shown = false;
    const session = mockSession(async (url, options = {}) => {
      const target = new URL(String(url));

      if (target.pathname === "/api/auth/session")
        return jsonResponse({ accessToken: "token" });

      if (target.pathname === "/backend-api/sentinel/chat-requirements")
        return jsonResponse({ token: "sentinel", ...required });

      if (target.pathname === "/backend-api/conversation") {
        conversationCalled = true;
        return textResponse("unexpected");
      }

      throw new Error(`Unexpected request ${target} ${options.method || "GET"}`);
    });

    const error = await chatgpt.operations.newChat({
      window: { show() { shown = true; }, focus() {} },
      session,
      args: { prompt: "hello", projectId: "g-p-mezhs", files: [] },
      sleep: async () => {}
    }).then(() => null, caught => caught);

    assert.match(error?.message || "", new RegExp(`^ChatGPT requires Sentinel challenge\\(s\\): ${expected.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}\\.`));
    assert.match(error?.message || "", /CHATGPT_PROTOCOL_TRACE/);
    assert.equal(shown, true);
    assert.equal(conversationCalled, false);
  }
});

test("ChatGPT frontend protocol trace captures structure without secret values", async () => {
  const chatgpt = loadChatGptModule();
  const debuggerApi = new EventEmitter();
  debuggerApi.attached = false;
  debuggerApi.commands = [];
  debuggerApi.isAttached = () => debuggerApi.attached;
  debuggerApi.attach = () => { debuggerApi.attached = true; };
  debuggerApi.sendCommand = async (method, args) => {
    debuggerApi.commands.push({ method, args });
    if (method === "Network.getResponseBody") {
      return {
        base64Encoded: false,
        body: JSON.stringify({
          prepare_token: "response-secret-token",
          proofofwork: { required: true, difficulty: 17, seed: "response-secret-seed" },
          turnstile: { required: true, challenge: "response-secret-challenge" },
          so: { required: true, token: "response-secret-so" }
        })
      };
    }
    return {};
  };

  const logs = [];
  const previousConsoleError = console.error;
  console.error = value => logs.push(String(value));
  try {
    await chatgpt.afterInitialize({
      window: { webContents: { debugger: debuggerApi } },
      session: {},
      sleep: async () => {}
    });

    debuggerApi.emit("message", {}, "Network.requestWillBeSent", {
      requestId: "request-1",
      request: {
        url: "https://chatgpt.com/backend-api/sentinel/chat-requirements/prepare?secret=do-not-log",
        method: "POST",
        headers: {
          Authorization: "Bearer request-secret-auth",
          "Content-Type": "application/json"
        },
        postData: JSON.stringify({
          conversation_mode_kind: "gizmo_interaction",
          prepare_token: "request-secret-token",
          proofofwork: { required: true, difficulty: 9, seed: "request-secret-seed" }
        })
      }
    });
    debuggerApi.emit("message", {}, "Network.responseReceived", {
      requestId: "request-1",
      response: {
        status: 200,
        headers: { "Content-Type": "application/json", "Set-Cookie": "response-secret-cookie" }
      }
    });
    debuggerApi.emit("message", {}, "Network.loadingFinished", { requestId: "request-1" });
    await new Promise(resolve => setImmediate(resolve));
  } finally {
    console.error = previousConsoleError;
  }

  const output = logs.join("\n");
  assert.equal(debuggerApi.attached, true);
  assert.equal(debuggerApi.commands.some(command => command.method === "Network.enable"), true);
  assert.match(output, /CHATGPT_PROTOCOL_TRACE/);
  assert.match(output, /chat-requirements\/prepare/);
  assert.match(output, /conversation_mode_kind/);
  assert.match(output, /gizmo_interaction/);
  assert.match(output, /difficulty/);
  assert.match(output, /"difficulty":17/);
  assert.match(output, /Authorization/);
  assert.match(output, /Set-Cookie/);
  assert.match(output, /<redacted:string:/);
  assert.doesNotMatch(output, /do-not-log/);
  assert.doesNotMatch(output, /request-secret/);
  assert.doesNotMatch(output, /response-secret/);
});
