const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { EventEmitter } = require('node:events');

// Run the actual main-process implementation without launching an Electron instance.
const main = fs.readFileSync(path.join(__dirname, '../electron/main.js'), 'utf8');
const start = main.indexOf('function invokePageOperation(');
const end = main.indexOf('\nasync function initialize(', start);
assert.ok(start >= 0 && end > start);
const implementation = main.slice(start, end);

function harness() {
  const ipcMain = new EventEmitter();
  const contents = new EventEmitter();
  const sent = [];
  const activeTimers = new Set();
  contents.send = (channel, request) => sent.push({ channel, request });
  contents.isDestroyed = () => false;
  const invoke = vm.runInNewContext(implementation + '\ninvokePageOperation', {
    ipcMain,
    randomUUID: () => `test-${sent.length}`,
    setTimeout: (callback, ms) => {
      const token = setTimeout(() => { activeTimers.delete(token); callback(); }, ms);
      activeTimers.add(token);
      return token;
    },
    clearTimeout: timer => { clearTimeout(timer); activeTimers.delete(timer); }
  });
  const targetWindow = { webContents: contents };
  const verifyClean = () => {
    assert.equal(ipcMain.listenerCount(sent[0]?.request.responseChannel ?? 'mezhs:page-operation-result:test-0'), 0);
    for (const event of ['did-start-navigation', 'render-process-gone', 'destroyed'])
      assert.equal(contents.listenerCount(event), 0, `${event} listener leaked`);
    assert.equal(activeTimers.size, 0);
  };
  return { invoke, ipcMain, contents, sent, targetWindow, verifyClean, activeTimers };
}

test('named page result resolves once and cleans IPC listeners and deadline', async () => {
  const h = harness();
  const result = h.invoke(h.targetWindow, 'sendPrompt', { prompt: 'safe' }, 50);
  assert.equal(h.sent.length, 1);
  assert.equal(h.sent[0].channel, 'mezhs:page-operation');
  assert.equal(h.sent[0].request.operation, 'sendPrompt');
  h.ipcMain.emit(h.sent[0].request.responseChannel, {}, { ok: true, result: { ok: true, text: 'OK' } });
  assert.equal((await result).text, 'OK');
  h.verifyClean();
});

test('main-frame navigation aborts a pending request without replay', async () => {
  const h = harness();
  const result = h.invoke(h.targetWindow, 'sendPrompt', {}, 50);
  h.contents.emit('did-start-navigation', {}, { isMainFrame: true, isInPlace: false });
  await assert.rejects(result, /navigated.*outcome is uncertain/i);
  assert.equal(h.sent.length, 1);
  h.verifyClean();
});

test('in-place navigation preserves an active request', async () => {
  const h = harness();
  const result = h.invoke(h.targetWindow, 'sendPrompt', {}, 50);
  h.contents.emit('did-start-navigation', {}, { isMainFrame: true, isInPlace: true });
  h.ipcMain.emit(h.sent[0].request.responseChannel, {}, { ok: true, result: 'done' });
  assert.equal(await result, 'done');
  h.verifyClean();
});

for (const reason of ['render-process-gone', 'destroyed']) {
  test(`${reason} rejects pending request and releases listeners`, async () => {
    const h = harness();
    const result = h.invoke(h.targetWindow, 'sendPrompt', {}, 50);
    h.contents.emit(reason);
    await assert.rejects(result, /renderer disappeared.*outcome is uncertain/i);
    assert.equal(h.sent.length, 1);
    h.verifyClean();
  });
}

test('guest IPC timeout settles a renderer that never replies', async () => {
  const h = harness();
  const result = h.invoke(h.targetWindow, 'sendPrompt', {}, 5);
  await assert.rejects(result, /timed out.*outcome is uncertain/i);
  assert.equal(h.sent.length, 1);
  h.verifyClean();
});

test('unbounded default does not impose a deadline on other providers', async () => {
  const h = harness();
  const result = h.invoke(h.targetWindow, 'sendPrompt', {});
  assert.equal(h.activeTimers.size, 0);
  h.ipcMain.emit(h.sent[0].request.responseChannel, {}, { ok: true, result: 42 });
  assert.equal(await result, 42);
  h.verifyClean();
});

test('send failure rejects and cleans listeners', async () => {
  const h = harness();
  h.contents.send = () => { throw new Error('renderer unavailable'); };
  await assert.rejects(h.invoke(h.targetWindow, 'sendPrompt', {}, 50), /renderer unavailable/);
  h.verifyClean();
});
