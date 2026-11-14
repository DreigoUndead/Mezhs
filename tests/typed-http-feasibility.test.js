const test = require("node:test");
const assert = require("node:assert/strict");
const { TextDecoder, TextEncoder } = require("node:util");
const modulePath = "../integrations/Mezhs.Integrations.ChatGpt/browser/modules/entry.js";
const chatgpt = require(modulePath);

const modelsPath = "/backend-api/models?history_and_training_disabled=false";

function session(fetcher) {
  return {
    fetch: fetcher,
    cookies: {
      get: async () => [],
      set: async () => {}
    }
  };
}

function json(value, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "content-type": "application/json" }
  });
}

test("typed feasibility endpoint reuses authenticated ChatGPT Electron session and reports streamed JSON progress", async () => {
  const calls = [];
  const progress = [];
  const token = "simulated-auth-token";
  const encoded = new TextEncoder();
  const pieces = [
    '{"versions":[{"id":"proof",',
    '"display_text_for_intelligence":"Proof"}],"models":[{"slug":"m1","title":"Model 1"}]}'
  ];
  const authenticated = session(async (url, options = {}) => {
    calls.push({ url: String(url), options });
    if (String(url).endsWith("/api/auth/session"))
      return json({ accessToken: token });
    assert.equal(options.headers.Authorization, "Bearer " + token);
    assert.equal(options.headers["X-OAI-IS-Client-Observation"], "v1.r.m");
    assert.equal(options.method, "GET");
    assert.ok(String(url).endsWith(modelsPath));

    let next = 0;
    const body = new ReadableStream({
      pull(controller) {
        if (next < pieces.length) controller.enqueue(encoded.encode(pieces[next++]));
        else controller.close();
      }
    });
    return new Response(body, { status: 200 });
  });

  const output = await chatgpt.operations.typedHttp({
    session: authenticated,
    args: { method: "GET", path: modelsPath, body: null },
    signal: new AbortController().signal,
    reportProgress: value => progress.push(value)
  });
  assert.equal(output.versions[0].id, "proof");
  assert.equal(output.models[0].slug, "m1");
  assert.equal(calls.length, 2);
  assert.ok(progress.some(value => value.state === "preparing"));
  assert.ok(progress.some(value => value.state === "retrieving" && /bytes/.test(value.detail)));
});

test("typed feasibility endpoint rejects unsupported endpoints before authentication", async () => {
  let contacted = false;
  const authenticated = session(async () => {
    contacted = true;
    throw new Error("Unauthorized request was dispatched.");
  });
  await assert.rejects(chatgpt.operations.typedHttp({
    session: authenticated, args: { method: "POST", path: modelsPath }
  }), /not allowed/);
  await assert.rejects(chatgpt.operations.typedHttp({
    session: authenticated, args: { method: "GET", path: "https://example.org/" }
  }), /not allowed/);
  await assert.rejects(chatgpt.operations.typedHttp({
    session: authenticated, args: { method: "GET", path: modelsPath, body: {} }
  }), /not allowed/);
  assert.equal(contacted, false);
});

test("typed feasibility endpoint propagates HTTP authorization errors without replay", async () => {
  let count = 0;
  const authenticated = session(async url => {
    if (String(url).endsWith("/api/auth/session"))
      return json({ accessToken: "expired" });
    count++;
    return json({ code: "unauthorized" }, 401);
  });

  await assert.rejects(chatgpt.operations.typedHttp({
    session: authenticated,
    args: { method: "GET", path: modelsPath, body: null }
  }), error => {
    assert.equal(error.code, "authorization_required");
    assert.equal(error.status, 401);
    return true;
  });
  assert.equal(count, 1);
});

test("typed feasibility endpoint aborts an in-flight native fetch when its operation is cancelled", async () => {
  let unblock;
  const started = new Promise(resolve => { unblock = resolve; });
  const authenticated = session(async (url, options = {}) => {
    if (String(url).endsWith("/api/auth/session"))
      return json({ accessToken: "token" });
    unblock();
    return await new Promise((_, reject) => {
      if (options.signal.aborted)
        return reject(new DOMException("Aborted", "AbortError"));
      options.signal.addEventListener("abort",
        () => reject(new DOMException("Aborted", "AbortError")), { once: true });
    });
  });
  const controller = new AbortController();
  const pending = chatgpt.operations.typedHttp({
    session: authenticated,
    args: { method: "GET", path: modelsPath, body: null },
    signal: controller.signal
  });
  await started;
  controller.abort();
  await assert.rejects(pending, error => error.name === "AbortError");
});
