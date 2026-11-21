const test = require("node:test");
const assert = require("node:assert/strict");
const { executeBrowserHttp, StreamFramer } = require("../electron/browser-http");

const policy = fetch => ({
  origin: "https://example.test",
  pathPrefix: "/backend-api/",
  fetch: async ({ options }) => fetch(options),
  nativeHooks: {}
});

test("SSE frames preserve order across split UTF-8 and multiple data lines", async () => {
  const events = [];
  const framer = new StreamFramer("text/event-stream", async value => events.push(value));
  const encoded = new TextEncoder().encode('data: {"message":"café"}\n\ndata: first\ndata: second\n\ndata: [DONE]\n\n');
  const splitAt = encoded.indexOf(0xc3) + 1;
  await framer.write(encoded.slice(0, splitAt));
  await framer.write(encoded.slice(splitAt));
  await framer.write(new Uint8Array(0), true);
  assert.deepEqual(events, ['{"message":"café"}', 'first\nsecond']);
});

test("NDJSON events reach C# while the fetch stream is still active", async () => {
  let finish;
  const gate = new Promise(resolve => { finish = resolve; });
  const encoder = new TextEncoder();
  let eventObserved;
  const firstEvent = new Promise(resolve => { eventObserved = resolve; });
  const events = [];
  const response = new Response(new ReadableStream({
    async start(controller) {
      controller.enqueue(encoder.encode('{"index":1}\n'));
      await gate;
      controller.enqueue(encoder.encode('{"index":2}\n'));
      controller.close();
    }
  }), { status: 200, headers: { "content-type": "application/x-ndjson" } });
  let completed = false;
  const pending = executeBrowserHttp({
    args: { method: "POST", path: "/backend-api/test", body: "{}",
      accept: "application/x-ndjson", stream: true },
    reportEvent: async value => {
      events.push(JSON.parse(value));
      if (events.length === 1) eventObserved();
    }
  }, policy(async () => response)).then(value => { completed = true; return value; });
  await firstEvent;
  assert.equal(completed, false, "events must arrive before response completion");
  finish();
  const result = await pending;
  assert.equal(result.statusCode, 200);
  assert.deepEqual(events.map(e => e.index), [1, 2]);
});

test("stream backpressure is awaited before reading following frames", async () => {
  let release;
  const gate = new Promise(resolve => { release = resolve; });
  const calls = [];
  const framer = new StreamFramer("application/x-ndjson", async value => {
    calls.push(value);
    if (calls.length === 1) await gate;
  });
  let done = false;
  const pending = framer.write('{"n":1}\n{"n":2}\n').then(() => { done = true; });
  await Promise.resolve();
  assert.deepEqual(calls, ['{"n":1}']);
  assert.equal(done, false);
  release();
  await pending;
  assert.deepEqual(calls, ['{"n":1}', '{"n":2}']);
});

test("headers-only GET cancels the unread body instead of buffering it", async () => {
  let cancelled = false;
  const response = {
    status: 200,
    headers: { get: name => name === "content-type" ? "application/json" : null },
    body: {
      async cancel() { cancelled = true; },
      getReader() { throw new Error("Large conversation body must not be read."); }
    },
    text() { throw new Error("Large conversation body must not be buffered."); }
  };
  const result = await executeBrowserHttp({
    args: { method: "GET", path: "/backend-api/conversation/large",
      headersOnly: true }
  }, policy(async () => response));
  assert.equal(result.statusCode, 200);
  assert.equal(result.body, "");
  assert.equal(cancelled, true);
});

test("streaming rejects a response without an incremental reader", async () => {
  const response = {
    status: 200,
    headers: { get: () => "text/event-stream" },
    body: null,
    text() { throw new Error("Unbounded body buffering is forbidden."); }
  };
  await assert.rejects(executeBrowserHttp({
    args: { method: "POST", path: "/backend-api/f/conversation", body: "{}",
      accept: "text/event-stream", stream: true },
    reportEvent: () => {}
  }, policy(async () => response)), /incremental response body/);
});

test("malformed UTF-8 and oversized streamed frames fail explicitly", async () => {
  const framer = new StreamFramer("text/event-stream", async () => {});
  await assert.rejects(framer.write(new Uint8Array([0xc3, 0x28])), /encoded|valid|UTF/i);
  const framer2 = new StreamFramer("application/x-ndjson", async () => {});
  await assert.rejects(framer2.write("x".repeat(256 * 1024 + 1)), /256 KiB/);
});

test("generic transport rejects unsupported origins, headers and native hooks before fetch", async () => {
  let called = 0;
  const p = policy(async () => { called++; return new Response("{}"); });
  for (const args of [
    { method: "DELETE", path: "/backend-api/test" },
    { method: "GET", path: "/backend-api/../api/auth/session" },
    { method: "GET", path: "https://evil.example/api" },
    { method: "POST", path: "/backend-api/test", body: {}, accept: "application/json" },
    { method: "GET", path: "/backend-api/test", nativeHook: "unknown" },
    { method: "GET", path: "/backend-api/test", stream: true, accept: "application/json" },
    { method: "POST", path: "/backend-api/test", headersOnly: true }
  ]) await assert.rejects(executeBrowserHttp({ args }, p), /not allowed/);
  assert.equal(called, 0);
});

test("failed streamed framing cancels the unread upstream response", async () => {
  let cancelled = false;
  const body = new ReadableStream({
    start(controller) {
      controller.enqueue(new TextEncoder().encode("data: " + "x".repeat(256 * 1024 + 1) + "\n\n"));
    },
    cancel() { cancelled = true; }
  });
  await assert.rejects(executeBrowserHttp({
    args: { method: "POST", path: "/backend-api/f/conversation",
      body: "{}", accept: "text/event-stream", stream: true },
    reportEvent: () => {}
  }, policy(async () => new Response(body, { status: 200,
    headers: { "content-type": "text/event-stream" } }))), /256 KiB/);
  assert.equal(cancelled, true, "failed framing must cancel its source");
});

test("oversized JSON response cancels the unread upstream response", async () => {
  let cancelled = false;
  const body = new ReadableStream({
    start(controller) {
      controller.enqueue(new Uint8Array(16 * 1024 * 1024 + 1));
    },
    cancel() { cancelled = true; }
  });
  await assert.rejects(executeBrowserHttp({
    args: { method: "GET", path: "/backend-api/models" }
  }, policy(async () => new Response(body, { status: 200,
    headers: { "content-type": "application/json" } }))), /16 MiB/);
  assert.equal(cancelled, true, "oversized response must cancel its source");
});
