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

function completedConversation(
  conversationId,
  requestMessageId,
  text = "answer",
  assistantModel = "gpt-5-6-thinking",
  requestResolvedModel = assistantModel,
  thinkingEffort = null,
  contentReferences = null
) {
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
          metadata: {
            model_slug: assistantModel,
            ...(thinkingEffort ? { thinking_effort: thinkingEffort } : {}),
            ...(contentReferences ? { content_references: contentReferences } : {})
          }
        }
      },
      "request-new": {
        parent: null,
        message: {
          id: requestMessageId,
          author: { role: "user" },
          status: "finished_successfully",
          content: { parts: ["prompt"] },
          metadata: { resolved_model_slug: requestResolvedModel }
        }
      }
    }
  };
}

function apiSession(handler) {
  const cookies = new Map();
  return {
    fetch: handler,
    cookies: {
      get: async ({ name } = {}) => {
        const value = cookies.get(name);
        return value == null ? [] : [{ name, value }];
      },
      set: async cookie => {
        cookies.set(cookie.name, cookie.value);
      }
    },
    __cookies: cookies
  };
}

function nativeBrowserSurface() {
  // Production account submission must not depend on any browser DOM or input APIs.
  return {
    window: {
    webContents: {
      executeJavaScript: async source => {
        assert.match(source, /lXE/);
        assert.doesNotThrow(() => new Function(`return ${source};`));
        assert.doesNotMatch(source, /document\.querySelector|querySelectorAll|sendInputEvent/);
        return {
          headers: {
            "OpenAI-Sentinel-Chat-Requirements-Token": "native-requirements",
            "OpenAI-Sentinel-Proof-Token": "native-proof",
            "OpenAI-Sentinel-Turnstile-Token": "native-turnstile"
          },
          forceLogin: false
        };
      }
    }
  },
    page: {
      invoke: async () => { throw new Error("UI access is forbidden."); }
    }
  };
}

function protocolSession({
  conversationId,
  onConversationRead,
  onConversationPost,
  conversationStream
}) {
  const state = {
    conversationId,
    onConversationPost,
    conversationStream,
    model: "gpt-5-6-thinking",
    thinkingEffort: null,
    lastStream: "",
    posts: 0
  };

  const session = apiSession(async (url, options = {}) => {
    const target = new URL(String(url));
    if (target.pathname === "/api/auth/session")
      return jsonResponse({ accessToken: "token", user: { id: "account-1" } });
    if (target.pathname === "/backend-api/settings/user_last_used_model_config") {
      state.patchedModel = target.searchParams.get("model_slug");
      state.patchedThinkingEffort = target.searchParams.get("thinking_effort");
      return textResponse("");
    }
    if (target.pathname === "/backend-api/conversation/init")
      return jsonResponse({ default_model_slug: state.model });
    if (target.pathname === "/backend-api/f/conversation/prepare")
      return jsonResponse({ conduit_token: "conduit-server-issued" });
    if (target.pathname === "/backend-api/f/conversation" && options.method === "POST") {
      assert.equal(options.headers["OpenAI-Sentinel-Chat-Requirements-Token"], "native-requirements");
      assert.equal(options.headers["OpenAI-Sentinel-Proof-Token"], "native-proof");
      assert.equal(options.headers["OpenAI-Sentinel-Turnstile-Token"], "native-turnstile");
      const body = JSON.parse(options.body);
      state.posts++;
      state.onConversationPost?.(body);
      if (state.abortBeforeResponse)
        throw new Error("Native network connection aborted before HTTP response.");
      const stream = state.conversationStream?.(body) ??
        `data: {"conversation_id":"${conversationId}"}\n\ndata: [DONE]\n\n`;
      if (state.abortAfterResponse) {
        let sent = false;
        return new Response(new ReadableStream({
          pull(controller) {
            if (!sent) {
              sent = true;
              controller.enqueue(new TextEncoder().encode(stream));
            } else {
              controller.error(new Error("response stream interrupted"));
            }
          }
        }), { status: 200, headers: { "content-type": "text/event-stream" } });
      }
      return textResponse(stream, 200, "text/event-stream");
    }
    if (target.pathname === `/backend-api/conversation/${conversationId}`)
      return onConversationRead();
    throw new Error(`Unexpected request ${target}`);
  });
  session.__native = state;
  return session;
}

