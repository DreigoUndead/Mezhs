const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

// Exercise the actual Electron context/operation ownership functions without launching Electron.
const source = fs.readFileSync(path.join(__dirname, "../electron/main.js"), "utf8");
function section(start, end) {
  const from = source.indexOf(start);
  const to = source.indexOf(end, from);
  assert.ok(from >= 0 && to > from, `Could not extract Electron section ${start}`);
  return source.slice(from, to);
}
const contextSource = section("function closeContext(", "\nasync function initialize(");
const operationSource = section("function queueProviderOperation(", "\nasync function start(");

function harness({ guest = false, deferWindowReady = false, stopGeneration } = {}) {
  const contexts = new Map();
  const providerOperations = new Map();
  const sessions = [];
  const windows = [];
  const operations = [];
  const authenticatedSession = guest ? null : { account: true };
  const management = guest ? null : { window: { isDestroyed: () => false }, session: authenticatedSession };
  let sequence = 0;
  const env = {
    contexts, providerOperations, authenticatedSession, management, guest,
    AbortController,
    browserModule: { operations: { stopGeneration: stopGeneration || (async ({ abort }) => {
      abort();
      return { outcome: "attempted" };
    }) } },
    invokePageOperation: async () => ({ outcome: "attempted" }),
    randomUUID: () => `operation-${++sequence}`,
    session: {
      fromPartition(partition) {
        const session = { partition };
        sessions.push(session);
        return session;
      }
    },
    configureSessionBrowserIdentity(session) { session.identityConfigured = true; },
    async createWindow(session, _visible, _management, onCreated) {
      const window = {
        session, destroyed: false,
        isDestroyed() { return this.destroyed; },
        destroy() { this.destroyed = true; }
      };
      windows.push(window);
      onCreated?.(window); // Mirrors the Electron callback before navigation.
      if (deferWindowReady)
        await new Promise(resolve => { window.finishLoading = resolve; });
      return window;
    },
    invokeProvider(context, request, reportProgress, signal) {
      return new Promise((resolve, reject) => {
        operations.push({ context, request, reportProgress, signal, resolve, reject });
      });
    },
    setTimeout,
  };
  const runtime = vm.runInNewContext(
    contextSource + "\n" + operationSource +
    "\n({ contextPromise, releaseContext, cancelProviderOperation, queueProviderOperation, stopProviderOperation })",
    env
  );
  return { ...runtime, contexts, providerOperations, windows, sessions, operations, authenticatedSession };
}

const flush = () => new Promise(resolve => setImmediate(resolve));

test("authenticated ChatIds own distinct windows; explicit release retires only one", async () => {
  const h = harness();
  const a = await h.contextPromise("chat-a", false);
  const b = await h.contextPromise("chat-b", false);
  assert.notEqual(a.window, b.window);
  assert.equal(a.session, h.authenticatedSession);
  assert.equal(b.session, h.authenticatedSession);
  assert.equal(await h.contextPromise("chat-a", false), a);

  h.releaseContext("chat-a");
  await flush();
  assert.equal(a.window.destroyed, true);
  assert.equal(b.window.destroyed, false);
  assert.equal(h.contexts.has("chat-a"), false);
  assert.notEqual(await h.contextPromise("chat-a", false), a);
});

test("guest turns receive fresh nonpersistent sessions even for one ChatId", async () => {
  const h = harness({ guest: true });
  const a = await h.contextPromise("guest-chat", true);
  const b = await h.contextPromise("guest-chat", true);
  assert.notEqual(a.window, b.window);
  assert.notEqual(a.session, b.session);
  assert.equal(a.session.identityConfigured, true);
  assert.equal(b.session.identityConfigured, true);
  assert.ok(a.session.partition.startsWith("mezhs-guest-"));
  assert.ok(b.session.partition.startsWith("mezhs-guest-"));
  assert.equal(h.contexts.size, 0);
  assert.throws(() => h.contextPromise("guest-chat", false), /ephemeral browser context/);
});

test("cancelled chat context cannot destroy a different active ChatId", async () => {
  const h = harness();
  const idA = h.queueProviderOperation({ contextId: "a", operation: "send" });
  const idB = h.queueProviderOperation({ contextId: "b", operation: "send" });
  await flush();
  assert.equal(h.operations.length, 2);
  const a = h.operations.find(op => op.request.contextId === "a");
  const b = h.operations.find(op => op.request.contextId === "b");
  assert.ok(a && b);
  assert.equal(h.cancelProviderOperation(idA), true);
  assert.equal(a.signal.aborted, true);
  assert.equal(b.signal.aborted, false);
  await flush();
  assert.equal(a.context.window.destroyed, true);
  assert.equal(b.context.window.destroyed, false);
  assert.equal(h.providerOperations.has(idA), false);
  assert.equal(h.providerOperations.has(idB), true);
  a.reportProgress({ state: "stale" });
  b.reportProgress({ state: "running" });
  assert.equal(h.providerOperations.get(idB).progress.state, "running");
  b.resolve({ answer: "B" });
  await flush();
  assert.equal(h.providerOperations.get(idB).status, "completed");
  assert.equal(b.context.window.destroyed, false);
});

