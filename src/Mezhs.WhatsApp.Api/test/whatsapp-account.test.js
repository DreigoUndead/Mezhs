import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { mkdtemp, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { WhatsAppAccount, WhatsAppAccountNotConnectedError } from '../src/whatsapp-account.js';
import { MessageStore } from '../src/message-store.js';

function fakeSocket() {
  const ev = new EventEmitter();
  return {
    ev,
    user: null,
    ended: [],
    logoutCalls: 0,
    end(error) { this.ended.push(error); },
    async logout() { this.logoutCalls += 1; },
    async sendMessage(chatId, { text }) {
      return {
        key: { id: 'sent-1', remoteJid: chatId, fromMe: true },
        messageTimestamp: 100,
        message: { conversation: text },
      };
    },
  };
}

function fakeBaileys(overrides = {}) {
  const sockets = [];
  return {
    api: {
      DisconnectReason: { loggedOut: 401 },
      async useMultiFileAuthState() { return { state: {}, saveCreds: async () => {} }; },
      async fetchLatestWaWebVersion() { return { version: [2, 3000, 1] }; },
      makeWASocket() {
        const socket = fakeSocket();
        sockets.push(socket);
        return socket;
      },
      ...overrides,
    },
    sockets,
  };
}

async function withAccount(run, options = {}) {
  const authDir = await mkdtemp(path.join(os.tmpdir(), 'mezhs-wa-test-'));
  try {
    const transport = fakeBaileys(options.baileys);
    const store = new MessageStore();
    const account = new WhatsAppAccount({
      authDir,
      store,
      baileys: transport.api,
      reconnectDelayMs: options.reconnectDelayMs ?? 10,
    });
    await run({ account, store, transport, authDir });
  } finally {
    await rm(authDir, { recursive: true, force: true });
  }
}

const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

test('failed connect returns account to disconnected and can be retried', async () => {
  let attempts = 0;
  await withAccount(async ({ account, transport }) => {
    await assert.rejects(account.connect(), /version unavailable/);
    assert.equal(account.status().state, 'disconnected');

    await account.connect();
    assert.equal(account.status().state, 'connecting');
    assert.equal(transport.sockets.length, 1);
  }, {
    baileys: {
      async fetchLatestWaWebVersion() {
        attempts += 1;
        if (attempts === 1) throw new Error('version unavailable');
        return { version: [2, 3000, 1] };
      },
    },
  });
});

test('manual disconnect cancels a scheduled reconnect', async () => {
  await withAccount(async ({ account, transport }) => {
    await account.connect();
    assert.equal(transport.sockets.length, 1);

    transport.sockets[0].ev.emit('connection.update', {
      connection: 'close',
      lastDisconnect: { error: { output: { statusCode: 500 } } },
    });
    await account.disconnect();
    await delay(30);

    assert.equal(transport.sockets.length, 1);
    assert.equal(account.status().state, 'disconnected');
  });
});

test('disconnect invalidates an in-flight connect before a socket is created', async () => {
  const authState = deferred();
  await withAccount(async ({ account, transport }) => {
    const connecting = account.connect();
    await delay(0);

    await account.disconnect();
    authState.resolve({ state: {}, saveCreds: async () => {} });
    await connecting;

    assert.equal(transport.sockets.length, 0);
    assert.equal(account.status().state, 'disconnected');
  }, {
    baileys: {
      useMultiFileAuthState: () => authState.promise,
    },
  });
});

test('old socket events cannot change state after a newer connection starts', async () => {
  await withAccount(async ({ account, transport }) => {
    await account.connect();
    const first = transport.sockets[0];
    await account.disconnect();
    await account.connect();
    const second = transport.sockets[1];

    first.user = { id: 'old' };
    first.ev.emit('connection.update', { connection: 'open' });
    assert.notEqual(account.status().accountId, 'old');

    second.user = { id: 'new' };
    second.ev.emit('connection.update', { connection: 'open' });
    assert.equal(account.status().accountId, 'new');
  });
});

test('message update/delete events are forwarded to the store', async () => {
  await withAccount(async ({ account, store, transport }) => {
    await account.connect();
    const socket = transport.sockets[0];

    socket.ev.emit('messages.upsert', { messages: [{
      key: { id: 'a', remoteJid: 'chat@s.whatsapp.net', fromMe: false },
      messageTimestamp: 10,
      message: { conversation: 'old' },
    }] });
    socket.ev.emit('messages.update', [{
      key: { id: 'a', remoteJid: 'chat@s.whatsapp.net' },
      update: { message: { conversation: 'new' } },
    }]);
    assert.equal(store.get('a', 'chat@s.whatsapp.net').text, 'new');

    socket.ev.emit('messages.delete', { keys: [{ id: 'a', remoteJid: 'chat@s.whatsapp.net' }] });
    assert.equal(store.get('a', 'chat@s.whatsapp.net'), null);
  });
});

test('sendText requires a connected account and stores the sent message', async () => {
  await withAccount(async ({ account, transport }) => {
    await assert.rejects(account.sendText('chat@s.whatsapp.net', 'hello'), WhatsAppAccountNotConnectedError);

    await account.connect();
    transport.sockets[0].user = { id: 'me' };
    transport.sockets[0].ev.emit('connection.update', { connection: 'open' });

    const sent = await account.sendText('chat@s.whatsapp.net', 'hello');
    assert.equal(sent.text, 'hello');
    assert.equal(sent.fromMe, true);
  });
});
