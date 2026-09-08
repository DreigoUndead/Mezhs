import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { once } from 'node:events';
import { MessageStore } from '../src/message-store.js';
import { WhatsAppAccountNotConnectedError } from '../src/whatsapp-account.js';
import {
  API_HOST,
  DEFAULT_AUTH_DIR,
  createWhatsAppApiServer,
} from '../src/server.js';

function fakeAccount(overrides = {}) {
  return {
    status: () => ({ state: 'disconnected', connected: false, qrAvailable: false, accountId: null }),
    connect: async () => ({ state: 'connecting', connected: false, qrAvailable: false, accountId: null }),
    getQr: () => null,
    disconnect: async () => ({ state: 'disconnected', connected: false, qrAvailable: false, accountId: null }),
    deleteSession: async () => ({ state: 'disconnected', connected: false, qrAvailable: false, accountId: null }),
    sendText: async () => { throw new WhatsAppAccountNotConnectedError(); },
    ...overrides,
  };
}

async function withServer(run, account = fakeAccount(), store = new MessageStore()) {
  const server = createWhatsAppApiServer({ account, store });
  server.listen(0, API_HOST);
  await once(server, 'listening');
  const address = server.address();
  const baseUrl = `http://${API_HOST}:${address.port}`;
  try {
    await run({ baseUrl, server, store });
  } finally {
    server.close();
    await once(server, 'close');
  }
}

test('API is explicitly loopback-only', () => {
  assert.equal(API_HOST, '127.0.0.1');
});

test('default auth directory is a native filesystem path', () => {
  assert.equal(path.basename(DEFAULT_AUTH_DIR), 'auth');
  assert.equal(path.basename(path.dirname(DEFAULT_AUTH_DIR)), 'data');
  if (process.platform === 'win32') {
    assert.match(DEFAULT_AUTH_DIR, /^[A-Za-z]:\\/);
  } else {
    assert.ok(DEFAULT_AUTH_DIR.startsWith('/'));
  }
});

test('invalid query input is a 400 rather than a 500', async () => {
  await withServer(async ({ baseUrl }) => {
    const response = await fetch(`${baseUrl}/messages?limit=nope`);
    assert.equal(response.status, 400);
    assert.deepEqual(await response.json(), { error: 'limit must be an integer from 1 to 500.' });
  });
});

test('malformed and non-object JSON are client errors', async () => {
  await withServer(async ({ baseUrl }) => {
    const malformed = await fetch(`${baseUrl}/messages`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: '{',
    });
    assert.equal(malformed.status, 400);

    const array = await fetch(`${baseUrl}/messages`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: '[]',
    });
    assert.equal(array.status, 400);
  });
});

test('oversized request body is a 413', async () => {
  await withServer(async ({ baseUrl }) => {
    const response = await fetch(`${baseUrl}/messages`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ chatId: 'chat@s.whatsapp.net', text: 'x'.repeat(1024 * 1024) }),
    });
    assert.equal(response.status, 413);
  });
});

test('sending while disconnected is a 409', async () => {
  await withServer(async ({ baseUrl }) => {
    const response = await fetch(`${baseUrl}/messages`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ chatId: 'chat@s.whatsapp.net', text: 'hello' }),
    });
    assert.equal(response.status, 409);
    assert.deepEqual(await response.json(), { error: 'WhatsApp account is not connected.' });
  });
});

test('chatId disambiguates duplicate message ids', async () => {
  const store = new MessageStore();
  store.upsertMessages([
    {
      key: { id: 'same', remoteJid: 'one@s.whatsapp.net', fromMe: false },
      messageTimestamp: 10,
      message: { conversation: 'one' },
    },
    {
      key: { id: 'same', remoteJid: 'two@s.whatsapp.net', fromMe: false },
      messageTimestamp: 20,
      message: { conversation: 'two' },
    },
  ]);

  await withServer(async ({ baseUrl }) => {
    const ambiguous = await fetch(`${baseUrl}/messages/same`);
    assert.equal(ambiguous.status, 404);

    const exact = await fetch(`${baseUrl}/messages/same?chatId=${encodeURIComponent('two@s.whatsapp.net')}`);
    assert.equal(exact.status, 200);
    assert.equal((await exact.json()).text, 'two');
  }, fakeAccount(), store);
});
