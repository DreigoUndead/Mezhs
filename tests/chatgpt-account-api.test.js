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
  thinkingEffort = null
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
            ...(thinkingEffort ? { thinking_effort: thinkingEffort } : {})
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

function nativeBrowserSurface(session) {
  const state = session.__native;
  if (!state)
    throw new Error("Native browser fixture requires protocolSession().");

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
        state.streamResourceRequestId = args.requestId;
        return {
          bufferedData: Buffer.from(state.lastStream || "", "utf8").toString("base64")
        };
      }
      if (method === "Network.getResponseBody") {
        if (state.responseBodyUnavailable)
          throw new Error("No resource with given identifier found");
        return { body: state.lastStream || "", base64Encoded: false };
      }
      throw new Error(`Unexpected debugger command ${method}`);
    }
  }

  const debug = new NativeDebugger();
  let currentUrl = "https://chatgpt.com/";
  let composerText = "";
  let selectedAll = false;

  return {
    window: {
      loadURL: async url => {
        currentUrl = String(url);
        composerText = String(state.draft || "");
        selectedAll = false;

        if (!state.ignoreModelCookie) {
          const encoded = session.__cookies?.get("oai-last-model-config");
          if (encoded) {
            const preference = JSON.parse(decodeURIComponent(encoded));
            state.model = preference.model || state.model;
            state.thinkingEffort = preference.effort ?? null;
          }
        }
      },
      webContents: {
        debugger: debug,
        getURL: () => currentUrl,
        executeJavaScript: async source => {
          assert.doesNotThrow(() => new Function(`return ${source};`));
          return { ok: true };
        },
        selectAll: () => {
          selectedAll = true;
          state.selectAllCalls = (state.selectAllCalls || 0) + 1;
        },
        insertText: async text => {
          composerText = selectedAll
            ? String(text)
            : composerText + String(text);
          selectedAll = false;
        },
        sendInputEvent: event => {
          if (event.type !== "keyDown" || event.keyCode !== "Enter")
            return;

          const url = new URL(currentUrl);
          const continuation = /^\/c\/([^/]+)$/.exec(url.pathname);
          const project = /^\/g\/(g-p-[^/]+)\/project$/.exec(url.pathname);
          const requestMessageId = `request-${++state.posts}`;
          const body = {
            action: "next",
            model: state.model || "gpt-5-6-thinking",
            parent_message_id: continuation ? "assistant-old" : "client-created-root",
            client_prepare_state: "success",
            supported_encodings: ["v1"],
            messages: [{
              id: requestMessageId,
              author: { role: "user" },
              content: { content_type: "text", parts: [composerText] }
            }],
            ...(state.thinkingEffort
              ? { thinking_effort: state.thinkingEffort }
              : {}),
            ...(continuation
              ? { conversation_id: decodeURIComponent(continuation[1]) }
              : {}),
            ...(project
              ? {
                  conversation_mode: {
                    kind: "gizmo_interaction",
                    gizmo_id: decodeURIComponent(project[1])
                  }
                }
              : {})
          };

          state.onConversationPost?.(body);
          state.lastStream = state.conversationStream?.(body) ??
            `data: {"conversation_id":"${state.conversationId}"}\n\ndata: [DONE]\n\n`;

          const requestId = `native-${state.posts}`;
          debug.emit("message", {}, "Network.requestWillBeSent", {
            requestId,
            request: {
              url: "https://chatgpt.com/backend-api/f/conversation",
              method: "POST",
              headers: {},
              postData: JSON.stringify(body)
            }
          });

          if (state.abortBeforeResponse) {
            debug.emit("message", {}, "Network.loadingFailed", {
              requestId,
              errorText: "net::ERR_ABORTED"
            });
            return;
          }

          debug.emit("message", {}, "Network.responseReceived", {
            requestId,
            response: { status: 200 }
          });

          if (!continuation && state.conversationId && !state.stayOnProjectUrl)
            currentUrl = `https://chatgpt.com/c/${state.conversationId}`;

          if (state.emitConversationInitRequest) {
            debug.emit("message", {}, "Network.requestWillBeSent", {
              requestId: `${requestId}-init`,
              request: {
                url: "https://chatgpt.com/backend-api/conversation/init",
                method: "POST",
                headers: {}
              }
            });
          }

          if (state.emitConversationReadRequest && state.conversationId) {
            debug.emit("message", {}, "Network.requestWillBeSent", {
              requestId: `${requestId}-conversation`,
              request: {
                url: `https://chatgpt.com/backend-api/conversation/${state.conversationId}`,
                method: "GET",
                headers: {}
              }
            });
          }

          if (state.abortAfterResponse) {
            debug.emit("message", {}, "Network.loadingFailed", {
              requestId,
              errorText: "net::ERR_ABORTED"
            });
          } else {
            debug.emit("message", {}, "Network.loadingFinished", { requestId });
          }
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

    if (target.pathname === `/backend-api/conversation/${conversationId}`)
      return onConversationRead();

    throw new Error(`Unexpected request ${target}`);
  });
  session.__native = state;
  return session;
}

test("ChatGPT account newChat submits through the native composer and reads the semantic conversation API", async () => {
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
  assert.equal(session.__native.selectAllCalls, 1);
  assert.equal(result.conversationId, "conv-api");
  assert.equal(result.text, "API_OK");
});

test("ChatGPT native page loads the requested Instant model from the preference cookie", async () => {
  const chatgpt = loadChatGptModule();
  let requestMessageId;

  const session = protocolSession({
    conversationId: "conv-instant",
    onConversationPost: body => {
      requestMessageId = body.messages[0].id;
      assert.equal(body.model, "gpt-5-6-instant");
      assert.equal(body.thinking_effort ?? null, null);
    },
    onConversationRead: () =>
      jsonResponse(completedConversation(
        "conv-instant",
        requestMessageId,
        "INSTANT_OK",
        "gpt-5-6-instant",
        "gpt-5-6-instant"
      ))
  });

  session.__native.model = "gpt-5-6-thinking";
  session.__native.thinkingEffort = "extended";

  const result = await chatgpt.operations.newChat({
    ...nativeBrowserSurface(session),
    session,
    args: { prompt: "instant", model: "gpt-5-6-instant", files: [] },
    sleep: async () => {}
  });

  assert.equal(session.__native.patchedModel, "gpt-5-6-instant");
  assert.equal(session.__native.patchedThinkingEffort, null);
  assert.equal(
    JSON.parse(decodeURIComponent(
      session.__cookies.get("oai-last-model-config")
    )).model,
    "gpt-5-6-instant"
  );
  assert.equal(result.model, "gpt-5-6-instant");
});

test("ChatGPT continues after a successful native response is renderer-aborted", async () => {
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
  assert.equal(session.__native.streamResourceRequestId, "native-1");
  assert.equal(result.conversationId, "conv-aborted");
  assert.equal(result.text, "ABORTED_STREAM_OK");
});

test("ChatGPT still fails when the native request aborts before a response", async () => {
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
    /Native ChatGPT request failed: net::ERR_ABORTED/
  );
});

test("ChatGPT follow-up ignores a stale assistant until the native user message appears in ancestry", async () => {
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
        "gpt-5-6-instant",
        "gpt-5-6-thinking"
      ))
  });

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

  assert.equal(result.model, "gpt-5-6-instant");
  assert.ok(progress.some(value =>
    value.state === "completed" &&
    value.detail.includes("served model gpt-5-6-instant") &&
    value.detail.includes("requested model gpt-5-6-thinking")
  ));
});

test("ChatGPT conversation polling backs off on 429 without resending the native turn", async () => {
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

test("ChatGPT fails stalled state polling without replaying a native send", async () => {
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
