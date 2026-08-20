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
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "mezhs-chatgpt-model-prepare-"));
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

test("ChatGPT prepares the selected model with the pending user query", async () => {
  const chatgpt = loadChatGptModule();
  let preparePayload;
  let conversationPayload;

  const session = {
    cookies: { get: async () => [] },
    fetch: async (url, options = {}) => {
      const target = new URL(String(url));
      if (target.pathname === "/api/auth/session")
        return jsonResponse({ accessToken: "token" });
      if (target.pathname === "/backend-api/f/conversation/prepare") {
        preparePayload = JSON.parse(options.body);
        return jsonResponse({ conduit_token: "conduit" });
      }
      if (target.pathname === "/backend-api/sentinel/chat-requirements/prepare")
        return jsonResponse({ prepare_token: "prepared" });
      if (target.pathname === "/backend-api/sentinel/chat-requirements/finalize")
        return jsonResponse({ token: "sentinel" });
      if (target.pathname === "/backend-api/f/conversation" && options.method === "POST") {
        conversationPayload = JSON.parse(options.body);
        return textResponse('data: {"conversation_id":"conv-model"}\n\n', 200, "text/event-stream");
      }
      if (target.pathname === "/backend-api/conversation/conv-model") {
        const requestId = conversationPayload.messages[0].id;
        return jsonResponse({
          conversation_id: "conv-model",
          current_node: "assistant-1",
          mapping: {
            "assistant-1": {
              parent: "request-1",
              message: {
                id: "assistant-1",
                author: { role: "assistant" },
                status: "finished_successfully",
                content: { parts: ["answer"] },
                metadata: { model_slug: "gpt-5-6-thinking" }
              }
            },
            "request-1": {
              parent: null,
              message: {
                id: requestId,
                author: { role: "user" },
                status: "finished_successfully",
                content: { parts: ["which model?"] },
                metadata: {}
              }
            }
          }
        });
      }
      throw new Error(`Unexpected request ${target}`);
    }
  };

  await chatgpt.operations.newChat({
    window: { webContents: { getUserAgent: () => "TestBrowser/1.0" } },
    session,
    args: {
      prompt: "which model?",
      model: "gpt-5-6-thinking::thinking-effort=extended",
      files: []
    },
    sleep: async () => {}
  });

  assert.equal(preparePayload.model, "gpt-5-6-thinking");
  assert.equal(preparePayload.thinking_effort, "extended");
  assert.equal(preparePayload.client_prepare_state, "success");
  assert.equal(preparePayload.partial_query.id, conversationPayload.messages[0].id);
  assert.deepEqual(preparePayload.partial_query.author, { role: "user" });
  assert.deepEqual(preparePayload.partial_query.content, {
    content_type: "text",
    parts: ["which model?"]
  });
  assert.equal(conversationPayload.client_prepare_state, "success");
  assert.equal(conversationPayload.model, "gpt-5-6-thinking");
  assert.equal(conversationPayload.thinking_effort, "extended");
});