test("releasing a busy context lets its active call finish, then closes the window", async () => {
  const h = harness();
  const id = h.queueProviderOperation({ contextId: "busy", operation: "send" });
  await flush();
  assert.equal(h.operations.length, 1);
  const op = h.operations[0];
  h.releaseContext("busy");
  await flush();
  assert.equal(op.context.window.destroyed, false);
  op.resolve({ answer: "done" });
  await flush();
  assert.equal(op.context.window.destroyed, true);
  assert.equal(h.providerOperations.get(id).status, "completed");
});

test("cancel destroys an account window immediately even when navigation never completes", async () => {
  const h = harness({ deferWindowReady: true });
  const cancelled = h.queueProviderOperation({ contextId: "loading-a", operation: "send" });
  const peer = h.queueProviderOperation({ contextId: "loading-b", operation: "send" });
  assert.equal(h.windows.length, 2);
  assert.equal(h.operations.length, 0); // Both are still navigating.

  assert.equal(h.cancelProviderOperation(cancelled), true);
  assert.equal(h.windows[0].destroyed, true);
  assert.equal(h.windows[1].destroyed, false);
  assert.equal(h.contexts.has("loading-a"), false);

  h.windows[0].finishLoading();
  h.windows[1].finishLoading();
  await flush();
  assert.equal(h.operations.length, 1); // Never dispatch the cancelled request.
  assert.equal(h.operations[0].request.contextId, "loading-b");
  h.operations[0].resolve({ answer: "peer" });
  await flush();
  assert.equal(h.providerOperations.get(peer).status, "completed");
  assert.equal(h.windows[1].destroyed, false);
});

test("guest cancellation and ordinary release also close still-loading windows", async () => {
  const guest = harness({ guest: true, deferWindowReady: true });
  const id = guest.queueProviderOperation({ contextId: "guest-loading", ephemeral: true, operation: "send" });
  assert.equal(guest.cancelProviderOperation(id), true);
  assert.equal(guest.windows[0].destroyed, true);

  const account = harness({ deferWindowReady: true });
  const pending = account.contextPromise("ordinary-loading", false);
  account.releaseContext("ordinary-loading");
  assert.equal(account.windows[0].destroyed, true);
  account.windows[0].finishLoading();
  await assert.rejects(pending, /closed while loading/);
});

test("closing the login window ends the current attempt instead of reopening it", async () => {
  const authSource = section("async function checkAuthorization(", "\nfunction invokeProvider(");
  let authorized = false, shown = 0;
  const auth = vm.runInNewContext(
    authSource + "\n({ authorizeRequest, dismiss: () => { managementAuthorizationDismissed = true; } })",
    {
      managementAuthorizationDismissed: false,
      management: { window: { show() { shown++; }, focus() {} } },
      browserModule: { name: "Fake", isAuthorized: async () => authorized },
      authenticatedSession: {
        flushStorageData: async () => {},
        cookies: { flushStore: async () => {} }
      }
    }
  );
  const started = await auth.authorizeRequest(true, true);
  assert.equal(started.authorized, false);
  assert.equal(started.dismissed, false);
  assert.equal(shown, 1);

  auth.dismiss();
  const stopped = await auth.authorizeRequest(true, false);
  assert.equal(stopped.dismissed, true);
  assert.equal(shown, 1);

  // A later explicit attempt resets the dismissed flag.
  const retried = await auth.authorizeRequest(true, true);
  assert.equal(retried.dismissed, false);
  assert.equal(shown, 2);
  authorized = true;
  assert.equal((await auth.authorizeRequest(true, false)).authorized, true);
});

