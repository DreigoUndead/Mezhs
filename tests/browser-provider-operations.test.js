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

function assertProofToken(token, seed, difficulty) {
  const prefix = "gAAAAAB";
  assert.match(token, /^gAAAAAB/);
  const encoded = token.slice(prefix.length);
  const config = JSON.parse(Buffer.from(encoded, "base64").toString("utf8"));
  assert.equal(config.length, 18);

  // Plain Node independently verifies the SHA3 result. Electron's embedded
  // crypto backend does not expose SHA3, so its run verifies that the provider
  // itself no longer depends on that runtime capability.
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

test("ChatGPT newChat completes with the live Sentinel challenge bundle", async () => {
  const chatgpt = loadChatGptModule();
  const seed = "0.559779845730002";
  const difficulty = "ffffff";
  let requirementsPayload;
  let conversationPayload;
  let conversationHeaders;

  const session = mockSession(async (url, options = {}) => {
    const target = new URL(String(url));

    if (target.pathname === "/api/auth/session")
      return jsonResponse({ accessToken: "token" });

    if (target.pathname === "/backend-api/sentinel/chat-requirements") {
      requirementsPayload = JSON.parse(options.body);
      return jsonResponse({
        token: "sentinel",
        proofofwork: { required: true, seed, difficulty },
        turnstile: { required: true, dx: "turnstile-challenge" },
        so: { required: true, collector_dx: "collector", snapshot_dx: "snapshot" }
      });
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
    window: { webContents: { getUserAgent: () => "TestBrowser/1.0" } },
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

  assert.match(requirementsPayload.p, /^gAAAAAC/);
  assert.equal("conversation_mode_kind" in requirementsPayload, false);
  assert.deepEqual(conversationPayload.conversation_mode, {
    kind: "gizmo_interaction",
    gizmo_id: "g-p-mezhs"
  });
  assert.equal(conversationPayload.messages[0].content.parts.at(-1), "hello");
  assert.equal("conversation_id" in conversationPayload, false);
  assert.equal(conversationHeaders["Openai-Sentinel-Chat-Requirements-Token"], "sentinel");
  assert.equal(conversationHeaders["Oai-Device-Id"], "device-1");
  assertProofToken(conversationHeaders["Openai-Sentinel-Proof-Token"], seed, difficulty);
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

test("ChatGPT rejects an invalid proof-of-work challenge before sending", async () => {
  const chatgpt = loadChatGptModule();
  let conversationCalled = false;
  const session = mockSession(async (url) => {
    const target = new URL(String(url));
    if (target.pathname === "/api/auth/session")
      return jsonResponse({ accessToken: "token" });
    if (target.pathname === "/backend-api/sentinel/chat-requirements")
      return jsonResponse({ token: "sentinel", proofofwork: { required: true } });
    if (target.pathname === "/backend-api/conversation") {
      conversationCalled = true;
      return textResponse("unexpected");
    }
    throw new Error(`Unexpected request ${target}`);
  });

  const error = await chatgpt.operations.newChat({
    session,
    args: { prompt: "hello", projectId: "g-p-mezhs", files: [] },
    sleep: async () => {}
  }).then(() => null, caught => caught);

  assert.equal(error?.message, "ChatGPT returned an invalid Sentinel proof-of-work challenge.");
  assert.equal(conversationCalled, false);
});
