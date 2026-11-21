const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const main = fs.readFileSync(path.join(__dirname, "../electron/main.js"), "utf8");
const from = main.indexOf("const MAX_OPERATION_EVENTS =");
const to = main.indexOf("\nconst parentProcessId", from);
assert.ok(from >= 0 && to > from, "Event queue must live in Electron's existing operation owner.");
const queue = vm.runInNewContext(main.slice(from, to) +
  "\n({ queueOperationEvent, acknowledgeOperationEvents, MAX_OPERATION_EVENTS })",
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
  await assert.rejects(pending, /cancelled/);
});

test("single oversized event fails before enqueueing; byte accounting remains bounded", async () => {
  const s = state();
  await assert.rejects(queue.queueOperationEvent(s, "x".repeat(256 * 1024 + 1)),
    /256 KiB/);
  assert.equal(s.events.length, 0);
  assert.equal(s.eventBytes, 0);
});
