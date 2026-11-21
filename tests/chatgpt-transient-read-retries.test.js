const test = require("node:test");
const assert = require("node:assert/strict");
const { accessToken, apiFetch, apiJson } =
  require("../integrations/Mezhs.Integrations.ChatGpt/browser/modules/account-transport.js");
const chatgpt = require("../integrations/Mezhs.Integrations.ChatGpt/browser/modules/entry.js");

function session(fetch) {
  return {
    fetch,
    cookies: { get: async () => [], set: async () => {} }
  };
}

function success(body) {
  return new Response(JSON.stringify(body), { status: 200 });
}

function interruptedBody() {
  return new Response(new ReadableStream({
    start(controller) {
      controller.error(new Error("net::ERR_HTTP2_PROTOCOL_ERROR"));
    }
  }), { status: 200 });
}

test("ChatGPT retries a transient account-session read", async () => {
  let attempts = 0;
  const browser = session(async () => {
    if (++attempts === 1)
      throw new Error("net::ERR_HTTP2_PROTOCOL_ERROR");
    return success({ accessToken: "access-token" });
  });
  assert.equal(await accessToken(browser), "access-token");
  assert.equal(attempts, 2);
});

test("ChatGPT retries a failed GET response body and redacts conversation IDs in diagnostics", async () => {
  let attempts = 0;
  const lines = [];
  const original = console.error;
  console.error = (...args) => lines.push(args.join(" "));
  try {
    const browser = session(async () => {
      attempts++;
      return attempts === 1 ? interruptedBody() : success({ status: "complete" });
    });
    const state = await apiJson(browser, "token", "/backend-api/conversation/secret-conversation-id");
    assert.deepEqual(state, { status: "complete" });
    assert.equal(attempts, 2);
    assert.ok(lines.some(line => line.includes("GET /backend-api/conversation/:id")));
    assert.ok(lines.some(line => line.includes("ERR_HTTP2_PROTOCOL_ERROR")));
    assert.ok(lines.every(line => !line.includes("secret-conversation-id") && !line.includes("token")));
  } finally {
    console.error = original;
  }
});

test("ChatGPT exhausts bounded retries and preserves the original network error", async () => {
  let attempts = 0;
  const browser = session(async () => {
    attempts++;
    throw new Error("net::ERR_NETWORK_IO_SUSPENDED");
  });
  await assert.rejects(
    apiJson(browser, "token", "/backend-api/conversation/secret"),
    /ERR_NETWORK_IO_SUSPENDED/
  );
  assert.equal(attempts, 4, "initial read and three bounded retries");
});

test("ChatGPT does not retry non-transient exceptions or provider HTTP errors", async () => {
  let attempts = 0;
  const browser = session(async () => {
    attempts++;
    throw new Error("unrelated application error");
  });
  await assert.rejects(apiJson(browser, "token", "/backend-api/models"), /unrelated application error/);
  assert.equal(attempts, 1);

  let requests = 0;
  const refused = session(async () => {
    requests++;
    return new Response(JSON.stringify({ detail: { code: "conversation_inaccessible" } }), {
      status: 404
    });
  });
  await assert.rejects(
    apiJson(refused, "token", "/backend-api/conversation/unavailable"),
    error => error.status === 404 && error.providerCode === "conversation_inaccessible"
  );
  assert.equal(requests, 1);
});

test("ChatGPT never replays a failed POST even with a transient error code", async () => {
  let posts = 0;
  const browser = session(async (_url, options) => {
    assert.equal(options.method, "POST");
    posts++;
    throw new Error("net::ERR_HTTP2_PROTOCOL_ERROR");
  });
  await assert.rejects(
    apiFetch(browser, "token", "/backend-api/f/conversation", {
      method: "POST",
      body: JSON.stringify({ action: "next" })
    }),
    /ERR_HTTP2_PROTOCOL_ERROR/
  );
  assert.equal(posts, 1);
});

test("ChatGPT recovers conversation polling after submission without a second prompt POST", async () => {
  let posts = 0;
  let reads = 0;
  let requestMessageId;
  const conversationId = "conv-read-retry";
  const browser = session(async (url, options = {}) => {
    const endpoint = new URL(url).pathname;
    if (endpoint === "/api/auth/session")
      return success({ accessToken: "token" });
    if (endpoint === "/backend-api/settings/user_last_used_model_config")
      return new Response("");
    if (endpoint === "/backend-api/f/conversation/prepare")
      return success({ conduit_token: "conduit" });
    if (endpoint === "/backend-api/f/conversation") {
      posts++;
      requestMessageId = JSON.parse(options.body).messages[0].id;
      return new Response(
        'data: {"conversation_id":"conv-read-retry"}\n\ndata: [DONE]\n\n',
        { status: 200, headers: { "content-type": "text/event-stream" } }
      );
    }
    if (endpoint === "/backend-api/conversation/conv-read-retry") {
      reads++;
      if (reads === 1)
        throw new Error("net::ERR_HTTP2_PROTOCOL_ERROR");
      return success({
        conversation_id: conversationId,
        current_node: "assistant-final",
        mapping: {
          "assistant-final": {
            parent: "request",
            message: {
              id: "assistant-final",
              author: { role: "assistant" },
              status: "finished_successfully",
              content: { parts: ["recovered answer"] }
            }
          },
          request: {
            parent: null,
            message: {
              id: requestMessageId,
              author: { role: "user" },
              status: "finished_successfully"
            }
          }
        }
      });
    }
    throw new Error("Unexpected endpoint " + endpoint);
  });
  const result = await chatgpt.operations.newChat({
    session: browser,
    window: {
      webContents: {
        executeJavaScript: async () => ({
          headers: { "OpenAI-Sentinel-Chat-Requirements-Token": "native-token" },
          forceLogin: false
        })
      }
    },
    args: { prompt: "one request", files: [] },
    sleep: async () => {}
  });
  assert.equal(posts, 1);
  assert.equal(reads, 2);
  assert.equal(result.text, "recovered answer");
});
