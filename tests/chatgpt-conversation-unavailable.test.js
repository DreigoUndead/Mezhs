// Regression tests for ChatGPT's expected inaccessible-conversation provider state.
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const root = path.resolve(__dirname, "..");

function loadChatGptModule() {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "mezhs-chatgpt-unavailable-test-"));
  fs.cpSync(path.join(root, "integrations", "Mezhs.Integrations.ChatGpt", "browser", "modules"), directory, { recursive: true });
  return require(path.join(directory, "entry.js"));
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

function noBrowserUi() {
  return {
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
  };
}

function send(chatgpt, session) {
  return chatgpt.operations.send({
    window: noBrowserUi(),
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

test("ChatGPT unrelated conversation 404 fails without exposing provider response text", async () => {
  const chatgpt = loadChatGptModule();
  await assert.rejects(
    send(chatgpt, sessionForConversationError("some_other_error")),
    error => {
      assert.equal(error.status, 404);
      assert.equal(error.providerCode, "some_other_error");
      assert.match(error.message, /ChatGPT \/backend-api\/conversation\/stale-conversation failed with HTTP 404/);
      assert.doesNotMatch(String(error.stack), /You don.t have access/);
      assert.equal(Object.hasOwn(error, "detail"), false);
      return true;
    }
  );
});

test("ChatGPT non-JSON HTTP errors retain status without logging response bodies", async () => {
  const { apiFetch } = require(path.join(root,
    "integrations/Mezhs.Integrations.ChatGpt/browser/modules/account-transport.js"));
  const session = {
    cookies: { get: async () => [] },
    fetch: async () => new Response("private-server-response-secret", { status: 503 })
  };
  await assert.rejects(apiFetch(session, "access-token", "/backend-api/f/conversation"), error => {
    assert.equal(error.status, 503);
    assert.equal(error.providerCode, null);
    assert.doesNotMatch(String(error.stack), /private-server-response-secret/);
    assert.doesNotMatch(JSON.stringify(error), /private-server-response-secret/);
    return true;
  });
});
