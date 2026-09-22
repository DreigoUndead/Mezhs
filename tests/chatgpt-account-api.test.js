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


test("ChatGPT reposts once when a submitted turn never shows model activity", async () => {
  const chatgpt = loadChatGptModule();
  const requestIds = [];
  const postedConversationIds = [];
  let reads = 0;

  const session = protocolSession({
    conversationId: "conv-retry-start",
    onConversationPost: body => {
      requestIds.push(body.messages[0].id);
      postedConversationIds.push(body.conversation_id ?? null);
    },
    onConversationRead: () => {
      reads++;
      const activeRequestId = requestIds.at(-1);
      if (requestIds.length === 1) {
        return jsonResponse({
          conversation_id: "conv-retry-start",
          current_node: "request-new",
          mapping: {
            "request-new": {
              parent: null,
              message: {
                id: activeRequestId,
                author: { role: "user" },
                status: "finished_successfully",
                content: { content_type: "text", parts: ["prompt"] }
              }
            }
          }
        });
      }
      return jsonResponse(
        completedConversation("conv-retry-start", activeRequestId, "retry worked")
      );
    }
  });

  const result = await chatgpt.operations.newChat({
    ...hostileBrowserSurface(),
    session,
    args: { prompt: "retry me", files: [] },
    sleep: async () => {}
  });

  assert.equal(requestIds.length, 2);
  assert.notEqual(requestIds[0], requestIds[1]);
  assert.deepEqual(postedConversationIds, [null, "conv-retry-start"]);
  assert.ok(reads >= 11);
  assert.equal(result.text, "retry worked");
});

test("ChatGPT reposts when model activity stops without a final reply", async () => {
  const chatgpt = loadChatGptModule();
  const requestIds = [];
  let reads = 0;
  const progress = [];

  const stalledConversation = requestMessageId => ({
    conversation_id: "conv-stalled-activity",
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
            parts: ["Command finished; deciding what to do next."]
          }
        }
      },
      "request-new": {
        parent: null,
        message: {
          id: requestMessageId,
          author: { role: "user" },
          status: "finished_successfully",
          content: { content_type: "text", parts: ["prompt"] }
        }
      }
    }
  });

  const session = protocolSession({
    conversationId: "conv-stalled-activity",
    onConversationPost: body => {
      requestIds.push(body.messages[0].id);
    },
    onConversationRead: () => {
      reads++;
      const activeRequestId = requestIds.at(-1);
      return jsonResponse(
        requestIds.length === 1
          ? stalledConversation(activeRequestId)
          : completedConversation("conv-stalled-activity", activeRequestId, "retry recovered")
      );
    }
  });

  const result = await chatgpt.operations.newChat({
    ...hostileBrowserSurface(),
    session,
    args: { prompt: "recover stalled turn", files: [] },
    sleep: async () => {},
    reportProgress: value => progress.push(value)
  });

  assert.equal(requestIds.length, 2);
  assert.notEqual(requestIds[0], requestIds[1]);
  assert.ok(reads >= 12);
  assert.equal(result.text, "retry recovered");
  assert.ok(progress.some(value =>
    value.state === "waiting" &&
    value.detail === "Model activity was observed, but no active generation is currently detected." &&
    value.analysis === "Command finished; deciding what to do next."
  ));
  assert.ok(progress.some(value => value.state === "retrying"));
});

test("ChatGPT inactivity watchdog resets when the conversation advances", async () => {
  const chatgpt = loadChatGptModule();
  let requestMessageId;
  let posts = 0;
  let reads = 0;

  const progressConversation = (nodeId, text) => ({
    conversation_id: "conv-progress-reset",
    current_node: nodeId,
    mapping: {
      [nodeId]: {
        parent: "request-new",
        message: {
          id: nodeId,
          author: { role: "assistant" },
          status: "finished_successfully",
          channel: "analysis",
          content: {
            content_type: "text",
            parts: [text]
          }
        }
      },
      "request-new": {
        parent: null,
        message: {
          id: requestMessageId,
          author: { role: "user" },
          status: "finished_successfully",
          content: { content_type: "text", parts: ["prompt"] }
        }
      }
    }
  });

  const session = protocolSession({
    conversationId: "conv-progress-reset",
    onConversationPost: body => {
      posts++;
      requestMessageId = body.messages[0].id;
    },
    onConversationRead: () => {
      reads++;
      if (reads <= 6)
        return jsonResponse(progressConversation("assistant-analysis-1", "First progress marker."));
      if (reads <= 12)
        return jsonResponse(progressConversation("assistant-analysis-2", "Second progress marker."));
      return jsonResponse(
        completedConversation("conv-progress-reset", requestMessageId, "done")
      );
    }
  });

  const result = await chatgpt.operations.newChat({
    ...hostileBrowserSurface(),
    session,
    args: { prompt: "keep progressing", files: [] },
    sleep: async () => {}
  });

  assert.equal(posts, 1);
  assert.equal(reads, 13);
  assert.equal(result.text, "done");
});

