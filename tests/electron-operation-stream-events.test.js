const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { StreamFramer } = require("../electron/browser-http");

const main = fs.readFileSync(path.join(__dirname, "../electron/main.js"), "utf8");
const from = main.indexOf("const MAX_OPERATION_EVENTS =");
const to = main.indexOf("\nconst parentProcessId", from);
assert.ok(from >= 0 && to > from, "Event queue must live in Electron's existing operation owner.");
const queue = vm.runInNewContext(main.slice(from, to) +
  "\n({ queueOperationEvent, acknowledgeOperationEvents, MAX_OPERATION_EVENTS, streamFailureProgress })",
  { Buffer, Promise });

function state() {
  return { events: [], eventBytes: 0, eventSequence: 0, eventWaiters: [],
    cancelled: false, abortController: new AbortController() };
}
const tick = () => new Promise(resolve => setImmediate(resolve));

test("event acknowledgement preserves order across duplicate and lost status polls", async () => {
  const s = state();
  await queue.queueOperationEvent(s, "first");
  await queue.queueOperationEvent(s, "second");
  let batch = queue.acknowledgeOperationEvents(s, 0);
  assert.deepEqual([...batch].map(e => [e.sequence, e.value]),
    [[1, "first"], [2, "second"]]);
  // A poll lost by the network must be safe to repeat without advancing the cursor.
  batch = queue.acknowledgeOperationEvents(s, 0);
  assert.deepEqual([...batch].map(e => e.sequence), [1, 2]);
  batch = queue.acknowledgeOperationEvents(s, 1);
  assert.deepEqual([...batch].map(e => e.sequence), [2]);
  batch = queue.acknowledgeOperationEvents(s, 2);
  assert.equal(batch.length, 0);
  await queue.queueOperationEvent(s, "third");
  assert.deepEqual([...queue.acknowledgeOperationEvents(s, 2)].map(e => e.sequence), [3]);
  assert.throws(() => queue.acknowledgeOperationEvents(s, 5), /acknowledgement/);
});

test("full bounded event queue blocks producer and wakes only on acknowledgement", async () => {
  const s = state();
  for (let i = 0; i < queue.MAX_OPERATION_EVENTS; i++)
    await queue.queueOperationEvent(s, String(i));
  let released = false;
  const pending = queue.queueOperationEvent(s, "tail").then(() => { released = true; });
  await tick();
  assert.equal(released, false);
  assert.equal(s.events.length, queue.MAX_OPERATION_EVENTS);
  const first = queue.acknowledgeOperationEvents(s, 1);
  assert.equal(first[0].sequence, 2);
  await pending;
  assert.equal(released, true);
  assert.equal(s.events.at(-1).sequence, queue.MAX_OPERATION_EVENTS + 1);
});

test("cancelled event producers fail instead of silently dropping events", async () => {
  const s = state();
  for (let i = 0; i < queue.MAX_OPERATION_EVENTS; i++)
    await queue.queueOperationEvent(s, "full");
  const pending = queue.queueOperationEvent(s, "blocked");
  await tick();
  s.cancelled = true;
  s.abortController.abort();
  for (const wake of s.eventWaiters.splice(0)) wake();
  await assert.rejects(pending, error => {
    assert.equal(error.code, "stream_delivery_failed");
    assert.equal(error.reason, "queue_cancelled");
    return true;
  });
});

test("single oversized event fails before enqueueing; byte accounting remains bounded", async () => {
  const s = state();
  await assert.rejects(queue.queueOperationEvent(s, "x".repeat(2 * 1024 * 1024 + 1)),
    error => {
      assert.match(error.message, /2 MiB/);
      assert.equal(error.code, "stream_delivery_failed");
      assert.equal(error.reason, "queue_event_oversized");
      return true;
    });
  assert.equal(s.events.length, 0);
  assert.equal(s.eventBytes, 0);
});

test("framer-legal SSE event survives JSON escaping at the bounded queue", async () => {
  const s = state();
  const raw = JSON.stringify({ message: "\\\\".repeat(100000) });
  assert.ok(Buffer.byteLength(raw, "utf8") < 256 * 1024);
  assert.ok(Buffer.byteLength(JSON.stringify(raw), "utf8") > 256 * 1024);
  const framer = new StreamFramer("text/event-stream",
    value => queue.queueOperationEvent(s, value));
  await framer.write("data: " + raw + "\n\n");
  assert.equal(s.events.length, 1);
  assert.equal(s.events[0].value, raw);
  assert.equal(s.events[0].sequence, 1);
  assert.ok(s.eventBytes < 4 * 1024 * 1024);
});

test("worst-case control escaping still fits serialized queue limit", async () => {
  const s = state();
  const raw = "\u0001".repeat(250000);
  assert.ok(Buffer.byteLength(raw, "utf8") <= 256 * 1024);
  const framer = new StreamFramer("text/event-stream",
    value => queue.queueOperationEvent(s, value));
  await framer.write("data: " + raw + "\n\n");
  assert.equal(s.events.length, 1);
  assert.equal(s.events[0].value, raw);
  assert.ok(s.eventBytes > 256 * 1024);
  assert.ok(s.eventBytes <= 2 * 1024 * 1024);
});

test("stream failure progress exposes only whitelisted categories", () => {
  const known = queue.streamFailureProgress({
    code: "stream_delivery_failed", reason: "sse_event_oversized",
    message: "private response fragment"
  });
  assert.equal(known.state, "retrieving");
  assert.equal(known.detail, "Browser HTTP stream failed (sse_event_oversized).");
  assert.equal(known.analysis, null);
  const unknown = queue.streamFailureProgress({
    code: "stream_delivery_failed", reason: "private response fragment",
    message: "private response fragment"
  });
  assert.equal(unknown.detail, "Browser HTTP stream failed (unknown).");
  assert.doesNotMatch(JSON.stringify(unknown), /private response fragment/);
  assert.equal(queue.streamFailureProgress({ code: "request_not_submitted" }), null);
});

test("terminal results survive a status response that is sent but not acknowledged", () => {
  // Successful socket flush is not proof that the remote poller received the result.
  assert.doesNotMatch(main, /response\.once\("finish",[^\n]*providerOperations\.delete/);
  assert.match(main, /setTimeout\(\(\) => providerOperations\.delete\(operationId\), 10 \* 60 \* 1000\)/);
});
