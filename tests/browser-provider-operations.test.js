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
  return {
    fetch,
    cookies: {
      get: async () => deviceId ? [{ value: deviceId }] : []
    }
  };
}

function nativeChatGptWindow(state) {
  class NativeDebugger extends EventEmitter {
    constructor() {
      super();
      this.attached = false;
    }
    isAttached() { return this.attached; }
    attach(version) {
      assert.equal(version, "1.3");
      this.attached = true;
    }
    detach() { this.attached = false; }
    async sendCommand(method) {
      if (method === "Network.enable")
        return {};
      if (method === "Network.getResponseBody")
        return { body: state.streamBody || "", base64Encoded: false };
      throw new Error(`Unexpected debugger command ${method}`);
    }
  }

  const debug = new NativeDebugger();
  let currentUrl = "https://chatgpt.com/";
  let composerText = "";
  let selectedAll = false;

  return {
    getBounds: () => ({ width: 1200, height: 850 }),
    loadURL: async url => {
      currentUrl = String(url);
      state.loadedUrl = currentUrl;
      composerText = String(state.draft || "");
      selectedAll = false;
    },
    webContents: {
      debugger: debug,
      executeJavaScript: async source => {
        assert.doesNotThrow(() => new Function(`return ${source};`));
        return { ok: true };
      },
      selectAll: () => {
        selectedAll = true;
        state.selectAllCalls = (state.selectAllCalls || 0) + 1;
      },
      insertText: async text => {
        const inserted = String(text).replace(/\r\n?/g, "\n");
        composerText = selectedAll
          ? inserted
          : composerText + inserted;
        selectedAll = false;
      },
      sendInputEvent: event => {
        if (event.type !== "keyDown" || event.keyCode !== "Enter")
          return;

        const url = new URL(currentUrl);
        const continuation = /^\/c\/([^/]+)$/.exec(url.pathname);
        const project = /^\/g\/(g-p-[^/]+)\/project$/.exec(url.pathname);
        const requestMessageId = `request-${(state.posts || 0) + 1}`;
        state.posts = (state.posts || 0) + 1;

        const body = {
          action: "next",
          model: state.model || "gpt-5-6-thinking",
          parent_message_id: continuation
            ? state.parentMessageId || "assistant-old"
            : "client-created-root",
          client_prepare_state: "success",
          supported_encodings: ["v1"],
          messages: [{
            id: requestMessageId,
            author: { role: "user" },
            content: { content_type: "text", parts: [composerText] }
          }],
          ...(state.effort ? { thinking_effort: state.effort } : {}),
          ...(continuation
            ? { conversation_id: decodeURIComponent(continuation[1]) }
            : {}),
          ...(project
            ? {
                conversation_mode: {
                  kind: "gizmo_interaction",
                  gizmo_id: decodeURIComponent(project[1])
                }
              }
            : {})
        };

        state.lastBody = body;
        state.onRequest?.(body);
        state.streamBody = state.stream?.(body) ??
          `data: {"conversation_id":"${state.conversationId}"}\n\ndata: [DONE]\n\n`;

        const requestId = `native-${state.posts}`;
        debug.emit("message", {}, "Network.requestWillBeSent", {
          requestId,
          request: {
            url: "https://chatgpt.com/backend-api/f/conversation",
            method: "POST",
            headers: {},
            postData: JSON.stringify(body)
          }
        });
        debug.emit("message", {}, "Network.loadingFinished", { requestId });
      }
    }
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

test("ChatGPT o3 newChat uses the native project composer and reports the served model", async () => {
  const chatgpt = loadChatGptModule();
  const state = {
    conversationId: "conv-1",
    projectId: "g-p-mezhs",
    model: "gpt-5-6-thinking",
    effort: null
  };

  const session = mockSession(async (url) => {
    const target = new URL(String(url));

    if (target.pathname === "/api/auth/session")
      return jsonResponse({ accessToken: "token" });

    if (target.pathname === "/backend-api/settings/user_last_used_model_config") {
      assert.equal(target.search, "?model_slug=o3");
      state.model = target.searchParams.get("model_slug");
      state.effort = target.searchParams.get("thinking_effort");
      return textResponse("");
    }

    if (target.pathname === "/backend-api/conversation/conv-1") {
      return jsonResponse(completedConversation(
        "conv-1",
        "g-p-mezhs",
        "o3",
        state.lastBody.messages[0].id,
        "o3"
      ));
    }

    throw new Error(`Unexpected request ${target}`);
  });

  const result = await chatgpt.operations.newChat({
    window: nativeChatGptWindow(state),
    page: { invoke: async () => { throw new Error("page operation not expected"); } },
    session,
    args: {
      prompt: "what model are you?",
      projectId: "g-p-mezhs",
      conversationId: null,
      parentMessageId: null,
      model: "o3",
      files: []
    },
    sleep: async () => {}
  });

  assert.equal(state.loadedUrl, "https://chatgpt.com/g/g-p-mezhs/project");
  assert.equal(state.lastBody.model, "o3");
  assert.equal("thinking_effort" in state.lastBody, false);
  assert.deepEqual(state.lastBody.conversation_mode, {
    kind: "gizmo_interaction",
    gizmo_id: "g-p-mezhs"
  });
  assert.equal(state.lastBody.messages[0].content.parts[0], "what model are you?");
  assert.equal("conversation_id" in state.lastBody, false);

  assert.equal(result.conversationId, "conv-1");
  assert.equal(result.parentMessageId, "assistant-1");
  assert.equal(result.projectId, "g-p-mezhs");
  assert.equal(result.text, "answer");
  assert.equal(result.model, "o3");
});

test("ChatGPT browser module delegates conversation security to the native frontend", () => {
  const source = fs.readFileSync(
    path.join(root, "integrations", "Mezhs.Integrations.ChatGpt", "browser", "chatgpt.ts"),
    "utf8"
  );
  assert.doesNotMatch(source, /CHATGPT_WIRE_MODEL/);
  assert.doesNotMatch(source, /"gpt-[^"]+":\s*"gpt-[^"]+"/);
  assert.doesNotMatch(source, /versionId\.toLowerCase\(\)/);
  assert.match(source, /selectAll\(\)/);
  assert.match(source, /insertText\(prompt\)/);
  assert.match(source, /sendInputEvent\(\{ type: "keyDown", keyCode: "Enter" \}\)/);
  assert.match(source, /Network\.requestWillBeSent/);
  assert.match(source, /Network\.getResponseBody/);
  assert.doesNotMatch(source, /nativeChatRequirementsHeaders/);
  assert.doesNotMatch(source, /conversation-small/);
  assert.doesNotMatch(source, /chat-requirements|Turnstile|Proof-Token|x-conduit-token/i);
  assert.doesNotMatch(source, /conversationPreparePayload|getConduitToken|webApiHeaders/);
  assert.doesNotMatch(source, /apiFetch\(session, token, API\.conversation/);
});

test("ChatGPT picker selections are verified on the native outgoing request", async () => {
  const chatgpt = loadChatGptModule();
  const selections = [
    { selected: undefined, model: "gpt-5-6-thinking", effort: null },
    { selected: "gpt-5-6-instant", model: "gpt-5-6-instant", effort: null },
    {
      selected: "gpt-5-6-thinking::thinking-effort=standard",
      model: "gpt-5-6-thinking",
      effort: "standard"
    },
    {
      selected: "gpt-5-6-thinking::thinking-effort=extended",
      model: "gpt-5-6-thinking",
      effort: "extended"
    },
    { selected: "o3", model: "o3", effort: null }
  ];

  for (const selection of selections) {
    const state = {
      conversationId: "conv-selection",
      model: "gpt-5-6-thinking",
      effort: null
    };
    const session = mockSession(async (url) => {
      const target = new URL(String(url));
      if (target.pathname === "/api/auth/session")
        return jsonResponse({ accessToken: "token" });
      if (target.pathname === "/backend-api/settings/user_last_used_model_config") {
        state.model = target.searchParams.get("model_slug");
        state.effort = target.searchParams.get("thinking_effort");
        assert.equal(state.model, selection.model);
        assert.equal(state.effort, selection.effort);
        return textResponse("");
      }
      if (target.pathname === "/backend-api/conversation/conv-selection") {
        return jsonResponse(completedConversation(
          "conv-selection",
          null,
          selection.model,
          state.lastBody.messages[0].id,
          selection.model
        ));
      }
      throw new Error(`Unexpected request ${target}`);
    });

    if (!selection.selected)
      state.model = selection.model;

    await chatgpt.operations.newChat({
      window: nativeChatGptWindow(state),
      session,
      args: { prompt: "test selection", model: selection.selected, files: [] },
      sleep: async () => {}
    });

    assert.equal(state.lastBody.model, selection.model, selection.selected);
    assert.equal(state.lastBody.thinking_effort ?? null, selection.effort, selection.selected);
  }
});

test("ChatGPT send continues the existing conversation through the native composer", async () => {
  const chatgpt = loadChatGptModule();
  const state = {
    conversationId: "conv-existing",
    model: "gpt-5-6-thinking",
    effort: null,
    parentMessageId: "assistant-old"
  };
  const session = mockSession(async (url) => {
    const target = new URL(String(url));

    if (target.pathname === "/api/auth/session")
      return jsonResponse({ accessToken: "token" });

    if (target.pathname === "/backend-api/conversation/conv-existing") {
      return jsonResponse(completedConversation(
        "conv-existing",
        "g-p-mezhs",
        "served-continuation",
        state.lastBody.messages[0].id
      ));
    }

    throw new Error(`Unexpected request ${target}`);
  });

  const result = await chatgpt.operations.send({
    window: nativeChatGptWindow(state),
    session,
    args: {
      prompt: "continue",
      conversationId: "conv-existing",
      parentMessageId: "assistant-old",
      files: []
    },
    sleep: async () => {}
  });

  assert.equal(state.loadedUrl, "https://chatgpt.com/c/conv-existing");
  assert.equal(state.lastBody.conversation_id, "conv-existing");
  assert.equal("conversation_mode" in state.lastBody, false);
  assert.equal(result.projectId, "g-p-mezhs");
  assert.equal(result.model, "served-continuation");
});

test("ChatGPT fails closed when the native outgoing request uses the wrong effort", async () => {
  const chatgpt = loadChatGptModule();
  const state = {
    conversationId: "conv-wrong-effort",
    model: "gpt-5-6-thinking",
    effort: "standard"
  };
  const session = mockSession(async (url) => {
    const target = new URL(String(url));
    if (target.pathname === "/api/auth/session")
      return jsonResponse({ accessToken: "token" });
    if (target.pathname === "/backend-api/settings/user_last_used_model_config") {
      // Simulate the frontend ignoring the requested preference after the PATCH.
      return textResponse("");
    }
    throw new Error(`Unexpected request ${target}`);
  });

  await assert.rejects(
    chatgpt.operations.newChat({
      window: nativeChatGptWindow(state),
      session,
      args: {
        prompt: "hello",
        model: "gpt-5-6-thinking::thinking-effort=extended",
        files: []
      },
      sleep: async () => {}
    }),
    /selected thinking effort 'standard' instead of 'extended'/
  );
});

test("ChatGPT rejects file input instead of falling back to the obsolete direct protocol", async () => {
  const chatgpt = loadChatGptModule();
  const state = { conversationId: "unused" };
  const session = mockSession(async (url) => {
    const target = new URL(String(url));
    if (target.pathname === "/api/auth/session")
      return jsonResponse({ accessToken: "token" });
    throw new Error(`Unexpected request ${target}`);
  });

  await assert.rejects(
    chatgpt.operations.newChat({
      window: nativeChatGptWindow(state),
      session,
      args: {
        prompt: "hello",
        files: [{ path: "unused", name: "unused.txt", contentType: "text/plain" }]
      },
      sleep: async () => {}
    }),
    /file input is temporarily unavailable/
  );
});

