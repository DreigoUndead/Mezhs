// Regression tests for unavailable remote conversations without resurrecting
// the deleted JavaScript Account send/receive protocol.
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { executeBrowserHttp } = require("../electron/browser-http");
const chatgpt = require("../integrations/Mezhs.Integrations.ChatGpt/browser/modules/entry.js");

const directory = path.resolve(__dirname, "../integrations/Mezhs.Integrations.ChatGpt");
const request = { method: "GET", path: "/backend-api/conversation/stale-conversation" };

function session(backendResponse) {
  return {
    cookies: { get: async () => [], set: async () => {} },
    async fetch(url) {
      const endpoint = new URL(String(url)).pathname;
      if (endpoint === "/api/auth/session")
        return new Response(JSON.stringify({ accessToken: "access-token" }), {
          status: 200, headers: { "content-type": "application/json" }
        });
      if (endpoint === request.path) return backendResponse();
      throw Error("Unexpected provider endpoint.");
    }
  };
}

function responseWithCode(code) {
  return new Response(JSON.stringify({
    detail: { message: "Private provider error text", code, can_retry: false }
  }), { status: 404, headers: { "content-type": "application/json" } });
}

test("preflight preserves safe conversation_inaccessible code for C# restoration", async () => {
  const result = await executeBrowserHttp({
    session: session(() => responseWithCode("conversation_inaccessible")),
    args: request
  }, chatgpt.http);
  assert.equal(result.statusCode, 404);
  assert.equal(result.providerCode, "conversation_inaccessible");
  assert.equal(result.body, "");
  assert.doesNotMatch(JSON.stringify(result), /Private provider error text|access-token/);
  const typedProtocol = fs.readFileSync(path.join(directory,
    "ChatGptAccountProtocol.cs"), "utf8");
  assert.match(typedProtocol, /error\.ProviderCode == "conversation_inaccessible"/);
  assert.match(typedProtocol, /ConversationUnavailable: true/);
});

test("unrelated conversation 404 is not treated as inaccessible-remote recovery", async () => {
  const result = await executeBrowserHttp({
    session: session(() => responseWithCode("some_other_error")),
    args: request
  }, chatgpt.http);
  assert.equal(result.statusCode, 404);
  assert.equal(result.providerCode, "some_other_error");
  const typedProtocol = fs.readFileSync(path.join(directory,
    "ChatGptAccountProtocol.cs"), "utf8");
  assert.match(typedProtocol, /error\.ProviderCode == "conversation_inaccessible"/);
  assert.doesNotMatch(JSON.stringify(result), /Private provider error text/);
});

test("non-JSON failures expose HTTP status but never response bodies", async () => {
  const result = await executeBrowserHttp({
    session: session(() => new Response("private-server-response-secret", { status: 503 })),
    args: request
  }, chatgpt.http);
  assert.equal(result.statusCode, 503);
  assert.equal(result.body, "");
  assert.equal(result.providerCode, null);
  assert.doesNotMatch(JSON.stringify(result), /private-server-response-secret/);
});

test("401 after session validation preserves status for C# reauthorization", async () => {
  const result = await executeBrowserHttp({
    session: session(() => new Response(null, { status: 401 })),
    args: request
  }, chatgpt.http);
  assert.equal(result.statusCode, 401);
  const executor = fs.readFileSync(path.resolve(__dirname,
    "../src/Mezhs.Integration.Browser/BrowserHttpExecutor.cs"), "utf8");
  assert.match(executor, /HttpStatusCode\.Unauthorized/);
  assert.match(executor, /BrowserAuthorizationRequiredException/);
});

test("C# stream reconciliation is never a hidden replay after an uncertain POST", () => {
  const typedProtocol = fs.readFileSync(path.join(directory,
    "ChatGptAccountProtocol.cs"), "utf8");
  assert.match(typedProtocol, /must not be resent/);
  assert.match(typedProtocol, /ChatGptConversationState\.WaitAsync/);
  assert.doesNotMatch(typedProtocol, /RetryIfSafe|while\s*\([^)]*post/i);
  const entry = require(path.join(directory, "browser/modules/entry.js"));
  assert.equal(entry.operations.newChat, undefined);
  assert.equal(entry.operations.send, undefined);
});
