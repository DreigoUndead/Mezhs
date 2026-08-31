import test from 'node:test';
import assert from 'node:assert/strict';
import { MessageStore } from '../src/message-store.js';

const message = (id, timestamp, text, chatId = 'chat@s.whatsapp.net', fromMe = false) => ({
  key: { id, remoteJid: chatId, fromMe },
  messageTimestamp: timestamp,
  message: { conversation: text },
});

test('messages are ordered and can be paged around anchors', () => {
  const store = new MessageStore();
  store.upsertMessages([
    message('b', 20, 'second'),
    message('a', 10, 'first'),
    message('c', 30, 'third'),
  ]);

  assert.deepEqual(store.list({ limit: 2 }).map(x => x.id), ['b', 'c']);
  assert.deepEqual(store.list({ beforeId: 'c', limit: 10 }).map(x => x.id), ['a', 'b']);
  assert.deepEqual(store.list({ afterId: 'a', limit: 10 }).map(x => x.id), ['b', 'c']);
});

test('messages can be filtered by chat and text', () => {
  const store = new MessageStore();
  store.upsertMessages([
    message('a', 10, 'Alpha', 'one@s.whatsapp.net'),
    message('b', 20, 'Beta', 'two@s.whatsapp.net'),
    message('c', 30, 'alphabet', 'one@s.whatsapp.net', true),
  ]);

  assert.deepEqual(
    store.list({ chatId: 'one@s.whatsapp.net', search: 'ALPHA', limit: 10 }).map(x => x.id),
    ['a', 'c'],
  );
  assert.equal(store.get('c').fromMe, true);
});

test('history updates also produce chat entries', () => {
  const store = new MessageStore();
  store.upsertMessages([message('a', 10, 'hello', 'group@g.us')]);
  store.upsertChats([{ id: 'group@g.us', name: 'Test group', unreadCount: 2, conversationTimestamp: 10 }]);

  assert.deepEqual(store.listChats(), [{
    id: 'group@g.us',
    name: 'Test group',
    unreadCount: 2,
    conversationTimestamp: '1970-01-01T00:00:10.000Z',
  }]);
});
