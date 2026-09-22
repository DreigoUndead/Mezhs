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
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "mezhs-chatgpt-lifetime-"));
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
  }

  isAttached() { return this.attached; }
  attach() { this.attached = true; }
  detach() { this.attached = false; }
  async sendCommand() {}
}

test("ChatGPT native observation timeout starts after the UI submit finishes", async () => {
  const chatgpt = loadChatGptModule();
  const browserDebugger = new FakeDebugger();
  const requestMessageId = "native-message-timeout";
  let currentUrl = "https://chatgpt.com/c/native-conversation";

  const session = {
    fetch: async url => {
      const target = new URL(String(url));
      if (target.pathname === "/api/auth/session")
        return jsonResponse({ accessToken: "token", user: { id: "account-1" } });
      if (target.pathname === "/backend-api/conversation/native-conversation") {
        return jsonResponse({
          conversation_id: "native-conversation",
          current_node: "assistant-new",
          mapping: {
            "assistant-new": {
              parent: "request-new",
              message: {
                id: "assistant-new",
                author: { role: "assistant" },
                status: "finished_successfully",
                content: { parts: ["completed"] },
                metadata: { model_slug: "gpt-5-6-thinking" }
              }
            },
            "request-new": {
              parent: null,
              message: {
                id: requestMessageId,
                author: { role: "user" },
                status: "finished_successfully",
                content: { parts: ["continue"] },
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

  const page = {
    invoke: async () => {
      browserDebugger.emit("message", {}, "Fetch.requestPaused", {
        requestId: "request-timeout",
        request: {
          url: "https://chatgpt.com/backend-api/f/conversation",
          postData: JSON.stringify({
            action: "next",
            conversation_id: "native-conversation",
            messages: [{ id: requestMessageId, author: { role: "user" } }],
            model: "gpt-5-6-thinking"
          })
        }
      });
    }
  };

  const originalSetTimeout = global.setTimeout;
  global.setTimeout = (callback, milliseconds, ...args) => {
    if (milliseconds !== 60000)
      return originalSetTimeout(callback, milliseconds, ...args);

    const handle = originalSetTimeout(() => {}, 60 * 60 * 1000);
    callback(...args);
    return handle;
  };

  try {
    const result = await chatgpt.operations.send({
      window,
      session,
      page,
      args: {
        prompt: "continue",
        conversationId: "native-conversation",
        parentMessageId: "assistant-old",
        model: "auto",
        files: []
      },
      sleep: async () => {}
    });

    assert.equal(result.text, "completed");
  } finally {
    global.setTimeout = originalSetTimeout;
  }
});
