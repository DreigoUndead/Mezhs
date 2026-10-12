const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { EventEmitter } = require("node:events");

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
  const cookies = new Map();
  return {
    fetch,
    cookies: {
      get: async ({ name } = {}) => {
        if (cookies.has(name))
          return [{ name, value: cookies.get(name) }];
        return deviceId ? [{ value: deviceId }] : [];
      },
      set: async cookie => {
        cookies.set(cookie.name, cookie.value);
      }
    },
    __cookies: cookies
  };
}

function completedConversation(
  conversationId,
  projectId = null,
  model = "served-test",
  requestMessageId = null,
  resolvedModel = null
) {
  const conversation = {
    conversation_id: conversationId,
    gizmo_id: projectId,
    current_node: "assistant-1",
    mapping: {
      "assistant-1": {
        parent: requestMessageId ? "request-1" : null,
        message: {
          id: "assistant-1",
          author: { role: "assistant" },
          status: "finished_successfully",
          content: { parts: ["answer"] },
          metadata: { model_slug: model }
        }
      }
    }
  };
  if (requestMessageId) {
    conversation.mapping["request-1"] = {
      parent: null,
      message: {
        id: requestMessageId,
        author: { role: "user" },
        status: "finished_successfully",
        content: { parts: ["question"] },
        metadata: { resolved_model_slug: resolvedModel }
      }
    };
  }
  return conversation;
}