test("ChatGPT account newChat submits a semantic request and reads conversation state", async () => {
  const chatgpt = loadChatGptModule();
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

  session.__native.draft = "stale draft from a previous native send";

  const result = await chatgpt.operations.newChat({
    ...nativeBrowserSurface(session),
    session,
    args: { prompt: "hello api", files: [] },
    sleep: async () => {}
  });

  assert.equal(conversationPosts, 1);
  assert.equal(result.conversationId, "conv-api");
  assert.equal(result.text, "API_OK");
});

test("ChatGPT resolves citation glyphs from matching content references", async () => {
  const chatgpt = loadChatGptModule();
  let requestMessageId;
  const cited = "\uE200cite\uE202turn0search0\uE201";
  const unknown = "\uE200cite\uE202turn0search3\uE201";
  const memory = "\uE200memcite\uE201";
  const session = protocolSession({
    conversationId: "conv-citations",
    onConversationPost: body => { requestMessageId = body.messages[0].id; },
    onConversationRead: () => jsonResponse(completedConversation(
      "conv-citations",
      requestMessageId,
      "Sources for fruit " + cited + " and " + unknown + ". " + memory,
      "gpt-5-6-thinking",
      "gpt-5-6-thinking",
      null,
      [
        { type: "grouped_webpages", matched_text: cited,
          alt: "([Growing guide](https://example.org/watermelons))" },
        { type: "sources_footnote", matched_text: " ", alt: "should NOT replace spaces" }
      ]
    ))
  });

  const result = await chatgpt.operations.newChat({
    ...nativeBrowserSurface(),
    session,
    args: { prompt: "give sources", files: [] },
    sleep: async () => {}
  });
  assert.equal(
    result.text,
    "Sources for fruit ([Growing guide](https://example.org/watermelons)) and [citation unavailable]."
  );
  assert.doesNotMatch(result.text, /[\uE200-\uE203]/);
});

test("ChatGPT fails closed when browser-owned requirements are unavailable", async () => {
  const chatgpt = loadChatGptModule();
  let posts = 0;
  const session = protocolSession({
    conversationId: "conv-missing-requirements",
    onConversationPost: () => { posts++; },
    onConversationRead: () => {
      throw new Error("No conversation polling should occur.");
    }
  });
  const browser = nativeBrowserSurface();
  browser.window.webContents.executeJavaScript = async () => ({
    requirementsToken: null, prepareToken: null, forceLogin: false
  });
  await assert.rejects(
    chatgpt.operations.newChat({
      ...browser,
      session,
      args: { prompt: "should not submit", files: [] },
      sleep: async () => {}
    }),
    /did not produce a requirements token/
  );
  assert.equal(posts, 0);
  assert.equal(session.__native.posts, 0);
});

test("ChatGPT continues after a partial semantic response stream is aborted", async () => {
  const chatgpt = loadChatGptModule();
  let requestMessageId;
  let posts = 0;

  const session = protocolSession({
    conversationId: "conv-aborted",
    onConversationPost: body => {
      posts++;
      requestMessageId = body.messages[0].id;
    },
    onConversationRead: () =>
      jsonResponse(completedConversation(
        "conv-aborted",
        requestMessageId,
        "ABORTED_STREAM_OK"
      ))
  });
  session.__native.model = "gpt-5-6-thinking";
  session.__native.thinkingEffort = "extended";
  session.__native.abortAfterResponse = true;
  session.__native.responseBodyUnavailable = true;
  session.__native.stayOnProjectUrl = true;
  session.__native.emitConversationInitRequest = true;

  const result = await chatgpt.operations.newChat({
    ...nativeBrowserSurface(session),
    session,
    args: {
      prompt: "test successful renderer abort",
      model: "gpt-5-6-thinking::thinking-effort=extended",
      files: []
    },
    sleep: async () => {}
  });

  assert.equal(posts, 1);
  assert.equal(result.conversationId, "conv-aborted");
  assert.equal(result.text, "ABORTED_STREAM_OK");
});

test("ChatGPT does not retry when the semantic request aborts before a response", async () => {
  const chatgpt = loadChatGptModule();

  const session = protocolSession({
    conversationId: "conv-never-accepted",
    onConversationPost: () => {},
    onConversationRead: () => {
      throw new Error("Conversation polling must not start.");
    }
  });
  session.__native.abortBeforeResponse = true;

  await assert.rejects(
    chatgpt.operations.newChat({
      ...nativeBrowserSurface(session),
      session,
      args: { prompt: "must fail", files: [] },
      sleep: async () => {}
    }),
    /Native network connection aborted before HTTP response/
  );
});

