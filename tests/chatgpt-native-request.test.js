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

test("ChatGPT project send applies requested model to the native request", async () => {
  const chatgpt = loadChatGptModule();
  const browserDebugger = new FakeDebugger();
  const sessionCalls = [];
  const events = [];
  let currentUrl = "https://chatgpt.com/";

  const session = {
    fetch: async (url, options = {}) => {
      const target = new URL(String(url));
      sessionCalls.push({
        method: options.method || "GET",
        path: target.pathname,
        search: target.search
      });
      if (target.pathname === "/api/auth/session")
        return jsonResponse({ accessToken: "token", user: { id: "account-1" } });
      if (target.pathname === "/backend-api/settings/user_last_used_model_config") {
        events.push("preference");
        assert.equal(options.headers?.["ChatGPT-Account-Id"], "account-1");
        return new Response("", { status: 200 });
      }
      if (target.pathname === "/backend-api/conversation/native-conversation") {
        return jsonResponse({
          conversation_id: "native-conversation",
          gizmo_id: "g-p-project",
          current_node: "assistant-1",
          mapping: {
            "assistant-1": {
              parent: "request-1",
              message: {
                id: "assistant-1",
                author: { role: "assistant" },
                status: "finished_successfully",
                content: { parts: ["answer"] },
                metadata: { model_slug: "gpt-5-5-thinking" }
              }
            },
            "request-1": {
              parent: null,
              message: {
                id: "native-message",
                author: { role: "user" },
                status: "finished_successfully",
                content: { parts: ["what model are you?"] },
                metadata: { resolved_model_slug: "gpt-5-5-thinking" }
              }
            }
          }
        });
      }
      throw new Error(`Unexpected session request ${target}`);
    }
  };

  const window = {
    loadURL: async url => {
      events.push("load");
      assert.equal(url, "https://chatgpt.com/g/g-p-project/project");
      currentUrl = url;
    },
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
    model: "gpt-5-6-thinking",
    thinking_effort: "extended",
    conversation_mode: { kind: "gizmo_interaction", gizmo_id: "g-p-project" },
    parent_message_id: "client-created-root",
    client_prepare_state: "success",
    system_hints: [{ native: true }],
    client_contextual_info: { native_field: "preserve-me" },
    future_field_mezhs_does_not_know: { value: 42 }
  };

  const page = {
    invoke: async (operation, args) => {
      events.push("send");
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
      currentUrl = "https://chatgpt.com/g/g-p-project/c/native-conversation";
      await new Promise(resolve => setImmediate(resolve));
    }
  };

  const result = await chatgpt.operations.newChat({
    window,
    session,
    page,
    args: {
      prompt: "what model are you?",
      model: "gpt-5-5-thinking::thinking-effort=standard",
      projectId: "g-p-project",
      files: []
    },
    sleep: async () => {}
  });

  assert.deepEqual(events.slice(0, 3), ["load", "preference", "send"]);
  assert.equal(browserDebugger.continued.requestId, "request-1");
  const continuedBody = JSON.parse(
    Buffer.from(browserDebugger.continued.postData, "base64").toString("utf8")
  );
  assert.equal(continuedBody.model, "gpt-5-5-thinking");
  assert.equal(continuedBody.thinking_effort, "standard");
  assert.deepEqual(continuedBody.system_hints, nativeBody.system_hints);
  assert.deepEqual(continuedBody.client_contextual_info, nativeBody.client_contextual_info);
  assert.deepEqual(
    continuedBody.future_field_mezhs_does_not_know,
    nativeBody.future_field_mezhs_does_not_know
  );
  assert.deepEqual(sessionCalls, [
    { method: "GET", path: "/api/auth/session", search: "" },
    {
      method: "PATCH",
      path: "/backend-api/settings/user_last_used_model_config",
      search: "?model_slug=gpt-5-5-thinking&thinking_effort=standard"
    },
    { method: "GET", path: "/backend-api/conversation/native-conversation", search: "" }
  ]);
  assert.equal(result.projectId, "g-p-project");
  assert.equal(result.model, "gpt-5-5-thinking");
});

test("ChatGPT follow-up ignores the previous finished assistant while waiting for the new turn", async () => {
  const chatgpt = loadChatGptModule();
  const browserDebugger = new FakeDebugger();
  let currentUrl = "https://chatgpt.com/c/native-conversation";
  let conversationReads = 0;

  const staleConversation = {
    conversation_id: "native-conversation",
    current_node: "assistant-old",
    mapping: {
      "assistant-old": {
        parent: "request-old",
        message: {
          id: "assistant-old",
          author: { role: "assistant" },
          status: "finished_successfully",
          content: { parts: ["GPT-5.5 Thinking"] },
          metadata: { model_slug: "gpt-5-5-thinking" }
        }
      },
      "request-old": {
        parent: null,
        message: {
          id: "request-old",
          author: { role: "user" },
          status: "finished_successfully",
          content: { parts: ["what model are you?"] }
        }
      }
    }
  };

  const freshConversation = {
    conversation_id: "native-conversation",
    current_node: "assistant-new",
    mapping: {
      "assistant-new": {
        parent: "request-new-node",
        message: {
          id: "assistant-new",
          author: { role: "assistant" },
          status: "finished_successfully",
          content: { parts: ["GPT-5.6 Sol"] },
          metadata: { model_slug: "gpt-5-6-thinking" }
        }
      },
      "request-new-node": {
        parent: "assistant-old",
        message: {
          id: "native-message-2",
          author: { role: "user" },
          status: "finished_successfully",
          content: { parts: ["and now?"] },
          metadata: { resolved_model_slug: "gpt-5-6-thinking" }
        }
      },
      ...staleConversation.mapping
    }
  };

  const session = {
    fetch: async (url, options = {}) => {
      const target = new URL(String(url));
      if (target.pathname === "/api/auth/session")
        return jsonResponse({ accessToken: "token", user: { id: "account-1" } });
      if (target.pathname === "/backend-api/settings/user_last_used_model_config")
        return new Response("", { status: 200 });
      if (target.pathname === "/backend-api/conversation/native-conversation") {
        conversationReads++;
        return jsonResponse(conversationReads === 1 ? staleConversation : freshConversation);
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

  const page = {
    invoke: async () => {
      browserDebugger.emit("message", {}, "Fetch.requestPaused", {
        requestId: "request-2",
        request: {
          url: "https://chatgpt.com/backend-api/f/conversation",
          postData: JSON.stringify({
            action: "next",
            conversation_id: "native-conversation",
            messages: [{ id: "native-message-2", author: { role: "user" } }],
            model: "gpt-5-5-thinking",
            thinking_effort: "standard"
          })
        }
      });
      await new Promise(resolve => setImmediate(resolve));
    }
  };

  const result = await chatgpt.operations.send({
    window,
    session,
    page,
    args: {
      prompt: "and now?",
      conversationId: "native-conversation",
      parentMessageId: "assistant-old",
      model: "gpt-5-6-thinking::thinking-effort=standard",
      files: []
    },
    sleep: async () => {}
  });

  assert.equal(conversationReads, 2);
  assert.equal(result.text, "GPT-5.6 Sol");
  assert.equal(result.model, "gpt-5-6-thinking");
  const continuedBody = JSON.parse(
    Buffer.from(browserDebugger.continued.postData, "base64").toString("utf8")
  );
  assert.equal(continuedBody.model, "gpt-5-6-thinking");
  assert.equal(continuedBody.thinking_effort, "standard");
});
