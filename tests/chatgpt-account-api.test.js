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
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "mezhs-chatgpt-api-"));
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

function completedConversation(conversationId, requestMessageId, text = "answer") {
  return {
    conversation_id: conversationId,
    current_node: "assistant-new",
    mapping: {
      "assistant-new": {
        parent: "request-new",
        message: {
          id: "assistant-new",
          author: { role: "assistant" },
          status: "finished_successfully",
          content: { parts: [text] },
          metadata: { model_slug: "gpt-5-6-thinking" }
        }
      },
      "request-new": {
        parent: null,
        message: {
          id: requestMessageId,
          author: { role: "user" },
          status: "finished_successfully",
          content: { parts: ["prompt"] },
          metadata: { resolved_model_slug: "gpt-5-6-thinking" }
        }
      }
    }
  };
}

function apiSession(handler) {
  return {
    fetch: handler,
    cookies: { get: async () => [] }
  };
}

function hostileBrowserSurface() {
  return {
    window: {
      loadURL: async () => {
        throw new Error("ChatGPT account send must not navigate the UI.");
      },
      webContents: {
        getUserAgent: () => "TestBrowser/1.0",
        debugger: {
          isAttached() { throw new Error("ChatGPT account send must not inspect the debugger."); },
          attach() { throw new Error("ChatGPT account send must not attach the debugger."); },
          sendCommand() { throw new Error("ChatGPT account send must not use debugger commands."); }
        }
      }
    },
    page: {
      invoke: async () => {
        throw new Error("ChatGPT account send must not invoke page operations.");
      }
    }
  };
}

function protocolSession({ conversationId, onConversationRead, onConversationPost }) {
  return apiSession(async (url, options = {}) => {
    const target = new URL(String(url));

    if (target.pathname === "/api/auth/session")
      return jsonResponse({ accessToken: "token", user: { id: "account-1" } });

    if (target.pathname === "/backend-api/f/conversation/prepare")
      return jsonResponse({ conduit_token: "conduit" });

    if (target.pathname === "/backend-api/sentinel/chat-requirements/prepare")
      return jsonResponse({ prepare_token: "prepared" });

    if (target.pathname === "/backend-api/sentinel/chat-requirements/finalize")
      return jsonResponse({ token: "sentinel" });

    if (target.pathname === "/backend-api/f/conversation" && options.method === "POST") {
      const body = JSON.parse(options.body);
      onConversationPost?.(body);
      return textResponse(
        `data: {"conversation_id":"${conversationId}"}\n\ndata: [DONE]\n\n`,
        200,
        "text/event-stream"
      );
    }

    if (target.pathname === `/backend-api/conversation/${conversationId}`)
      return onConversationRead();

    throw new Error(`Unexpected request ${target}`);
  });
}

test("ChatGPT account newChat uses the semantic API even when browser UI hooks exist", async () => {
  const chatgpt = loadChatGptModule();
  const browser = hostileBrowserSurface();
  let requestMessageId;
  let conversationPosts = 0;

  const session = protocolSession({
    conversationId: "conv-api",
    onConversationPost: body => {
      conversationPosts++;
      requestMessageId = body.messages[0].id;
      assert.equal(body.messages[0].content.parts.at(-1), "hello api");
    },
    onConversationRead: () =>
      jsonResponse(completedConversation("conv-api", requestMessageId, "API_OK"))
  });

  const result = await chatgpt.operations.newChat({
    ...browser,
    session,
    args: { prompt: "hello api", files: [] },
    sleep: async () => {}
  });

  assert.equal(conversationPosts, 1);
  assert.equal(result.conversationId, "conv-api");
  assert.equal(result.text, "API_OK");
});

test("ChatGPT follow-up ignores a stale assistant until the sent API message appears in ancestry", async () => {
  const chatgpt = loadChatGptModule();
  let requestMessageId;
  let reads = 0;
  const stale = {
    conversation_id: "conv-existing",
    current_node: "assistant-old",
    mapping: {
      "assistant-old": {
        parent: "request-old",
        message: {
          id: "assistant-old",
          author: { role: "assistant" },
          status: "finished_successfully",
          content: { parts: ["old answer"] }
        }
      },
      "request-old": {
        parent: null,
        message: {
          id: "request-old",
          author: { role: "user" },
          status: "finished_successfully",
          content: { parts: ["old prompt"] }
        }
      }
    }
  };

  const session = protocolSession({
    conversationId: "conv-existing",
    onConversationPost: body => { requestMessageId = body.messages[0].id; },
    onConversationRead: () => {
      reads++;
      return jsonResponse(
        reads === 1
          ? stale
          : completedConversation("conv-existing", requestMessageId, "fresh answer")
      );
    }
  });

  const result = await chatgpt.operations.send({
    ...hostileBrowserSurface(),
    session,
    args: {
      prompt: "continue",
      conversationId: "conv-existing",
      parentMessageId: "assistant-old",
      files: []
    },
    sleep: async () => {}
  });

  assert.equal(reads, 2);
  assert.equal(result.text, "fresh answer");
});