test("stop preserves the active chat context until its scoped attempt completes", async () => {
  let finishStop;
  let reachedStop = false;
  const h = harness({ stopGeneration: ({ abort, window }) => {
    assert.equal(window.destroyed, false, "stop must precede context retirement");
    reachedStop = true;
    return new Promise(resolve => { finishStop = () => { abort(); resolve({ outcome: "attempted" }); }; });
  } });
  const aId = h.queueProviderOperation({
    contextId: "chat-a", operation: "send", arguments: { messageId: "user-a" }
  });
  const bId = h.queueProviderOperation({
    contextId: "chat-b", operation: "send", arguments: { messageId: "user-b" }
  });
  await flush();
  const a = h.operations.find(op => op.request.contextId === "chat-a");
  const b = h.operations.find(op => op.request.contextId === "chat-b");
  a.reportProgress({ state: "waiting" });
  const stopping = h.stopProviderOperation(h.providerOperations.get(aId));
  await flush();
  assert.equal(reachedStop, true);
  assert.equal(a.context.window.destroyed, false);
  assert.equal(b.context.window.destroyed, false);
  assert.equal(b.signal.aborted, false);
  finishStop();
  assert.equal((await stopping).outcome, "attempted");
  assert.equal(a.signal.aborted, true);
  assert.equal(b.signal.aborted, false);
  assert.equal(a.context.window.destroyed, false, "remote attempt alone must not retire context");
  assert.equal(h.cancelProviderOperation(aId), true);
  assert.equal(a.context.window.destroyed, true);
  assert.equal(b.context.window.destroyed, false);
  b.resolve({ answer: "peer" });
  await flush();
  assert.equal(h.providerOperations.get(bId).status, "completed");
  assert.equal(h.operations.length, 2, "stop must not replay a submitted turn");
});

test("stop before dispatch, after completion and repeated stop are bounded and do not send", async () => {
  let calls = 0;
  const h = harness({ deferWindowReady: true, stopGeneration: async () => {
    calls++; return { outcome: "attempted" };
  } });
  const id = h.queueProviderOperation({ contextId: "loading", operation: "send",
    arguments: { messageId: "outgoing" } });
  assert.equal((await h.stopProviderOperation(h.providerOperations.get(id))).outcome, "notActive");
  assert.equal(calls, 0);
  assert.equal(h.cancelProviderOperation(id), true);
  h.windows[0].finishLoading();
  await flush();
  assert.equal(h.operations.length, 0);
  assert.equal((await h.stopProviderOperation(h.providerOperations.get(id))).outcome, "notActive");

  const live = harness({ stopGeneration: async () => {
    calls++; return { outcome: "attempted" };
  } });
  const completed = live.queueProviderOperation({ contextId: "done", operation: "send",
    arguments: { messageId: "done-message" } });
  await flush();
  live.operations[0].resolve({ answer: "done" });
  await flush();
  assert.equal((await live.stopProviderOperation(live.providerOperations.get(completed))).outcome, "notActive");
  assert.equal(calls, 0);
});

test("provider stop rejection and timeout never block local cancellation", async () => {
  const failed = harness({ stopGeneration: () => { throw Error("provider rejected stop"); } });
  const id = failed.queueProviderOperation({ contextId: "failed", operation: "send",
    arguments: { messageId: "msg-failed" } });
  await flush();
  assert.equal((await failed.stopProviderOperation(failed.providerOperations.get(id))).outcome, "unsupported");
  assert.equal(failed.cancelProviderOperation(id), true);
  assert.equal(failed.windows[0].destroyed, true);

  const hung = harness({ stopGeneration: () => new Promise(() => {}) });
  const hungId = hung.queueProviderOperation({ contextId: "hung", operation: "send",
    arguments: { messageId: "msg-hung" } });
  await flush();
  const began = Date.now();
  assert.equal((await hung.stopProviderOperation(hung.providerOperations.get(hungId))).outcome, "unsupported");
  assert.ok(Date.now() - began < 2300, "stop must time out promptly");
  assert.equal(hung.cancelProviderOperation(hungId), true);
  assert.equal(hung.windows[0].destroyed, true);
});

test("guest stop is scoped to its ephemeral window and never touches a peer", async () => {
  const h = harness({ guest: true });
  const a = h.queueProviderOperation({ contextId: "guest-a", ephemeral: true,
    operation: "sendPrompt", arguments: { messageId: "guest-msg-a" } });
  const b = h.queueProviderOperation({ contextId: "guest-b", ephemeral: true,
    operation: "sendPrompt", arguments: { messageId: "guest-msg-b" } });
  await flush();
  const peer = h.operations.find(op => op.request.contextId === "guest-b");
  assert.equal((await h.stopProviderOperation(h.providerOperations.get(a))).outcome, "attempted");
  assert.equal(peer.signal.aborted, false);
  h.cancelProviderOperation(a);
  assert.equal(h.windows[0].destroyed, true);
  assert.equal(h.windows[1].destroyed, false);
  peer.resolve({ answer: "peer" });
  await flush();
  assert.equal(h.providerOperations.get(b).status, "completed");
});
