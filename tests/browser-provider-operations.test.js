const test = require("node:test");
const assert = require("node:assert/strict");
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

function chatGptWindow(overrides = {}) {
  const {
    webContents: webContentsOverrides = {},
    ...windowOverrides
  } = overrides;
  return {
    getBounds: () => ({ width: 1200, height: 850 }),
    ...windowOverrides,
    webContents: {
      getUserAgent: () => "TestBrowser/1.0",
      executeJavaScript: async source => {
        assert.match(source, /conversation-small/);
        assert.match(source, /chatReq/);
        assert.match(source, /turnstileToken/);
        assert.match(source, /proofToken/);
        assert.match(source, /getEnforcementTokenSync/);
        assert.match(source, /getEnforcementToken/);
        assert.doesNotMatch(source, /cacheEnforcementToken|finalizeCandidates/);
        assert.doesNotThrow(() => new Function(`return ${source};`));
        return {
          requirementsToken: "sentinel",
          prepareToken: null,
          forceLogin: false,
          turnstileToken: "turnstile",
          proofToken: "proof",
          telemetry: "[1,null]"
        };
      },
      ...webContentsOverrides
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

test("ChatGPT o3 newChat follows the semantic web API protocol and reports the assistant model", async () => {
  const chatgpt = loadChatGptModule();
  let initHeaders;
  let initPayload;
  let prepareHeaders;
  let preparePayload;
  let conversationPayload;
  let conversationHeaders;

  const session = mockSession(async (url, options = {}) => {
    const target = new URL(String(url));

    if (target.pathname === "/api/auth/session")
      return jsonResponse({ accessToken: "token" });
    if (target.pathname === "/backend-api/settings/user_last_used_model_config") {
      assert.equal(target.search, "?model_slug=o3");
      return textResponse("");
    }

    if (target.pathname === "/backend-api/conversation/init") {
      initHeaders = options.headers;
      initPayload = JSON.parse(options.body);
      return jsonResponse({
        type: "conversation_detail_metadata",
        default_model_slug: "o3",
        intended_default_model_slug: "o3"
      });
    }

    if (target.pathname === "/backend-api/f/conversation/prepare") {
      prepareHeaders = options.headers;
      assert.equal(options.method, "POST");
      if (!options.body) {
        return jsonResponse({ detail: [
          { type: "missing", loc: ["body"], msg: "Field required", input: null },
          { type: "missing", loc: ["body"], msg: "Field required", input: null }
        ] }, 422);
      }
      preparePayload = JSON.parse(options.body);
      return jsonResponse({ status: "ok", conduit_token: "conduit" });
    }


    if (target.pathname === "/backend-api/f/conversation" && options.method === "POST") {
      conversationPayload = JSON.parse(options.body);
      conversationHeaders = options.headers;
      return textResponse('data: {"conversation_id":"conv-1"}\n\ndata: [DONE]\n\n', 200, "text/event-stream");
    }

    if (target.pathname === "/backend-api/conversation/conv-1")
      return jsonResponse(completedConversation(
        "conv-1",
        "g-p-mezhs",
        "o3",
        conversationPayload.messages[0].id,
        "gpt-5-5-mini"
      ));

    throw new Error(`Unexpected request ${target}`);
  }, "device-1");

  const result = await chatgpt.operations.newChat({
    window: chatGptWindow({
      loadURL: async () => {
        throw new Error("ChatGPT account send must not navigate the UI.");
      },
      webContents: { debugger: {} }
    }),
    page: {
      invoke: async () => {
        throw new Error("ChatGPT account send must not invoke page operations.");
      }
    },
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

  assert.deepEqual(initPayload, {
    gizmo_id: "g-p-mezhs",
    requested_default_model: null,
    conversation_id: null,
    timezone: Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC",
    timezone_offset_min: new Date().getTimezoneOffset(),
    conversation_origin: null
  });
  assert.equal(initHeaders["x-openai-web-frontend"], "core_web");
  assert.equal(initHeaders["x-openai-target-path"], "/backend-api/conversation/init");
  assert.equal(initHeaders["Oai-Device-Id"], "device-1");
  assert.ok(initHeaders["Oai-Session-Id"]);

  assert.equal(prepareHeaders["Content-Type"], "application/json");
  assert.equal("x-conduit-token" in prepareHeaders, false);
  assert.equal(prepareHeaders["x-openai-target-path"], "/backend-api/f/conversation/prepare");
  assert.ok(prepareHeaders["x-oai-turn-trace-id"]);
  assert.equal(preparePayload.action, "next");
  assert.equal(preparePayload.model, "o3");
  assert.equal(preparePayload.parent_message_id, "client-created-root");
  assert.deepEqual(preparePayload.conversation_mode, {
    kind: "gizmo_interaction",
    gizmo_id: "g-p-mezhs"
  });
  assert.equal(preparePayload.client_prepare_state, "none");
  assert.equal(preparePayload.client_prepare_dispatch, "debounced");
  assert.equal(preparePayload.client_prepare_source, "composer_editor_state");
  assert.equal(preparePayload.partial_query.id, conversationPayload.messages[0].id);
  assert.deepEqual(preparePayload.partial_query.author, { role: "user" });
  assert.deepEqual(preparePayload.partial_query.content, conversationPayload.messages[0].content);
  assert.deepEqual(preparePayload.supported_encodings, ["v1"]);
  assert.equal(preparePayload.supports_buffering, true);
  assert.deepEqual(preparePayload.local_function_names, ["local.continue_in_work"]);
  assert.deepEqual(preparePayload.client_contextual_info, {
    app_name: "chatgpt.com",
    has_web_push_capabilities: true,
    web_push_notification_permission: "default"
  });
  assert.equal("thinking_effort" in preparePayload, false);

  assert.deepEqual(conversationPayload.conversation_mode, {
    kind: "gizmo_interaction",
    gizmo_id: "g-p-mezhs"
  });
  assert.equal(conversationPayload.messages[0].content.parts.at(-1), "what model are you?");
  assert.equal(conversationPayload.messages[0].metadata.serialization_metadata.custom_symbol_offsets.length, 0);
  assert.equal("selected_github_repos" in conversationPayload.messages[0].metadata, false);
  assert.equal(conversationPayload.model, "o3");
  assert.equal(conversationPayload.parent_message_id, "client-created-root");
  assert.equal(conversationPayload.client_prepare_state, "success");
  assert.deepEqual(conversationPayload.supported_encodings, ["v1"]);
  assert.equal(conversationPayload.supports_buffering, true);
  assert.equal(conversationPayload.enable_message_followups, true);
  assert.equal(conversationPayload.messages[0].metadata.submission_mode, "manual_send");
  assert.equal("history_and_training_disabled" in conversationPayload, false);
  assert.equal(conversationPayload.force_parallel_switch, "auto");
  assert.deepEqual(conversationPayload.local_function_names, ["local.continue_in_work"]);
  assert.equal("thinking_effort" in conversationPayload, false);
  assert.equal("conversation_id" in conversationPayload, false);

  assert.equal(conversationHeaders["OpenAI-Sentinel-Chat-Requirements-Token"], "sentinel");
  assert.equal(conversationHeaders["OpenAI-Sentinel-Turnstile-Token"], "turnstile");
  assert.equal(conversationHeaders["OpenAI-Sentinel-Proof-Token"], "proof");
  assert.equal(conversationHeaders["OAI-Telemetry"], "[1,null]");
  assert.equal(conversationHeaders["x-conduit-token"], "conduit");
  assert.equal(conversationHeaders["x-openai-web-frontend"], "core_web");
  assert.equal(prepareHeaders["x-openai-web-frontend"], "core_web");
  assert.equal(initHeaders["Oai-Session-Id"], prepareHeaders["Oai-Session-Id"]);
  assert.equal(prepareHeaders["Oai-Session-Id"], conversationHeaders["Oai-Session-Id"]);
  assert.equal(conversationHeaders["x-oai-turn-trace-id"], prepareHeaders["x-oai-turn-trace-id"]);
  assert.equal(conversationHeaders["x-openai-target-path"], "/backend-api/f/conversation");
  assert.equal(conversationHeaders["Oai-Device-Id"], "device-1");

  assert.equal(result.conversationId, "conv-1");
  assert.equal(result.parentMessageId, "assistant-1");
  assert.equal(result.projectId, "g-p-mezhs");
  assert.equal(result.text, "answer");
  assert.equal(result.model, "o3");
});

test("ChatGPT carries native integrity-state observations across account API requests", async () => {
  const chatgpt = loadChatGptModule();
  const stateA = "ois1.header.1234567890abcdef.payloadA";
  const stateB = "ois1.header.abcdef1234567890.payloadB";
  const stateC = "ois1.header.fedcba0987654321.payloadC";
  let integrityState = stateA;
  let preferenceObservation;
  let prepareObservation;
  let conversationObservation;
  let requestMessageId;

  const session = {
    cookies: {
      async get({ name }) {
        if (name === "__Secure-oai-is")
          return integrityState ? [{ value: integrityState }] : [];
        if (name === "oai-did")
          return [{ value: "device-1" }];
        return [];
      },
      async set(cookie) {
        if (cookie.name === "__Secure-oai-is")
          integrityState = cookie.value;
      }
    },
    async fetch(url, options = {}) {
      const target = new URL(String(url));

      if (target.pathname === "/api/auth/session")
        return jsonResponse({ accessToken: "token" });

      if (target.pathname === "/backend-api/settings/user_last_used_model_config") {
        preferenceObservation = options.headers["X-OAI-IS-Client-Observation"];
        return new Response("", {
          status: 200,
          headers: { "x-oai-is-update": stateB }
        });
      }

      if (target.pathname === "/backend-api/f/conversation/prepare") {
        prepareObservation = options.headers["X-OAI-IS-Client-Observation"];
        return jsonResponse({ conduit_token: "conduit" });
      }


      if (target.pathname === "/backend-api/f/conversation" && options.method === "POST") {
        conversationObservation = options.headers["X-OAI-IS-Client-Observation"];
        const body = JSON.parse(options.body);
        requestMessageId = body.messages[0].id;
        return new Response(
          'data: {"conversation_id":"conv-integrity"}\n\ndata: [DONE]\n\n',
          {
            status: 200,
            headers: {
              "content-type": "text/event-stream",
              "x-oai-is-update": stateC
            }
          }
        );
      }

      if (target.pathname === "/backend-api/conversation/conv-integrity")
        return jsonResponse(completedConversation(
          "conv-integrity",
          null,
          "o3",
          requestMessageId,
          "o3"
        ));

      throw new Error(`Unexpected request ${target}`);
    }
  };

  const result = await chatgpt.operations.newChat({
    window: chatGptWindow(),
    session,
    args: { prompt: "integrity", model: "o3", files: [] },
    sleep: async () => {}
  });

  assert.equal(preferenceObservation, "v1.r.p.1234567890abcdef");
  assert.equal(prepareObservation, "v1.r.p.abcdef1234567890");
  assert.equal(conversationObservation, "v1.s.p.abcdef1234567890");
  assert.equal(integrityState, stateC);
  assert.equal(result.text, "answer");
});

test("ChatGPT browser module does not hardcode provider model-id rewrites or version classifiers", () => {
  const source = fs.readFileSync(
    path.join(root, "integrations", "Mezhs.Integrations.ChatGpt", "browser", "chatgpt.ts"),
    "utf8"
  );
  assert.doesNotMatch(source, /CHATGPT_WIRE_MODEL/);
  assert.doesNotMatch(source, /"gpt-[^"]+":\s*"gpt-[^"]+"/);
  assert.doesNotMatch(source, /versionId\.toLowerCase\(\)/);
  assert.match(source, /nativeChatRequirementsHeaders/);
  assert.match(source, /conversation-small/);
  assert.match(source, /chatReq/);
  assert.match(source, /turnstileToken/);
  assert.match(source, /proofToken/);
  assert.match(source, /getEnforcementTokenSync/);
  assert.match(source, /getEnforcementToken/);
  assert.doesNotMatch(source, /cacheEnforcementToken|finalizeCandidates/);
  assert.doesNotMatch(source, /sentinelProofToken|solveSentinelProof|sha3_512|KECCAK_/);
  assert.doesNotMatch(source, /sentinel\/chat-requirements\/prepare/);
});

test("ChatGPT picker selections are sent without model-specific rewrites", async () => {
  const chatgpt = loadChatGptModule();
  const selections = [
    { selected: undefined, model: "auto", effort: null },
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
    { selected: "gpt-5-5-instant", model: "gpt-5-5-instant", effort: null },
    {
      selected: "gpt-5-5-thinking::thinking-effort=standard",
      model: "gpt-5-5-thinking",
      effort: "standard"
    },
    {
      selected: "gpt-5-5-thinking::thinking-effort=extended",
      model: "gpt-5-5-thinking",
      effort: "extended"
    },
    { selected: "o3", model: "o3", effort: null }
  ];

  for (const selection of selections) {
    let preparePayload;
    let conversationPayload;
    const session = mockSession(async (url, options = {}) => {
      const target = new URL(String(url));
      if (target.pathname === "/api/auth/session")
        return jsonResponse({ accessToken: "token" });
      if (target.pathname === "/backend-api/settings/user_last_used_model_config") {
        assert.equal(target.searchParams.get("model_slug"), selection.model);
        assert.equal(target.searchParams.get("thinking_effort"), selection.effort);
        return textResponse("");
      }
      if (target.pathname === "/backend-api/f/conversation/prepare") {
        preparePayload = JSON.parse(options.body);
        return jsonResponse({ conduit_token: "conduit" });
      }

      if (target.pathname === "/backend-api/f/conversation" && options.method === "POST") {
        conversationPayload = JSON.parse(options.body);
        return textResponse('data: {"conversation_id":"conv-selection"}\n\n', 200, "text/event-stream");
      }
      if (target.pathname === "/backend-api/conversation/conv-selection")
        return jsonResponse(completedConversation(
          "conv-selection",
          null,
          selection.model,
          conversationPayload.messages[0].id,
          selection.model
        ));
      throw new Error(`Unexpected request ${target}`);
    });

    await chatgpt.operations.newChat({
      window: chatGptWindow(),
      session,
      args: { prompt: "test selection", model: selection.selected, files: [] },
      sleep: async () => {}
    });

    assert.equal(preparePayload.model, selection.model, selection.selected);
    assert.equal(conversationPayload.model, selection.model, selection.selected);
    assert.equal(preparePayload.thinking_effort ?? null, selection.effort, selection.selected);
    assert.equal(conversationPayload.thinking_effort ?? null, selection.effort, selection.selected);
  }
});

test("ChatGPT send continues the existing conversation through the current transport", async () => {
  const chatgpt = loadChatGptModule();
  let conversationPayload;
  const session = mockSession(async (url, options = {}) => {
    const target = new URL(String(url));

    if (target.pathname === "/api/auth/session")
      return jsonResponse({ accessToken: "token" });
    if (target.pathname === "/backend-api/f/conversation/prepare")
      return jsonResponse({ conduit_token: "conduit" });


    if (target.pathname === "/backend-api/f/conversation" && options.method === "POST") {
      conversationPayload = JSON.parse(options.body);
      return textResponse('data: {"conversation_id":"conv-existing"}\n\n', 200, "text/event-stream");
    }

    if (target.pathname === "/backend-api/conversation/conv-existing")
      return jsonResponse(completedConversation(
        "conv-existing",
        "g-p-mezhs",
        "served-continuation",
        conversationPayload.messages[0].id
      ));

    throw new Error(`Unexpected request ${target}`);
  });

  const result = await chatgpt.operations.send({
    window: chatGptWindow(),
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
  assert.equal(conversationPayload.model, "auto");
  assert.equal("conversation_mode" in conversationPayload, false);
  assert.equal(result.projectId, "g-p-mezhs");
  assert.equal(result.model, "served-continuation");
});

test("ChatGPT account send fails closed when native chat requirements are unavailable", async () => {
  const chatgpt = loadChatGptModule();
  let conversationCalled = false;
  const session = mockSession(async (url) => {
    const target = new URL(String(url));
    if (target.pathname === "/api/auth/session")
      return jsonResponse({ accessToken: "token" });
    if (target.pathname === "/backend-api/conversation/init")
      return jsonResponse({
        type: "conversation_detail_metadata",
        default_model_slug: "gpt-5-6-thinking"
      });
    if (target.pathname === "/backend-api/f/conversation/prepare")
      return jsonResponse({ conduit_token: "conduit" });
    if (target.pathname === "/backend-api/f/conversation") {
      conversationCalled = true;
      return textResponse("unexpected");
    }
    throw new Error(`Unexpected request ${target}`);
  });

  const error = await chatgpt.operations.newChat({
    window: chatGptWindow({
      webContents: {
        executeJavaScript: async () => {
          throw new Error("native ChatGPT chat requirements unavailable");
        }
      }
    }),
    session,
    args: { prompt: "hello", projectId: "g-p-mezhs", files: [] },
    sleep: async () => {}
  }).then(() => null, caught => caught);

  assert.equal(error?.message, "native ChatGPT chat requirements unavailable");
  assert.equal(conversationCalled, false);
});