test("ChatGPT API polling backs off on 429 without resending the turn", async () => {
  const chatgpt = loadChatGptModule();
  let requestMessageId;
  let reads = 0;
  let posts = 0;
  const sleeps = [];

  const session = protocolSession({
    conversationId: "conv-rate-limit",
    onConversationPost: body => {
      posts++;
      requestMessageId = body.messages[0].id;
    },
    onConversationRead: () => {
      reads++;
      if (reads === 1) {
        return new Response('{"detail":"Too many requests"}', {
          status: 429,
          headers: { "Retry-After": "2" }
        });
      }
      return jsonResponse(
        completedConversation("conv-rate-limit", requestMessageId, "survived rate limit")
      );
    }
  });

  const result = await chatgpt.operations.newChat({
    ...hostileBrowserSurface(),
    session,
    args: { prompt: "test", files: [] },
    sleep: async ms => { sleeps.push(ms); }
  });

  assert.equal(posts, 1);
  assert.equal(reads, 2);
  assert.deepEqual(sleeps, [2000]);
  assert.equal(result.text, "survived rate limit");
});


test("ChatGPT account does not surface analysis-channel control text as the reply", async () => {
  const chatgpt = loadChatGptModule();
  let requestMessageId;
  let reads = 0;

  const analysisOnly = () => ({
    conversation_id: "conv-analysis",
    current_node: "assistant-analysis",
    mapping: {
      "assistant-analysis": {
        parent: "request-new",
        message: {
          id: "assistant-analysis",
          author: { role: "assistant" },
          status: "finished_successfully",
          channel: "analysis",
          content: {
            content_type: "text",
            parts: [
              "Need inspect WhatsApp project. Need find run instructions. Use command.\n<SH>\ndir\n</SH>\n\n<|end|>"
            ]
          },
          metadata: { model_slug: "gpt-5-6-thinking" }
        }
      },
      "request-new": {
        parent: null,
        message: {
          id: requestMessageId,
          author: { role: "user" },
          status: "finished_successfully",
          content: { content_type: "text", parts: ["prompt"] },
          metadata: { resolved_model_slug: "gpt-5-6-thinking" }
        }
      }
    }
  });

  const finalConversation = () => ({
    conversation_id: "conv-analysis",
    current_node: "assistant-final",
    mapping: {
      "assistant-final": {
        parent: "assistant-analysis",
        message: {
          id: "assistant-final",
          author: { role: "assistant" },
          status: "finished_successfully",
          channel: "final",
          content: { content_type: "text", parts: ["Visible final answer"] },
          metadata: { model_slug: "gpt-5-6-thinking" }
        }
      },
      ...analysisOnly().mapping
    }
  });

  const session = protocolSession({
    conversationId: "conv-analysis",
    onConversationPost: body => { requestMessageId = body.messages[0].id; },
    onConversationRead: () => {
      reads++;
      return jsonResponse(reads === 1 ? analysisOnly() : finalConversation());
    }
  });

  const result = await chatgpt.operations.newChat({
    ...hostileBrowserSurface(),
    session,
    args: { prompt: "test internal filtering", files: [] },
    sleep: async () => {}
  });

  assert.equal(reads, 2);
  assert.equal(result.text, "Visible final answer");
  assert.equal(result.parentMessageId, "assistant-final");
  assert.doesNotMatch(result.text, /<\|end\|>/);
  assert.doesNotMatch(result.text, /Need inspect WhatsApp project/);
});

test("ChatGPT account walks past hidden current nodes to the visible final reply", async () => {
  const chatgpt = loadChatGptModule();
  let requestMessageId;

  const session = protocolSession({
    conversationId: "conv-hidden-tail",
    onConversationPost: body => { requestMessageId = body.messages[0].id; },
    onConversationRead: () => jsonResponse({
      conversation_id: "conv-hidden-tail",
      current_node: "assistant-hidden",
      mapping: {
        "assistant-hidden": {
          parent: "assistant-final",
          message: {
            id: "assistant-hidden",
            author: { role: "assistant" },
            status: "finished_successfully",
            content: { content_type: "text", parts: ["<|end|>"] },
            metadata: {
              is_visually_hidden_from_conversation: true,
              model_slug: "gpt-5-6-thinking"
            }
          }
        },
        "assistant-final": {
          parent: "request-new",
          message: {
            id: "assistant-final",
            author: { role: "assistant" },
            status: "finished_successfully",
            content: { content_type: "text", parts: ["Actual visible reply"] },
            metadata: { model_slug: "gpt-5-6-thinking" }
          }
        },
        "request-new": {
          parent: null,
          message: {
            id: requestMessageId,
            author: { role: "user" },
            status: "finished_successfully",
            content: { content_type: "text", parts: ["prompt"] },
            metadata: { resolved_model_slug: "gpt-5-6-thinking" }
          }
        }
      }
    })
  });

  const result = await chatgpt.operations.newChat({
    ...hostileBrowserSurface(),
    session,
    args: { prompt: "test hidden tail", files: [] },
    sleep: async () => {}
  });

  assert.equal(result.text, "Actual visible reply");
  assert.equal(result.parentMessageId, "assistant-final");
});
