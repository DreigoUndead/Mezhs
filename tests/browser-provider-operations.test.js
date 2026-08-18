const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { createHash, getHashes } = require("node:crypto");

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

function completedConversation(conversationId, projectId = null, model = "served-test") {
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
          content: { parts: ["answer"] },
          metadata: { model_slug: model }
        }
      }
    }
  };
}

function assertProofToken(token, seed, difficulty) {
  const prefix = "gAAAAAB";
  assert.match(token, /^gAAAAAB/);
  const encoded = token.slice(prefix.length);
  const config = JSON.parse(Buffer.from(encoded, "base64").toString("utf8"));
  assert.equal(config.length, 18);

  if (!getHashes().includes("sha3-512")) return;

  const digest = createHash("sha3-512").update(seed).update(encoded).digest();
  const target = Buffer.from(difficulty, "hex");
  assert.ok(digest.subarray(0, target.length).compare(target) < 0);
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

test("ChatGPT getModels discovers account models and leaves provider default to MEŽS", async () => {
  const chatgpt = loadChatGptModule();
  const session = mockSession(async (url, options = {}) => {
    const target = new URL(String(url));
    if (target.pathname === "/api/auth/session")
      return jsonResponse({ accessToken: "token" });
    if (target.pathname === "/backend-api/models") {
      assert.equal(options.headers.Authorization, "Bearer token");
      assert.equal(target.searchParams.get("history_and_training_disabled"), "false");
      return jsonResponse({ models: [
        { slug: "auto", title: "Auto" },
        { slug: "gpt-test", title: "GPT Test" },
        { id: "reasoning-test", display_name: "Reasoning Test" },
        { slug: "gpt-test", title: "Duplicate" }
      ] });
    }
    throw new Error(`Unexpected request ${target}`);
  });

  assert.deepEqual(await chatgpt.operations.getModels({ session }), [
    { id: "gpt-test", name: "GPT Test" },
    { id: "reasoning-test", name: "Reasoning Test" }
  ]);
});

test("ChatGPT o3 newChat follows the current native web conversation protocol", async () => {
  const chatgpt = loadChatGptModule();
  const seed = "0.559779845730002";
  const difficulty = "ffffff";
  let prepareHeaders;
  let sentinelPreparePayload;
  let sentinelFinalizePayload;
  let conversationPayload;
  let conversationHeaders;

  const session = mockSession(async (url, options = {}) => {
    const target = new URL(String(url));

    if (target.pathname === "/api/auth/session")
      return jsonResponse({ accessToken: "token" });

    if (target.pathname === "/backend-api/f/conversation/prepare") {
      prepareHeaders = options.headers;
      assert.equal(options.method, "POST");
      assert.equal(options.body, undefined);
      return jsonResponse({ status: "ok", conduit_token: "conduit" });
    }

    if (target.pathname === "/backend-api/sentinel/chat-requirements/prepare") {
      sentinelPreparePayload = JSON.parse(options.body);
      return jsonResponse({
        prepare_token: "prepared",
        proofofwork: { required: true, seed, difficulty },
        turnstile: { required: true, dx: "turnstile-challenge" }
      });
    }

    if (target.pathname === "/backend-api/sentinel/chat-requirements/finalize") {
      sentinelFinalizePayload = JSON.parse(options.body);
      return jsonResponse({ token: "sentinel", expire_after: 540 });
    }

    if (target.pathname === "/backend-api/f/conversation" && options.method === "POST") {
      conversationPayload = JSON.parse(options.body);
      conversationHeaders = options.headers;
      return textResponse('data: {"conversation_id":"conv-1"}\n\ndata: [DONE]\n\n', 200, "text/event-stream");
    }

    if (target.pathname === "/backend-api/conversation/conv-1")
      return jsonResponse(completedConversation("conv-1", "g-p-mezhs", "o3"));

    throw new Error(`Unexpected request ${target}`);
  }, "device-1");

  const result = await chatgpt.operations.newChat({
    window: {
      getBounds: () => ({ width: 1200, height: 850 }),
      webContents: { getUserAgent: () => "TestBrowser/1.0" }
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

  assert.match(sentinelPreparePayload.p, /^gAAAAAC/);
  assert.equal(sentinelFinalizePayload.prepare_token, "prepared");
  assertProofToken(sentinelFinalizePayload.proofofwork, seed, difficulty);

  assert.equal(prepareHeaders["x-conduit-token"], "no-token");
  assert.equal(prepareHeaders["x-openai-target-path"], "/backend-api/f/conversation/prepare");
  assert.ok(prepareHeaders["x-oai-turn-trace-id"]);

  assert.deepEqual(conversationPayload.conversation_mode, {
    kind: "gizmo_interaction",
    gizmo_id: "g-p-mezhs"
  });
  assert.equal(conversationPayload.messages[0].content.parts.at(-1), "what model are you?");
  assert.equal(conversationPayload.messages[0].metadata.serialization_metadata.custom_symbol_offsets.length, 0);
  assert.equal(conversationPayload.model, "o3");
  assert.equal(conversationPayload.parent_message_id, "client-created-root");
  assert.equal(conversationPayload.client_prepare_state, "none");
  assert.deepEqual(conversationPayload.supported_encodings, ["v1"]);
  assert.equal(conversationPayload.supports_buffering, true);
  assert.equal(conversationPayload.enable_message_followups, true);
  assert.equal(conversationPayload.force_parallel_switch, "auto");
  assert.equal("thinking_effort" in conversationPayload, false);
  assert.equal("conversation_id" in conversationPayload, false);

  assert.equal(conversationHeaders["openai-sentinel-chat-requirements-token"], "sentinel");
  assert.equal(conversationHeaders["x-conduit-token"], "conduit");
  assert.equal(conversationHeaders["x-oai-turn-trace-id"], prepareHeaders["x-oai-turn-trace-id"]);
  assert.equal(conversationHeaders["x-openai-target-path"], "/backend-api/f/conversation");
  assert.equal(conversationHeaders["Oai-Device-Id"], "device-1");
  assertProofToken(conversationHeaders["openai-sentinel-proof-token"], seed, difficulty);

  assert.equal(result.conversationId, "conv-1");
  assert.equal(result.parentMessageId, "assistant-1");
  assert.equal(result.projectId, "g-p-mezhs");
  assert.equal(result.text, "answer");
  assert.equal(result.model, "o3");
});

test("ChatGPT thinking model sends extended thinking effort", async () => {
  const chatgpt = loadChatGptModule();
  let conversationPayload;
  const session = mockSession(async (url, options = {}) => {
    const target = new URL(String(url));
    if (target.pathname === "/api/auth/session")
      return jsonResponse({ accessToken: "token" });
    if (target.pathname === "/backend-api/f/conversation/prepare")
      return jsonResponse({ conduit_token: "conduit" });
    if (target.pathname === "/backend-api/sentinel/chat-requirements/prepare")
      return jsonResponse({ prepare_token: "prepared" });
    if (target.pathname === "/backend-api/sentinel/chat-requirements/finalize")
      return jsonResponse({ token: "sentinel" });
    if (target.pathname === "/backend-api/f/conversation" && options.method === "POST") {
      conversationPayload = JSON.parse(options.body);
      return textResponse('data: {"conversation_id":"conv-thinking"}\n\n', 200, "text/event-stream");
    }
    if (target.pathname === "/backend-api/conversation/conv-thinking")
      return jsonResponse(completedConversation("conv-thinking", null, "gpt-5-5-thinking"));
    throw new Error(`Unexpected request ${target}`);
  });

  await chatgpt.operations.newChat({
    window: { webContents: { getUserAgent: () => "TestBrowser/1.0" } },
    session,
    args: { prompt: "think", model: "gpt-5-5-thinking", files: [] },
    sleep: async () => {}
  });

  assert.equal(conversationPayload.thinking_effort, "extended");
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
    if (target.pathname === "/backend-api/sentinel/chat-requirements/prepare")
      return jsonResponse({ prepare_token: "prepared" });
    if (target.pathname === "/backend-api/sentinel/chat-requirements/finalize")
      return jsonResponse({ token: "sentinel" });

    if (target.pathname === "/backend-api/f/conversation" && options.method === "POST") {
      conversationPayload = JSON.parse(options.body);
      return textResponse('data: {"conversation_id":"conv-existing"}\n\n', 200, "text/event-stream");
    }

    if (target.pathname === "/backend-api/conversation/conv-existing")
      return jsonResponse(completedConversation("conv-existing", "g-p-mezhs", "served-continuation"));

    throw new Error(`Unexpected request ${target}`);
  });

  const result = await chatgpt.operations.send({
    window: { webContents: { getUserAgent: () => "TestBrowser/1.0" } },
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

test("ChatGPT rejects an invalid proof-of-work challenge before finalize/send", async () => {
  const chatgpt = loadChatGptModule();
  let finalizeCalled = false;
  let conversationCalled = false;
  const session = mockSession(async (url) => {
    const target = new URL(String(url));
    if (target.pathname === "/api/auth/session")
      return jsonResponse({ accessToken: "token" });
    if (target.pathname === "/backend-api/f/conversation/prepare")
      return jsonResponse({ conduit_token: "conduit" });
    if (target.pathname === "/backend-api/sentinel/chat-requirements/prepare")
      return jsonResponse({ prepare_token: "prepared", proofofwork: { required: true } });
    if (target.pathname === "/backend-api/sentinel/chat-requirements/finalize") {
      finalizeCalled = true;
      return jsonResponse({ token: "unexpected" });
    }
    if (target.pathname === "/backend-api/f/conversation") {
      conversationCalled = true;
      return textResponse("unexpected");
    }
    throw new Error(`Unexpected request ${target}`);
  });

  const error = await chatgpt.operations.newChat({
    window: { webContents: { getUserAgent: () => "TestBrowser/1.0" } },
    session,
    args: { prompt: "hello", projectId: "g-p-mezhs", files: [] },
    sleep: async () => {}
  }).then(() => null, caught => caught);

  assert.equal(error?.message, "ChatGPT returned an invalid Sentinel proof-of-work challenge.");
  assert.equal(finalizeCalled, false);
  assert.equal(conversationCalled, false);
});
