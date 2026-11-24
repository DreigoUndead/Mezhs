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

test("large provider SSE event remains ordered under a 2 MiB per-frame bound", async () => {
  const events = [];
  const framer = new StreamFramer("text/event-stream", value => events.push(value));
  const large = JSON.stringify({ output: "x".repeat(1024 * 1024) });
  await framer.write('data: {"type":"message_marker"}\n\n');
  await framer.write("data: " + large.slice(0, 512 * 1024));
  await framer.write(large.slice(512 * 1024) + "\n\n");
  assert.equal(events.length, 2);
  assert.equal(events[0], '{"type":"message_marker"}');
  assert.equal(events[1], large);
});

test("late oversized provider frame fails explicitly and cancels unread body", async () => {
  let cancelled = false;
  const marker = '{"type":"message_marker"}';
  const body = new ReadableStream({
    start(controller) {
      controller.enqueue(new TextEncoder().encode("data: " + marker + "\n\n"));
      controller.enqueue(new TextEncoder().encode("data: " + "x".repeat(2 * 1024 * 1024)));
    },
    cancel() { cancelled = true; }
  });
  const delivered = [];
  await assert.rejects(executeBrowserHttp({
    args: { method: "POST", path: "/backend-api/conversation", body: "{}",
      accept: "text/event-stream", stream: true },
    reportEvent: value => delivered.push(value)
  }, policy(async () => new Response(body, { status: 200,
    headers: { "content-type": "text/event-stream" } }))), error => {
    assert.equal(error.code, "stream_delivery_failed");
    assert.equal(error.reason, "frame_oversized");
    return true;
  });
  assert.deepEqual(delivered, [marker]);
  assert.equal(cancelled, true);
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
  await assert.rejects(framer.write(new Uint8Array([0xc3, 0x28])), error => {
    assert.match(error.message, /encoded|valid|UTF/i);
    assert.equal(error.code, "stream_delivery_failed");
    assert.equal(error.reason, "invalid_utf8");
    return true;
  });
  const framer2 = new StreamFramer("application/x-ndjson", async () => {});
  await assert.rejects(framer2.write("x".repeat(2 * 1024 * 1024 + 1)), error => {
    assert.match(error.message, /2 MiB/);
    assert.equal(error.reason, "frame_oversized");
    return true;
  });
});

test("late stream framing failure is categorized after previously delivered events", async () => {
  let cancelled = false;
  const first = '{"type":"message_marker","marker":"final_channel_token"}';
  const source = new ReadableStream({
    start(controller) {
      controller.enqueue(new TextEncoder().encode("data: " + first + "\n\n"));
      controller.enqueue(new Uint8Array([0xc3, 0x28]));
    },
    cancel() { cancelled = true; }
  });
  const events = [];
  await assert.rejects(executeBrowserHttp({
    args: { method: "POST", path: "/backend-api/f/conversation",
      body: "{}", accept: "text/event-stream", stream: true },
    reportEvent: value => events.push(value)
  }, policy(async () => new Response(source, { status: 200,
    headers: { "content-type": "text/event-stream" } }))), error => {
    assert.equal(error.code, "stream_delivery_failed");
    assert.equal(error.reason, "invalid_utf8");
    return true;
  });
  assert.deepEqual(events, [first]);
  assert.equal(cancelled, true, "failed stream cancels the unread source");
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
      controller.enqueue(new TextEncoder().encode("data: " + "x".repeat(2 * 1024 * 1024 + 1) + "\n\n"));
    },
    cancel() { cancelled = true; }
  });
  await assert.rejects(executeBrowserHttp({
    args: { method: "POST", path: "/backend-api/f/conversation",
      body: "{}", accept: "text/event-stream", stream: true },
    reportEvent: () => {}
  }, policy(async () => new Response(body, { status: 200,
    headers: { "content-type": "text/event-stream" } }))), /2 MiB/);
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

// review: unread response cleanup; do not buffer a nonstandard body without a reader
test('JSON HTTP rejects non-incremental nonempty bodies rather than unbounded text()', async () => {
  let textCalls = 0;
  const policy = { origin: 'https://chatgpt.com', pathPrefix: '/backend-api/',
    fetch: async () => ({ status: 200, headers: new Headers(), body: {},
      text() { textCalls++; return Promise.resolve('unbounded'); } }) };
  await assert.rejects(executeBrowserHttp({
    args: { method: 'GET', path: '/backend-api/models', accept: 'application/json' }
  }, policy), /requires an incremental body/);
  assert.equal(textCalls, 0);
});

test('SSE empty data lines count toward event size', async () => {
  const events = [];
  const f = new StreamFramer('text/event-stream', async x => events.push(x));
  // Keep the test linear: fill a near-limit event with one large line,
  // then prove that empty data-line separators also count toward the limit.
  await f.write('data: ' + 'x'.repeat(2 * 1024 * 1024 - 8) + '\n');
  await assert.rejects(f.write('data:\n'.repeat(9)), error => {
    assert.match(error.message, /2 MiB/);
    assert.equal(error.reason, "sse_event_oversized");
    return true;
  });
  assert.equal(events.length, 0);
});


test("non-success HTTP response cannot emit protocol events and releases the body", async () => {
  let cancelled = false;
  const events = [];
  const response = new Response(new ReadableStream({
    start(controller) {
      controller.enqueue(new TextEncoder().encode('data: {"type":"message_marker"}\n\n'));
    },
    cancel() { cancelled = true; }
  }), { status: 429, headers: { "content-type": "text/event-stream" } });
  const result = await executeBrowserHttp({
    args: { method: "POST", path: "/backend-api/f/conversation",
      body: "{}", accept: "text/event-stream", stream: true },
    reportEvent: value => events.push(value)
  }, policy(async () => response));
  assert.equal(result.statusCode, 429);
  assert.deepEqual(events, []);
  assert.equal(cancelled, true);
});