test("browser transport polls long provider operations through short HTTP requests", () => {
  const electron = fs.readFileSync(path.join(root, "electron", "main.js"), "utf8");
  const transport = fs.readFileSync(
    path.join(root, "transports", "Mezhs.Browser.Electron", "ElectronBrowserTransport.cs"),
    "utf8"
  );
  const contract = fs.readFileSync(
    path.join(root, "transports", "Mezhs.Browser.Abstractions", "IChatBrowserTransport.cs"),
    "utf8"
  );

  assert.match(electron, /requestUrl\.pathname === "\/invoke"/);
  assert.match(electron, /requestUrl\.pathname\.startsWith\("\/invoke\/"\)/);
  assert.match(electron, /writeJson\(response, 202, \{ operationId \}\)/);
  assert.match(electron, /queueProviderOperation\(body\)/);
  assert.doesNotMatch(electron, /request\.url === "\/prompt"/);
  assert.doesNotMatch(electron, /request\.url === "\/fetch"/);

  assert.match(transport, /Timeout = TimeSpan\.FromSeconds\(10\)/);
  assert.match(transport, /OperationPollInterval = TimeSpan\.FromSeconds\(2\)/);
  assert.match(transport, /GetAsync\(/);
  assert.doesNotMatch(transport, /FromMinutes\(6\)/);

  assert.match(contract, /InvokeAsync<TResult>/);
  assert.match(contract, /InvokeWithProgressAsync<TResult>/);
  assert.match(contract, /BrowserOperationProgress/);
  assert.match(electron, /reportProgress/);
  assert.match(transport, /reportProgress\?\.Invoke\(progress\)/);
  assert.doesNotMatch(contract, /SendPromptAsync|SendWebRequestAsync|BrowserWebRequest|BrowserWebResponse/);
});

test("provider response lifetime is not capped by wall-clock deadlines", () => {
  const files = [
    path.join(root, "integrations", "Mezhs.Integrations.ChatGpt", "browser", "chatgpt.ts"),
    path.join(root, "integrations", "Mezhs.Integrations.Grok", "browser", "grok.ts"),
    path.join(root, "integrations", "Mezhs.Integrations.Gemini", "browser", "gemini.ts")
  ];

  for (const file of files) {
    const source = fs.readFileSync(file, "utf8");
    assert.doesNotMatch(source, /response timed out/i, file);
    assert.doesNotMatch(source, /CONVERSATION_POLL_ATTEMPTS/, file);
    assert.doesNotMatch(source, /responseDeadline\s*=|const deadline = Date\.now\(\) \+ 180000/, file);
  }
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

test("ChatGPT getModels follows the native picker instead of the raw catalog", async () => {
  const chatgpt = loadChatGptModule();
  const session = mockSession(async (url, options = {}) => {
    const target = new URL(String(url));
    if (target.pathname === "/api/auth/session")
      return jsonResponse({ accessToken: "token" });
    if (target.pathname === "/backend-api/models") {
      assert.equal(options.headers.Authorization, "Bearer token");
      assert.equal(target.searchParams.get("history_and_training_disabled"), "false");
      return jsonResponse({
        models: [
          { slug: "gpt-5-6-instant", title: "GPT-5.6 Instant" },
          { slug: "gpt-5-6-thinking", title: "GPT-5.6 Thinking" },
          { slug: "gpt-5-5-instant", title: "GPT-5.5 Instant" },
          { slug: "gpt-5-5-thinking", title: "GPT-5.5 Thinking" },
          { slug: "o3", title: "o3" },
          { slug: "gpt-5-3-mini", title: "GPT-5.3 Mini" },
          { slug: "gpt-5.6-luna-wm", title: "GPT-5.6 Luna" }
        ],
        versions: [
          {
            id: "5.6",
            display_text_for_intelligence: "GPT-5.6 Sol",
            slugs: ["gpt-5-6", "gpt-5-6-instant", "gpt-5-6-thinking"],
            intelligence_presets: [
              {
                title: "Instant",
                model_slug: "gpt-5-6-instant",
                lane: "instant",
                preset_type: "available"
              },
              {
                title: "Medium",
                model_slug: "gpt-5-6-thinking",
                lane: "thinking",
                thinking_effort: "standard",
                preset_type: "available"
              },
              {
                title: "High",
                model_slug: "gpt-5-6-thinking",
                lane: "thinking",
                thinking_effort: "extended",
                preset_type: "available"
              }
            ],
            enabled: true
          },
          {
            id: "5.5",
            display_text_for_intelligence: "GPT-5.5",
            slugs: ["gpt-5-5-instant", "gpt-5-5-thinking"],
            intelligence_presets: [
              {
                title: "Instant",
                model_slug: "gpt-5-5-instant",
                lane: "instant",
                preset_type: "available"
              },
              {
                title: "Medium",
                model_slug: "gpt-5-5-thinking",
                lane: "thinking",
                thinking_effort: "standard",
                preset_type: "available"
              },
              {
                title: "High",
                model_slug: "gpt-5-5-thinking",
                lane: "thinking",
                thinking_effort: "extended",
                preset_type: "available"
              }
            ],
            enabled: true
          },
          {
            id: "o3",
            display_text_for_intelligence: "o3",
            slugs: ["o3"],
            enabled: true
          },
          {
            id: "5.3",
            display_text_for_intelligence: "GPT-5.3 Mini",
            slugs: ["gpt-5-3-mini"],
            enabled: false
          }
        ]
      });
    }
    throw new Error(`Unexpected request ${target}`);
  });

  assert.deepEqual(await chatgpt.operations.getModels({ session }), [
    { id: "gpt-5-6-instant", name: "GPT-5.6 Sol · Instant" },
    {
      id: "gpt-5-6-thinking::thinking-effort=standard",
      name: "GPT-5.6 Sol · Medium"
    },
    {
      id: "gpt-5-6-thinking::thinking-effort=extended",
      name: "GPT-5.6 Sol · High"
    },
    { id: "gpt-5-5-instant", name: "GPT-5.5 · Instant" },
    {
      id: "gpt-5-5-thinking::thinking-effort=standard",
      name: "GPT-5.5 · Medium"
    },
    {
      id: "gpt-5-5-thinking::thinking-effort=extended",
      name: "GPT-5.5 · High"
    },
    { id: "o3", name: "o3" }
  ]);
});


function noUiWindow() {
  return new Proxy({}, {
    get(_target, name) {
      throw new Error(`ChatGPT account must not access browser UI: ${String(name)}`);
    }
  });
}

function directSession({ conversationId = "conv-test", projectId = null, conversationStatus = 200 } = {}) {
  const state = { postCount: 0, requests: [], posted: null };
  const session = mockSession(async (url, options = {}) => {
    const target = new URL(String(url));
    state.requests.push({ path: target.pathname, method: options.method || "GET" });
    if (target.pathname === "/api/auth/session")
      return jsonResponse({ accessToken: "token", user: { id: "account-test" } });
    if (target.pathname === "/backend-api/settings/user_last_used_model_config")
      return textResponse("");
    if (target.pathname === "/backend-api/conversation/init")
      return jsonResponse({ default_model_slug: "gpt-5-6-thinking" });
    if (target.pathname === "/backend-api/f/conversation/prepare") {
      assert.equal(options.method, "POST");
      return jsonResponse({ conduit_token: "prepared-by-server" });
    }
    if (target.pathname === "/backend-api/f/conversation" && options.method === "POST") {
      state.postCount++;
      state.posted = JSON.parse(options.body);
      assert.equal(options.headers["x-conduit-token"], "prepared-by-server");
      if (conversationStatus !== 200)
        return textResponse("Direct submission not allowed", conversationStatus);
      return textResponse(
        `data: {"conversation_id":"${conversationId}"}\n\ndata: [DONE]\n\n`,
        200,
        "text/event-stream"
      );
    }
    if (target.pathname === `/backend-api/conversation/${conversationId}`) {
      return jsonResponse(completedConversation(
        conversationId,
        projectId,
        "gpt-5-6-thinking",
        state.posted.messages[0].id
      ));
    }
    throw new Error(`Unexpected semantic request ${target}`);
  });
  return { session, state };
}

test("ChatGPT account sends project turns through semantic HTTP with the requested effort", async () => {
  const chatgpt = loadChatGptModule();
  const { session, state } = directSession({ projectId: "g-p-test" });
  const result = await chatgpt.operations.newChat({
    window: noUiWindow(), session,
    args: {
      prompt: "test project",
      model: "gpt-5-6-thinking::thinking-effort=extended",
      projectId: "g-p-test",
      files: []
    },
    sleep: async () => {}
  });
  assert.equal(state.postCount, 1);
  assert.equal(state.posted.model, "gpt-5-6-thinking");
  assert.equal(state.posted.thinking_effort, "extended");
  assert.equal(state.posted.conversation_mode.gizmo_id, "g-p-test");
  assert.equal(state.posted.messages[0].content.parts[0], "test project");
  assert.equal(result.conversationId, "conv-test");
  assert.equal(result.text, "answer");
});

test("ChatGPT account continuations target the known conversation without UI navigation", async () => {
  const chatgpt = loadChatGptModule();
  const { session, state } = directSession();
  const result = await chatgpt.operations.send({
    window: noUiWindow(), session,
    args: {
      conversationId: "conv-test",
      parentMessageId: "assistant-previous",
      prompt: "continue",
      files: []
    },
    sleep: async () => {}
  });
  assert.equal(state.postCount, 1);
  assert.equal(state.posted.conversation_id, "conv-test");
  assert.equal(state.posted.parent_message_id, "assistant-previous");
  assert.equal(result.text, "answer");
});

test("ChatGPT does not replay a turn when the backend rejects direct submission", async () => {
  const chatgpt = loadChatGptModule();
  const { session, state } = directSession({ conversationStatus: 403 });
  await assert.rejects(
    chatgpt.operations.send({
      window: noUiWindow(), session,
      args: { conversationId: "conv-test", parentMessageId: "old", prompt: "once", files: [] },
      sleep: async () => {}
    }),
    /HTTP 403/
  );
  assert.equal(state.postCount, 1);
  assert.equal(state.requests.filter(x => x.path === "/backend-api/conversation/conv-test").length, 0);
});

test("ChatGPT account production code has no DOM or native input submission", () => {
  const source = fs.readFileSync(
    path.join(root, "integrations", "Mezhs.Integrations.ChatGpt", "browser", "chatgpt.ts"),
    "utf8"
  );
  assert.doesNotMatch(source, /querySelector|document\.|contenteditable|focusNativeComposer|insertText\(|sendInputEvent\(|executeJavaScript\(/);
  assert.doesNotMatch(source, /Network\.requestWillBeSent|Network\.streamResourceContent/);
  assert.match(source, /postConversationTurn/);
  assert.match(source, /requestMessageId = randomUUID\(\)/);
});