test("ChatGPT keeps waiting while explicit in-progress analysis remains active", async () => {
  const chatgpt = loadChatGptModule();
  let requestMessageId;
  let posts = 0;
  let reads = 0;
  const progress = [];

  const session = protocolSession({
    conversationId: "conv-thinking-state",
    onConversationPost: body => {
      posts++;
      requestMessageId = body.messages[0].id;
    },
    onConversationRead: () => {
      reads++;
      if (reads > 15)
        return jsonResponse(completedConversation("conv-thinking-state", requestMessageId, "done"));
      return jsonResponse({
        conversation_id: "conv-thinking-state",
        current_node: "assistant-analysis",
        mapping: {
          "assistant-analysis": {
            parent: "request-new",
            message: {
              id: "assistant-analysis",
              author: { role: "assistant" },
              status: "in_progress",
              channel: "analysis",
              content: {
                content_type: "text",
                parts: ["Inspecting the failure state."]
              }
            }
          },
          "request-new": {
            parent: null,
            message: {
              id: requestMessageId,
              author: { role: "user" },
              status: "finished_successfully",
              content: { content_type: "text", parts: ["prompt"] }
            }
          }
        }
      });
    }
  });

  const result = await chatgpt.operations.newChat({
    ...hostileBrowserSurface(),
    session,
    args: { prompt: "state please", files: [] },
    sleep: async () => {},
    reportProgress: value => progress.push(value)
  });

  assert.equal(posts, 1);
  assert.equal(reads, 16);
  assert.equal(result.text, "done");
  assert.ok(progress.some(value =>
    value.state === "thinking" &&
    value.analysis === "Inspecting the failure state."
  ));
});

test("rate-limited state checks do not consume the turn-start watchdog", async () => {
  const chatgpt = loadChatGptModule();
  let requestMessageId;
  let posts = 0;
  let reads = 0;

  const session = protocolSession({
    conversationId: "conv-rate-limit-watchdog",
    onConversationPost: body => {
      posts++;
      requestMessageId = body.messages[0].id;
    },
    onConversationRead: () => {
      reads++;
      if (reads <= 12) {
        return new Response('{"detail":"Too many requests"}', {
          status: 429,
          headers: { "Retry-After": "2" }
        });
      }
      return jsonResponse(
        completedConversation("conv-rate-limit-watchdog", requestMessageId, "after throttling")
      );
    }
  });

  const result = await chatgpt.operations.newChat({
    ...hostileBrowserSurface(),
    session,
    args: { prompt: "do not duplicate", files: [] },
    sleep: async () => {}
  });

  assert.equal(posts, 1);
  assert.equal(reads, 13);
  assert.equal(result.text, "after throttling");
});

test("ChatGPT fails after one automatic repost if model activity still never starts", async () => {
  const chatgpt = loadChatGptModule();
  let requestMessageId;
  let posts = 0;

  const session = protocolSession({
    conversationId: "conv-dead-start",
    onConversationPost: body => {
      posts++;
      requestMessageId = body.messages[0].id;
    },
    onConversationRead: () => jsonResponse({
      conversation_id: "conv-dead-start",
      current_node: "request-new",
      mapping: {
        "request-new": {
          parent: null,
          message: {
            id: requestMessageId,
            author: { role: "user" },
            status: "finished_successfully",
            content: { content_type: "text", parts: ["prompt"] }
          }
        }
      }
    })
  });

  await assert.rejects(
    chatgpt.operations.newChat({
      ...hostileBrowserSurface(),
      session,
      args: { prompt: "never starts", files: [] },
      sleep: async () => {}
    }),
    /showed no active generation for 20s after the automatic retry/
  );
  assert.equal(posts, 2);
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
