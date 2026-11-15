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
  assert.equal(output.statusCode, 200);
  const payload = JSON.parse(output.body);
  assert.equal(payload.versions[0].id, "proof");
  assert.equal(payload.models[0].slug, "m1");
  assert.equal(calls.length, 2);
  assert.ok(progress.some(value => value.state === "preparing"));
  assert.ok(progress.some(value => value.state === "retrieving" && /bytes/.test(value.detail)));
});

test("typed HTTP rejects unsupported methods, origin escapes and invalid request bodies before authentication", async () => {
  let contacted = false;
  const authenticated = session(async () => {
    contacted = true;
    throw new Error("Unauthorized request was dispatched.");
  });
  await assert.rejects(chatgpt.operations.typedHttp({
    session: authenticated, args: { method: "DELETE", path: modelsPath }
  }), /not allowed/);
  await assert.rejects(chatgpt.operations.typedHttp({
    session: authenticated, args: { method: "GET", path: "https://example.org/" }
  }), /not allowed/);
  await assert.rejects(chatgpt.operations.typedHttp({
    session: authenticated, args: { method: "GET", path: modelsPath, body: {} }
  }), /not allowed/);
  await assert.rejects(chatgpt.operations.typedHttp({
    session: authenticated, args: { method: "GET", path: "/backend-api/../api/auth/session" }
  }), /not allowed/);
  await assert.rejects(chatgpt.operations.typedHttp({
    session: authenticated, args: { method: "POST", path: modelsPath, body: { message: "raw-object" } }
  }), /not allowed/);
  await assert.rejects(chatgpt.operations.typedHttp({
    session: authenticated, args: { method: "POST", path: modelsPath, body: "{}", accept: "Authorization: Bearer leaked" }
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

test("typed POST/PATCH send exact serialized JSON once and stream remains raw for C# interpretation", async () => {
  const calls = [];
  const progress = [];
  const authenticated = session(async (url, options = {}) => {
    if (String(url).endsWith("/api/auth/session"))
      return json({ accessToken: "test" });
    calls.push({ url, options });
    return new Response("data: {\\"part\\":1}\\n\\ndata: [DONE]\\n\\n", {
      status: 200, headers: { "content-type": "text/event-stream" }
    });
  });
  for (const method of ["POST", "PATCH"]) {
    const body = JSON.stringify({ messageId: "id-1", value: 42 });
    const result = await chatgpt.operations.typedHttp({
      session: authenticated,
      args: { method, path: "/backend-api/test/typed", body, accept: "text/event-stream" },
      reportProgress: value => progress.push(value)
    });
    assert.equal(result.statusCode, 200);
    assert.equal(result.contentType, "text/event-stream");
    assert.equal(result.body, "data: {\\"part\\":1}\\n\\ndata: [DONE]\\n\\n");
  }
  assert.equal(calls.length, 2, "one native fetch per mutation; never replay");
  assert.deepEqual(calls.map(call => call.options.method), ["POST", "PATCH"]);
  for (const call of calls) {
    assert.equal(call.options.body, JSON.stringify({ messageId: "id-1", value: 42 }));
    assert.equal(call.options.headers.Accept, "text/event-stream");
    assert.equal(call.options.headers.Authorization, "Bearer test");
  }
  assert.ok(progress.some(x => x.state === "retrieving"));
});

test("typed HTTP ambiguous POST failure is surfaced without replay", async () => {
  let submitted = 0;
  const authenticated = session(async url => {
    if (String(url).endsWith("/api/auth/session"))
      return json({ accessToken: "test" });
    submitted++;
    throw new Error("connection interrupted after submission");
  });
  await assert.rejects(chatgpt.operations.typedHttp({
    session: authenticated,
    args: { method: "POST", path: "/backend-api/test/typed", body: "{}" }
  }), /connection interrupted/);
  assert.equal(submitted, 1);
});