test("ChatGPT follow-up ignores a stale assistant until its user message appears in ancestry", async () => {
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
    ...nativeBrowserSurface(session),
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

test("ChatGPT final conversation metadata overrides earlier stream model metadata", async () => {
  const chatgpt = loadChatGptModule();
  let requestMessageId;
  const progress = [];

  const session = protocolSession({
    conversationId: "conv-served-model",
    onConversationPost: body => {
      requestMessageId = body.messages[0].id;
    },
    conversationStream: () =>
      'data: {"conversation_id":"conv-served-model","metadata":{"resolved_model_slug":"gpt-5-6-thinking","thinking_effort":"extended"}}\n\ndata: [DONE]\n\n',
    onConversationRead: () =>
      jsonResponse(completedConversation(
        "conv-served-model",
        requestMessageId,
        "served instant",
        "gpt-5-6",
        "gpt-5-6-thinking"
      ))
  });

  session.__native.model = "gpt-5-6-thinking";
  session.__native.thinkingEffort = "extended";

  const result = await chatgpt.operations.newChat({
    ...nativeBrowserSurface(session),
    session,
    args: {
      prompt: "verify served model",
      model: "gpt-5-6-thinking::thinking-effort=extended",
      files: []
    },
    sleep: async () => {},
    reportProgress: value => progress.push(value)
  });

  assert.equal(result.model, "gpt-5-6");
  assert.ok(progress.some(value =>
    value.state === "completed" &&
    value.detail.includes("served model gpt-5-6") &&
    value.detail.includes("requested model gpt-5-6-thinking")
  ));
});

test("ChatGPT conversation polling backs off on 429 without resending the turn", async () => {
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
    ...nativeBrowserSurface(session),
    session,
    args: { prompt: "test", files: [] },
    sleep: async ms => { sleeps.push(ms); }
  });

  assert.equal(posts, 1);
  assert.equal(reads, 2);
  assert.deepEqual(sleeps, [2000]);
  assert.equal(result.text, "survived rate limit");
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
    ...nativeBrowserSurface(session),
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
    ...nativeBrowserSurface(session),
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

test("ChatGPT captures transient provider reasoning metadata from the response stream", async () => {
  const chatgpt = loadChatGptModule();
  let requestMessageId;
  let postedPayload;
  const progress = [];

  const session = protocolSession({
    conversationId: "conv-stream-reasoning",
    onConversationPost: body => {
      postedPayload = body;
      requestMessageId = body.messages[0].id;
    },
    conversationStream: () => [
      'data: {"type":"message_marker","conversation_id":"conv-stream-reasoning","message_id":"reasoning-1","marker":"cot_token","event":"first"}',
      "",
      'event: delta',
      'data: {"v":{"message":{"id":"reasoning-1","author":{"role":"assistant"},"content":{"content_type":"reasoning_recap","content":"Worked"},"status":"finished_successfully","metadata":{"reasoning_status":"reasoning_ended","reasoning_start_time":100.25,"reasoning_end_time":103.75,"resolved_model_slug":"provider-thinking-model","model_slug":"provider-thinking-model","thinking_effort":"provider-high","can_save":false}},"conversation_id":"conv-stream-reasoning"}}',
      "",
      'event: delta',
      'data: {"v":{"message":{"id":"tool-call-1","author":{"role":"assistant"},"recipient":"web.run","content":{"content_type":"text","parts":[""]},"status":"finished_successfully","metadata":{"resolved_model_slug":"provider-thinking-model","thinking_effort":"provider-high"}},"conversation_id":"conv-stream-reasoning"}}',
      "",
      'event: delta',
      'data: {"v":{"message":{"id":"tool-result-1","author":{"role":"tool","name":"web.run"},"content":{"content_type":"text","parts":[""]},"status":"finished_successfully","metadata":{}},"conversation_id":"conv-stream-reasoning"}}',
      "",
      'data: {"type":"message_marker","conversation_id":"conv-stream-reasoning","message_id":"assistant-new","marker":"final_channel_token","event":"first"}',
      "",
      "data: [DONE]",
      ""
    ].join("\n"),
    onConversationRead: () =>
      jsonResponse(completedConversation(
        "conv-stream-reasoning",
        requestMessageId,
        "done",
        "provider-thinking-model",
        "provider-thinking-model",
        "provider-high"
      ))
  });

  session.__native.model = "provider-thinking-model";
  session.__native.thinkingEffort = "provider-high";

  const result = await chatgpt.operations.newChat({
    ...nativeBrowserSurface(session),
    session,
    args: {
      prompt: "reason",
      model: "provider-thinking-model::thinking-effort=provider-high",
      files: []
    },
    sleep: async () => {},
    reportProgress: value => progress.push(value)
  });

  assert.equal(postedPayload.model, "provider-thinking-model");
  assert.equal(postedPayload.thinking_effort, "provider-high");
  assert.equal(
    result.model,
    "provider-thinking-model::thinking-effort=provider-high"
  );
  assert.ok(progress.some(value =>
    value.state === "thinking" &&
    value.detail === "ChatGPT reported reasoning activity."
  ));
  assert.ok(progress.some(value =>
    value.state === "responding" &&
    value.detail === "ChatGPT is generating the visible response."
  ));
  assert.ok(progress.some(value =>
    value.state === "completed" &&
    value.detail ===
      "Model response received (served model provider-thinking-model, effort provider-high, reasoning 3.5s, tools web.run)."
  ));
  const states = progress.map(value => value.state);
  assert.ok(states.indexOf("waiting") >= 0);
  assert.ok(states.indexOf("waiting") < states.indexOf("thinking"));
  assert.ok(states.indexOf("thinking") < states.indexOf("responding"));
});

test("ChatGPT distinguishes requested thinking effort from provider-confirmed effort", async () => {
  const chatgpt = loadChatGptModule();
  let requestMessageId;
  const progress = [];

  const session = protocolSession({
    conversationId: "conv-effort-unconfirmed",
    onConversationPost: body => {
      requestMessageId = body.messages[0].id;
    },
    onConversationRead: () =>
      jsonResponse(completedConversation("conv-effort-unconfirmed", requestMessageId, "done"))
  });

  session.__native.model = "gpt-5-6-thinking";
  session.__native.thinkingEffort = "extended";

  const result = await chatgpt.operations.newChat({
    ...nativeBrowserSurface(session),
    session,
    args: {
      prompt: "reason",
      model: "gpt-5-6-thinking::thinking-effort=extended",
      files: []
    },
    sleep: async () => {},
    reportProgress: value => progress.push(value)
  });

  assert.equal(result.model, "gpt-5-6-thinking");
  assert.ok(progress.some(value =>
    value.state === "completed" &&
    value.detail ===
      "Model response received (served model gpt-5-6-thinking, requested effort extended, not confirmed by provider)."
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
    ...nativeBrowserSurface(session),
    session,
    args: { prompt: "do not duplicate", files: [] },
    sleep: async () => {}
  });

  assert.equal(posts, 1);
  assert.equal(reads, 13);
  assert.equal(result.text, "after throttling");
});

test("ChatGPT fails stalled state polling without replaying a request", async () => {
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
      ...nativeBrowserSurface(session),
      session,
      args: { prompt: "never starts", files: [] },
      sleep: async () => {}
    }),
    /showed no active generation for 20s after the native request completed/
  );
  assert.equal(posts, 1);
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
    ...nativeBrowserSurface(session),
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
    ...nativeBrowserSurface(session),
    session,
    args: { prompt: "test hidden tail", files: [] },
    sleep: async () => {}
  });

  assert.equal(result.text, "Actual visible reply");
  assert.equal(result.parentMessageId, "assistant-final");
});

