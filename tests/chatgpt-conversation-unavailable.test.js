// Regression tests for ChatGPT's expected inaccessible-conversation provider state.
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
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "mezhs-chatgpt-unavailable-test-"));
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

function sessionForConversationError(code) {
  return {
    cookies: { get: async () => [] },
    async fetch(url, options = {}) {
      const target = new URL(String(url));
      if (target.pathname === "/api/auth/session")
        return jsonResponse({ accessToken: "token" });
      if (target.pathname === "/backend-api/f/conversation/prepare")
        return jsonResponse({ conduit_token: "conduit" });
      if (target.pathname === "/backend-api/sentinel/chat-requirements/prepare")
        return jsonResponse({ prepare_token: "prepared" });
      if (target.pathname === "/backend-api/sentinel/chat-requirements/finalize")
        return jsonResponse({ token: "sentinel" });
      if (target.pathname === "/backend-api/f/conversation" && options.method === "POST")
        return textResponse(
          'data: {"conversation_id":"stale-conversation"}\n\n',
          200,
          "text/event-stream"
        );
      if (target.pathname === "/backend-api/conversation/stale-conversation") {
        return jsonResponse({
          detail: {
            message: "You don’t have access to this conversation.",
            code,
            can_retry: false
          },
          conversation_id: "stale-conversation"
        }, 404);
      }
      throw new Error(`Unexpected request ${target}`);
    }
  };
}

function nativeContinuationWindow() {
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
    async sendCommand(method, args = {}) {
      if (method === "Network.enable")
        return {};
      if (method === "Network.streamResourceContent") {
        assert.equal(args.requestId, "native-1");
        return {
          bufferedData: Buffer.from(
            'data: {"conversation_id":"stale-conversation"}\n\ndata: [DONE]\n\n',
            "utf8"
          ).toString("base64")
        };
      }
      if (method === "Network.getResponseBody")
        return { body: "", base64Encoded: false };
      throw new Error(`Unexpected debugger command ${method}`);
    }
  }

  const debug = new NativeDebugger();
  let currentUrl = "https://chatgpt.com/c/stale-conversation";
  let composerText = "";

  return {
    loadURL: async url => { currentUrl = String(url); },
    webContents: {
      debugger: debug,
      getURL: () => currentUrl,
      executeJavaScript: async source => {
        assert.doesNotThrow(() => new Function(`return ${source};`));
        return { ok: true };
      },
      selectAll: () => {},
      insertText: async text => { composerText = String(text); },
      sendInputEvent: event => {
        if (event.type !== "keyDown" || event.keyCode !== "Enter")
          return;

        const body = {
          action: "next",
          model: "gpt-5-6-thinking",
          parent_message_id: "old-parent",
          conversation_id: "stale-conversation",
          client_prepare_state: "success",
          supported_encodings: ["v1"],
          messages: [{
            id: "request-1",
            author: { role: "user" },
            content: { content_type: "text", parts: [composerText] }
          }]
        };

        debug.emit("message", {}, "Network.requestWillBeSent", {
          requestId: "native-1",
          request: {
            url: "https://chatgpt.com/backend-api/f/conversation",
            method: "POST",
            headers: {},
            postData: JSON.stringify(body)
          }
        });
        debug.emit("message", {}, "Network.responseReceived", {
          requestId: "native-1",
          response: { status: 200 }
        });
        debug.emit("message", {}, "Network.loadingFinished", {
          requestId: "native-1"
        });
      }
    }
  };
}

function send(chatgpt, session) {
  return chatgpt.operations.send({
    window: nativeContinuationWindow(),
    session,
    args: {
      prompt: "continue",
      conversationId: "stale-conversation",
      parentMessageId: "old-parent",
      files: []
    },
    sleep: async () => {}
  });
}

test("ChatGPT inaccessible continuation returns provider state instead of throwing", async () => {
  const chatgpt = loadChatGptModule();
  const result = await send(chatgpt, sessionForConversationError("conversation_inaccessible"));
  assert.deepEqual(result, { conversationUnavailable: true });
});

test("ChatGPT unrelated conversation 404 still fails", async () => {
  const chatgpt = loadChatGptModule();
  await assert.rejects(
    send(chatgpt, sessionForConversationError("some_other_error")),
    /ChatGPT \/backend-api\/conversation\/stale-conversation failed with HTTP 404/
  );
});
