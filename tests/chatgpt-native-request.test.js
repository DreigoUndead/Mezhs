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
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "mezhs-chatgpt-native-"));
  const file = path.join(directory, "chatgpt.cjs");
  fs.writeFileSync(file, source);
  return require(file);
}

function jsonResponse(value) {
  return new Response(JSON.stringify(value), {
    status: 200,
    headers: { "content-type": "application/json" }
  });
}

class FakeDebugger extends EventEmitter {
  constructor() {
    super();
    this.attached = false;
    this.continued = null;
  }

  isAttached() {
    return this.attached;
  }

  attach() {
    this.attached = true;
  }

  detach() {
    this.attached = false;
  }

  async sendCommand(method, args = {}) {
    if (method === "Fetch.continueRequest") this.continued = args;
  }
}

test("ChatGPT account send preserves the native request and changes only model selection", async () => {
  const chatgpt = loadChatGptModule();
  const browserDebugger = new FakeDebugger();
  let currentUrl = "https://chatgpt.com/";
  let sessionCalls = [];

  const session = {
    fetch: async url => {
      const target = new URL(String(url));
      sessionCalls.push(target.pathname);
      if (target.pathname === "/api/auth/session")
        return jsonResponse({ accessToken: "token" });
      if (target.pathname === "/backend-api/conversation/native-conversation") {
        return jsonResponse({
          conversation_id: "native-conversation",
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
                id: "native-message",
                author: { role: "user" },
                status: "finished_successfully",
                content: { parts: ["what model are you?"] },
                metadata: { resolved_model_slug: "gpt-5-6-thinking" }
              }
            }
          }
        });
      }
      throw new Error(`Unexpected session request ${target}`);
    }
  };

  const window = {
    loadURL: async url => { currentUrl = url; },
    webContents: {
      debugger: browserDebugger,
      getURL: () => currentUrl
    }
  };

  const nativeBody = {
    action: "next",
    messages: [{
      id: "native-message",
      author: { role: "user" },
      content: { content_type: "text", parts: ["what model are you?"] }
    }],
    model: "auto",
    parent_message_id: "client-created-root",
    client_prepare_state: "success",
    system_hints: [{ native: true }],
    client_contextual_info: { native_field: "preserve-me" },
    future_field_mezhs_does_not_know: { value: 42 }
  };

  const page = {
    invoke: async (operation, args) => {
      assert.equal(operation, "submitPrompt");
      assert.equal(args.prompt, "what model are you?");
      browserDebugger.emit("message", {}, "Fetch.requestPaused", {
        requestId: "request-1",
        request: {
          url: "https://chatgpt.com/backend-api/f/conversation",
          postData: JSON.stringify(nativeBody),
          headers: {
            Authorization: "Bearer native-token",
            "X-OAI-IS": "native-is",
            "OAI-Client-Version": "native-client",
            "Content-Length": "123"
          }
        }
      });
      currentUrl = "https://chatgpt.com/c/native-conversation";
      await new Promise(resolve => setImmediate(resolve));
    }
  };

  const result = await chatgpt.operations.newChat({
    window,
    session,
    page,
    args: {
      prompt: "what model are you?",
      model: "gpt-5-6-thinking::thinking-effort=extended",
      files: []
    },
    sleep: async () => {}
  });

  const forwarded = JSON.parse(
    Buffer.from(browserDebugger.continued.postData, "base64").toString("utf8")
  );
  assert.equal(forwarded.model, "gpt-5-6-thinking");
  assert.equal(forwarded.thinking_effort, "extended");
  assert.deepEqual(forwarded.system_hints, nativeBody.system_hints);
  assert.deepEqual(forwarded.client_contextual_info, nativeBody.client_contextual_info);
  assert.deepEqual(
    forwarded.future_field_mezhs_does_not_know,
    nativeBody.future_field_mezhs_does_not_know
  );
  assert.ok(browserDebugger.continued.headers.some(header =>
    header.name === "X-OAI-IS" && header.value === "native-is"
  ));
  assert.ok(browserDebugger.continued.headers.some(header =>
    header.name === "OAI-Client-Version" && header.value === "native-client"
  ));
  assert.equal(browserDebugger.continued.headers.some(header =>
    header.name.toLowerCase() === "content-length"
  ), false);
  assert.deepEqual(sessionCalls, [
    "/api/auth/session",
    "/backend-api/conversation/native-conversation"
  ]);
  assert.equal(result.model, "gpt-5-6-thinking");
});