test('ChatGPT Account uploads images and documents before one semantic turn', async () => {
  const chatgpt = loadChatGptModule();
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'mezhs-upload-fixtures-'));
  const image = path.join(dir, 'sample.png');
  const document = path.join(dir, 'sample.txt');
  const imageBytes = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGM4YS4CAALeART0GtlNAAAAAElFTkSuQmCC', 'base64');
  fs.writeFileSync(image, imageBytes);
  fs.writeFileSync(document, 'hello file');
  let requestMessageId;
  const stages = [];
  const session = protocolSession({
    conversationId: 'conv-upload',
    onConversationPost: body => {
      stages.push('submit');
      requestMessageId = body.messages[0].id;
      const message = body.messages[0];
      assert.equal(message.content.content_type, 'multimodal_text');
      assert.deepEqual(message.content.parts, [{
        content_type: 'image_asset_pointer',
        asset_pointer: 'file-service://file-1',
        size_bytes: imageBytes.length,
        width: 1,
        height: 1
      }, 'describe these']);
      assert.deepEqual(message.metadata.attachments, [
        { id: 'file-1', name: 'sample.png', mimeType: 'image/png', size: imageBytes.length, width: 1, height: 1 },
        { id: 'file-2', name: 'sample.txt', mimeType: 'text/plain', size: 10 }
      ]);
    },
    onConversationRead: () => jsonResponse(completedConversation('conv-upload', requestMessageId, 'processed'))
  });
  const originalFetch = session.fetch;
  let nextId = 0;
  session.fetch = async (url, options = {}) => {
    const target = new URL(String(url));
    if (target.pathname === '/backend-api/f/conversation/prepare') {
      const body = JSON.parse(options.body);
      assert.equal(body.partial_query.content.content_type, 'text');
      assert.deepEqual(body.partial_query.content.parts, ['describe these']);
      stages.push('prepare');
    }
    if (target.pathname === '/backend-api/files') {
      const body = JSON.parse(options.body);
      assert.equal(body.entry_surface, 'chat_composer');
      assert.equal(body.use_case, nextId === 0 ? 'multimodal' : 'my_files');
      assert.equal(body.mime_type, nextId === 0 ? 'image/png' : 'text/plain');
      const id = 'file-' + (++nextId);
      stages.push('create-' + id);
      return jsonResponse({ status: 'success', file_id: id,
        upload_url: id === 'file-1'
          ? 'https://uploads.example.net/test/file-1'
          : 'https://uploads.blob.core.windows.net/test/' + id });
    }
    if (['uploads.example.net', 'uploads.blob.core.windows.net'].includes(target.hostname)) {
      assert.equal(options.method, 'PUT');
      assert.equal(options.credentials, 'omit');
      assert.equal(options.headers['x-ms-blob-type'], 'BlockBlob');
      assert.equal(options.headers['x-ms-version'], '2020-04-08');
      const id = target.pathname.split('/').at(-1);
      stages.push('transfer-' + id);
      return textResponse('', 201);
    }
    if (target.pathname === '/backend-api/files/process_upload_stream') {
      const body = JSON.parse(options.body);
      assert.equal(options.method, 'POST');
      assert.equal(body.entry_surface, 'chat_composer');
      const id = body.file_id;
      stages.push('process-' + id);
      return textResponse('data: ' + JSON.stringify({
        event: 'file.processing.completed', file_id: id
      }) + String.fromCharCode(10, 10), 200, 'text/event-stream');
    }
    return originalFetch(url, options);
  };
  try {
    const result = await chatgpt.operations.newChat({
      ...nativeBrowserSurface(session), session,
      args: { prompt: 'describe these', files: [
        { path: image, name: 'sample.png', contentType: 'image/png' },
        { path: document, name: 'sample.txt', contentType: 'text/plain' }
      ] },
      sleep: async () => {}
    });
    assert.equal(result.text, 'processed');
    assert.deepEqual(stages, [
      'create-file-1', 'transfer-file-1', 'process-file-1',
      'create-file-2', 'transfer-file-2', 'process-file-2', 'prepare', 'submit'
    ]);
    assert.equal(session.__native.posts, 1);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('ChatGPT Account uploads on an existing conversation with JSON-lines processing', async () => {
  const chatgpt = loadChatGptModule();
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'mezhs-upload-existing-'));
  const document = path.join(dir, 'follow-up.txt');
  fs.writeFileSync(document, 'follow-up file');
  let requestMessageId;
  const stages = [];
  const session = protocolSession({
    conversationId: 'conv-existing-upload',
    onConversationPost: body => {
      stages.push('submit');
      requestMessageId = body.messages[0].id;
      assert.equal(body.conversation_id, 'conv-existing-upload');
      assert.equal(body.parent_message_id, 'assistant-existing');
      assert.equal(body.messages[0].content.content_type, 'text');
      assert.deepEqual(body.messages[0].content.parts, ['read this follow-up']);
      assert.deepEqual(body.messages[0].metadata.attachments, [
        { id: 'file-existing', name: 'follow-up.txt', mimeType: 'text/plain', size: 14 }
      ]);
    },
    onConversationRead: () => jsonResponse(
      completedConversation('conv-existing-upload', requestMessageId, 'existing processed')
    )
  });
  const originalFetch = session.fetch;
  session.fetch = async (url, options = {}) => {
    const target = new URL(String(url));
    if (target.pathname === '/backend-api/files') {
      stages.push('create');
      return jsonResponse({
        status: 'success',
        file_id: 'file-existing',
        upload_url: 'https://uploads.blob.core.windows.net/test/file-existing'
      });
    }
    if (target.hostname === 'uploads.blob.core.windows.net') {
      stages.push('transfer');
      return textResponse('', 201);
    }
    if (target.pathname === '/backend-api/files/process_upload_stream') {
      stages.push('process');
      return textResponse([
        { event: 'file.processing.started', file_id: 'file-existing', progress: 0 },
        { event: 'file.processing.file_ready', file_id: 'file-existing', progress: 50 },
        { event: 'file.processing.progress', file_id: 'file-existing', progress: 90 },
        { event: 'file.processing.completed', file_id: 'file-existing', progress: 100 }
      ].map(JSON.stringify).join(String.fromCharCode(10)), 200, 'text/event-stream');
    }
    return originalFetch(url, options);
  };

  try {
    const result = await chatgpt.operations.send({
      ...nativeBrowserSurface(session),
      session,
      args: {
        prompt: 'read this follow-up',
        conversationId: 'conv-existing-upload',
        parentMessageId: 'assistant-existing',
        files: [
          { path: document, name: 'follow-up.txt', contentType: 'text/plain' }
        ]
      },
      sleep: async () => {}
    });

    assert.equal(result.text, 'existing processed');
    assert.deepEqual(stages, ['create', 'transfer', 'process', 'submit']);
    assert.equal(session.__native.posts, 1);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('ChatGPT Account stops before semantic submission when attachment upload fails', async t => {
  const chatgpt = loadChatGptModule();
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'mezhs-upload-errors-'));
  const document = path.join(dir, 'sample.txt');
  fs.writeFileSync(document, 'hello file');
  try {
    for (const failure of ['create', 'transfer', 'transfer-code', 'transfer-unsafe-code', 'process-http', 'process-event', 'process-incomplete', 'process-empty', 'process-json', 'process-ndjson-mismatch', 'process-ndjson-incomplete', 'process-ndjson-failed', 'multipart', 'estuary', 'unsafe-url']) {
      await t.test(failure, async () => {
        const session = protocolSession({
          conversationId: 'conv-upload-error',
          onConversationRead: () => { throw Error('Unexpected conversation read'); }
        });
        const originalFetch = session.fetch;
        session.fetch = async (url, options = {}) => {
          const target = new URL(String(url));
          if (target.pathname === '/backend-api/files') return failure === 'multipart'
            ? jsonResponse({ status: 'success', file_id: 'file-1', upload_url: 'https://uploads.blob.core.windows.net/test/file-1', direct_library_upload_strategy: { kind: 'direct_azure_multipart', part_count: 2 } })
            : failure === 'estuary'
            ? jsonResponse({ status: 'success', file_id: 'file-1', upload_url: 'https://chatgpt.com/backend-api/estuary/upload_content_bytes' })
            : failure === 'unsafe-url'
            ? jsonResponse({ status: 'success', file_id: 'file-1', upload_url: 'http://uploads.blob.core.windows.net/test/file-1' })
            : failure === 'create'
            ? jsonResponse({ status: 'error', error_code: 'quota' })
            : jsonResponse({ status: 'success', file_id: 'file-1',
              upload_url: 'https://uploads.blob.core.windows.net/test/file-1' });
          if (target.hostname === 'uploads.blob.core.windows.net') {
            if (failure === 'transfer-code' || failure === 'transfer-unsafe-code')
              return new Response('PRIVATE_RESPONSE_BODY', {
                status: 400, headers: { 'x-ms-error-code': failure === 'transfer-code'
                  ? 'InvalidHeaderValue' : 'Bad https://private.signed.url' }
              });
            return textResponse('', failure === 'transfer' ? 403 : 201);
          }
          if (target.pathname === '/backend-api/files/process_upload_stream') {
            if (failure === 'process-http') return textResponse('error', 500);
            if (failure === 'process-empty') return textResponse('', 200, 'text/event-stream');
            if (failure === 'process-json') return jsonResponse({ status: 'success', private_value: 'PRIVATE_RESPONSE_BODY', upload_url: 'https://private.signed.url' });
            if (failure.startsWith('process-ndjson-')) return textResponse([
              { event: 'file.processing.started', file_id: 'file-1', status: 'processing', private_value: 'PRIVATE_RESPONSE_BODY' },
              ...(failure === 'process-ndjson-failed' ? [{ event: 'file.processing.file_ready', file_id: 'file-1', status: 'ready' }] : []),
              ...(failure === 'process-ndjson-incomplete' ? [] : [{ event: failure === 'process-ndjson-failed' ? 'file.processing.failed' : 'file.processing.completed', file_id: failure === 'process-ndjson-mismatch' ? 'file-different' : 'file-1', status: 'completed', upload_url: 'https://private.signed.url' }])
            ].map(JSON.stringify).join(String.fromCharCode(10)), 200, 'text/event-stream');
            const event = failure === 'process-event'
              ? 'file.processing.failed' : 'file.processing.in_progress';
            return textResponse('data: ' + JSON.stringify({ event, file_id: 'file-1', private_value: 'PRIVATE_RESPONSE_BODY', upload_url: 'https://private.signed.url' }) +
              String.fromCharCode(10, 10), 200, 'text/event-stream');
          }
          return originalFetch(url, options);
        };
        await assert.rejects(chatgpt.operations.newChat({
          ...nativeBrowserSurface(session), session,
          args: { prompt: 'should not send', files: [
            { path: document, name: 'sample.txt', contentType: 'text/plain' }
          ] },
          sleep: async () => {}
        }), error => {
          if (failure === 'transfer-code') assert.match(error.message, /HTTP 400 [(]InvalidHeaderValue[)]/);
          if (failure === 'transfer-unsafe-code') assert.match(error.message, /HTTP 400[.]/);
          if (failure === 'process-event') assert.match(error.message, /HTTP 200; type=text\/event-stream; size=\d+; shape=sse; jsonStatus=none; lines=1; samples=sse; events=file[.]processing[.]failed[(]topId=match,dataId=absent[)]/);
          if (failure === 'process-incomplete') assert.match(error.message, /HTTP 200; type=text\/event-stream; size=\d+; shape=sse; jsonStatus=none; lines=1; samples=sse; events=file[.]processing[.]in_progress[(]topId=match,dataId=absent[)]/);
          if (failure === 'process-empty') assert.match(error.message, /HTTP 200; type=text\/event-stream; size=0; shape=empty; jsonStatus=none; lines=0; samples=none; events=none;/);
          if (failure === 'process-json') assert.match(error.message, /HTTP 200; type=application\/json; size=\d+; shape=json-object; jsonStatus=success; lines=1; samples=object[^;]+; events=unnamed[(]topId=absent,dataId=absent[)];/);
          if (failure.startsWith('process-ndjson-')) {
            if (failure === 'process-ndjson-incomplete') assert.match(error.message, /shape=json-object; jsonStatus=processing; lines=1; samples=object/);
            else assert.match(error.message, /shape=json-object; jsonStatus=invalid-json; lines=[23]; samples=object/);
            assert.match(error.message, /events=file[.]processing[.]started[(]topId=match,dataId=absent[)]/);
            if (failure === 'process-ndjson-mismatch') assert.match(error.message, /file[.]processing[.]completed[(]topId=different,dataId=absent[)]/);
            if (failure === 'process-ndjson-failed') assert.match(error.message, /file[.]processing[.]failed[(]topId=match,dataId=absent[)]/);
            if (failure === 'process-ndjson-incomplete') assert.doesNotMatch(error.message, /file[.]processing[.]completed/);
          }
          assert.doesNotMatch(error.message, /PRIVATE_RESPONSE_BODY|private[.]signed[.]url/);
          return true;
        });
        assert.equal(session.__native.posts, 0, 'must not send a prompt after an upload failure');
      });
    }
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('ChatGPT Account chains document and PNG existing-conversation attachments without duplicate turns', async () => {
  const chatgpt = loadChatGptModule();
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'mezhs-chained-followups-'));
  const document = path.join(dir, 'follow-up.txt');
  const image = path.join(dir, 'follow-up.png');
  const posts = [];
  const prepareContents = [];
  const assistantIds = ['assistant-after-document', 'assistant-after-image'];
  const conversationId = 'conv-chained-attachments';
  fs.writeFileSync(document, 'unique document contents');
  fs.writeFileSync(
    image,
    Buffer.from(
      'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGM4YS4CAALeART0GtlNAAAAAElFTkSuQmCC',
      'base64',
    ),
  );
  const session = protocolSession({
    conversationId,
    onConversationPost: (body) => {
      assert.equal(body.conversation_id, conversationId);
      assert.equal(
        body.parent_message_id,
        posts.length === 0 ? 'assistant-existing' : assistantIds[0],
      );
      assert.equal(body.messages.length, 1);
      posts.push(body);
    },
    onConversationRead: () => {
      const index = posts.length - 1;
      const result = completedConversation(
        conversationId,
        posts[index].messages[0].id,
        index === 0 ? 'document received' : 'image received',
      );
      const assistant = result.mapping['assistant-new'];
      delete result.mapping['assistant-new'];
      assistant.message.id = assistantIds[index];
      result.mapping[assistantIds[index]] = assistant;
      result.current_node = assistantIds[index];
      return jsonResponse(result);
    },
  });
  const originalFetch = session.fetch;
  let created = 0;
  session.fetch = async (url, options = {}) => {
    const target = new URL(String(url));
    if (target.pathname === '/backend-api/f/conversation/prepare') {
      prepareContents.push(JSON.parse(options.body).partial_query.content);
      return originalFetch(url, options);
    }
    if (target.pathname === '/backend-api/files') {
      created++;
      return jsonResponse({
        status: 'success',
        file_id: 'chained-file-' + created,
        upload_url: 'https://uploads.blob.core.windows.net/test/chained-file-' + created,
      });
    }
    if (target.hostname === 'uploads.blob.core.windows.net') return textResponse('', 201);
    if (target.pathname === '/backend-api/files/process_upload_stream') {
      const id = JSON.parse(options.body).file_id;
      return textResponse(
        'data: ' +
          JSON.stringify({ event: 'file.processing.completed', file_id: id }) +
          String.fromCharCode(10, 10),
        200,
        'text/event-stream',
      );
    }
    return originalFetch(url, options);
  };
  try {
    const first = await chatgpt.operations.send({
      ...nativeBrowserSurface(session),
      session,
      args: {
        prompt: 'read attached document',
        conversationId,
        parentMessageId: 'assistant-existing',
        files: [{ path: document, name: 'follow-up.txt', contentType: 'text/plain' }],
      },
      sleep: async () => {},
    });
    assert.equal(first.text, 'document received');
    assert.equal(first.conversationId, conversationId);
    assert.equal(first.parentMessageId, assistantIds[0]);
    const second = await chatgpt.operations.send({
      ...nativeBrowserSurface(session),
      session,
      args: {
        prompt: 'describe attached image',
        conversationId: first.conversationId,
        parentMessageId: first.parentMessageId,
        files: [{ path: image, name: 'follow-up.png', contentType: 'image/png' }],
      },
      sleep: async () => {},
    });
    assert.equal(second.text, 'image received');
    assert.equal(second.conversationId, conversationId);
    assert.equal(second.parentMessageId, assistantIds[1]);
    assert.notEqual(second.parentMessageId, first.parentMessageId);
    assert.equal(session.__native.posts, 2);
    assert.equal(posts.length, 2);
    assert.equal(created, 2);
    assert.deepEqual(
      posts.map((b) => b.messages[0].metadata.attachments[0].mimeType),
      ['text/plain', 'image/png'],
    );
    assert.deepEqual(
      posts.map((b) => b.messages[0].metadata.attachments[0].id),
      ['chained-file-1', 'chained-file-2'],
    );
    assert.equal(posts[0].messages[0].content.content_type, 'text');
    assert.equal(posts[1].messages[0].content.content_type, 'multimodal_text');
    assert.deepEqual(posts[1].messages[0].content.parts[0], {
      content_type: 'image_asset_pointer',
      asset_pointer: 'file-service://chained-file-2',
      size_bytes: fs.statSync(image).size,
      width: 1,
      height: 1,
    });
    assert.deepEqual(
      prepareContents.map((c) => c.content_type),
      ['text', 'text'],
    );
    assert.deepEqual(prepareContents[1].parts, ['describe attached image']);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
