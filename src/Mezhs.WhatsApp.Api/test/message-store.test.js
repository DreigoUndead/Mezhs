import test from 'node:test';
import assert from 'node:assert/strict';
import { MessageAnchorNotFoundError, MessageStore } from '../src/message-store.js';

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

test('unknown paging anchors fail instead of falling back to unanchored history', () => {
  const store = new MessageStore();
  store.upsertMessages([
    message('a', 10, 'first'),
    message('b', 20, 'second'),
  ]);

  assert.throws(
    () => store.list({ beforeId: 'missing', limit: 10 }),
    error => error instanceof MessageAnchorNotFoundError && /beforeId/.test(error.message),
  );
  assert.throws(
    () => store.list({ afterId: 'missing', limit: 10 }),
    error => error instanceof MessageAnchorNotFoundError && /afterId/.test(error.message),
  );
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

test('same WhatsApp message id is isolated by chat', () => {
  const store = new MessageStore();
  store.upsertMessages([
    message('same', 10, 'one', 'one@s.whatsapp.net'),
    message('same', 20, 'two', 'two@s.whatsapp.net'),
  ]);

  assert.equal(store.get('same'), null, 'ambiguous bare id must not select the wrong chat');
  assert.equal(store.get('same', 'one@s.whatsapp.net').text, 'one');
  assert.equal(store.get('same', 'two@s.whatsapp.net').text, 'two');
  assert.equal(store.getRaw({ id: 'same', remoteJid: 'two@s.whatsapp.net' }).message.conversation, 'two');
});

test('message updates and deletes are reflected', () => {
  const store = new MessageStore();
  store.upsertMessages([
    message('a', 10, 'old', 'one@s.whatsapp.net'),
    message('b', 20, 'keep', 'one@s.whatsapp.net'),
    message('a', 30, 'other chat', 'two@s.whatsapp.net'),
  ]);

  store.updateMessages([{
    key: { id: 'a', remoteJid: 'one@s.whatsapp.net' },
    update: { message: { conversation: 'edited' } },
  }]);
  assert.equal(store.get('a', 'one@s.whatsapp.net').text, 'edited');

  store.deleteMessages({ keys: [{ id: 'b', remoteJid: 'one@s.whatsapp.net' }] });
  assert.equal(store.get('b', 'one@s.whatsapp.net'), null);

  store.deleteMessages({ jid: 'two@s.whatsapp.net', all: true });
  assert.equal(store.get('a', 'two@s.whatsapp.net'), null);
});

test('message history is bounded and evicts oldest messages', () => {
  const store = new MessageStore({ maxMessages: 2 });
  store.upsertMessages([
    message('a', 10, 'first'),
    message('b', 20, 'second'),
    message('c', 30, 'third'),
  ]);

  assert.deepEqual(store.list({ limit: 10 }).map(x => x.id), ['b', 'c']);
  assert.equal(store.get('a'), null);
});

test('deleting a chat removes its cached messages', () => {
  const store = new MessageStore();
  store.upsertMessages([
    message('a', 10, 'one', 'one@s.whatsapp.net'),
    message('b', 20, 'two', 'two@s.whatsapp.net'),
  ]);

  store.deleteChats(['one@s.whatsapp.net']);

  assert.equal(store.get('a', 'one@s.whatsapp.net'), null);
  assert.equal(store.get('b', 'two@s.whatsapp.net').text, 'two');
});
