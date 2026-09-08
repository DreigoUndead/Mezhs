export class MessageAnchorNotFoundError extends Error {
  constructor(parameter, id) {
    super(`${parameter} message '${id}' was not found.`);
    this.name = 'MessageAnchorNotFoundError';
  }
}

export class MessageStore {
  #messages = new Map();
  #chats = new Map();
  #maxMessages;

  constructor({ maxMessages = 10_000 } = {}) {
    if (!Number.isInteger(maxMessages) || maxMessages < 1) {
      throw new TypeError('maxMessages must be a positive integer.');
    }
    this.#maxMessages = maxMessages;
  }

  upsertChats(chats) {
    for (const chat of chats ?? []) {
      if (!chat?.id) continue;
      this.#chats.set(chat.id, {
        ...this.#chats.get(chat.id),
        ...chat,
      });
    }
  }

  updateChats(chats) {
    this.upsertChats(chats);
  }

  deleteChats(ids) {
    const deleted = new Set(ids ?? []);
    for (const id of deleted) this.#chats.delete(id);
    if (!deleted.size) return;

    for (const [storageKey, message] of this.#messages) {
      if (deleted.has(message.key?.remoteJid)) this.#messages.delete(storageKey);
    }
  }

  upsertMessages(messages) {
    for (const message of messages ?? []) {
      const storageKey = messageStorageKey(message?.key);
      if (!storageKey) continue;
      this.#messages.set(storageKey, message);

      const chatId = message.key.remoteJid;
      if (!this.#chats.has(chatId)) {
        this.#chats.set(chatId, { id: chatId });
      }
    }

    this.#trimMessages();
  }

  updateMessages(updates) {
    for (const item of updates ?? []) {
      const storageKey = messageStorageKey(item?.key);
      if (!storageKey) continue;

      const existing = this.#messages.get(storageKey);
      if (!existing) continue;

      this.#messages.set(storageKey, {
        ...existing,
        ...item.update,
        key: {
          ...existing.key,
          ...item.key,
        },
      });
    }
  }

  deleteMessages(deletion) {
    if (!deletion) return;

    if (deletion.all && deletion.jid) {
      for (const [storageKey, message] of this.#messages) {
        if (message.key?.remoteJid === deletion.jid) this.#messages.delete(storageKey);
      }
      return;
    }

    for (const key of deletion.keys ?? []) {
      const storageKey = messageStorageKey(key);
      if (storageKey) this.#messages.delete(storageKey);
    }
  }

  getRaw(keyOrId, chatId = null) {
    if (keyOrId && typeof keyOrId === 'object') {
      const storageKey = messageStorageKey(keyOrId);
      return storageKey ? this.#messages.get(storageKey) ?? null : null;
    }

    if (typeof keyOrId !== 'string' || !keyOrId) return null;
    if (chatId) return this.#messages.get(messageStorageKey({ id: keyOrId, remoteJid: chatId })) ?? null;

    let match = null;
    for (const message of this.#messages.values()) {
      if (message.key?.id !== keyOrId) continue;
      if (match) return null;
      match = message;
    }
    return match;
  }

  get(id, chatId = null) {
    const message = this.getRaw(id, chatId);
    return message ? toMessageDto(message) : null;
  }

  list({ chatId = null, limit = 20, beforeId = null, afterId = null, search = null } = {}) {
    const before = beforeId ? this.getRaw(beforeId, chatId) : null;
    if (beforeId && !before) throw new MessageAnchorNotFoundError('beforeId', beforeId);
    const after = afterId ? this.getRaw(afterId, chatId) : null;
    if (afterId && !after) throw new MessageAnchorNotFoundError('afterId', afterId);
    const term = search?.trim().toLocaleLowerCase() || null;

    return [...this.#messages.values()]
      .filter(message => !chatId || message.key?.remoteJid === chatId)
      .filter(message => !before || compareMessages(message, before) < 0)
      .filter(message => !after || compareMessages(message, after) > 0)
      .map(toMessageDto)
      .filter(message => !term || message.text?.toLocaleLowerCase().includes(term))
      .sort(compareMessageDtos)
      .slice(-limit);
  }

  listChats() {
    return [...this.#chats.values()]
      .map(chat => ({
        id: chat.id,
        name: chat.name ?? chat.subject ?? null,
        unreadCount: chat.unreadCount ?? null,
        conversationTimestamp: toTimestamp(chat.conversationTimestamp),
      }))
      .sort((a, b) => (b.conversationTimestamp ?? '').localeCompare(a.conversationTimestamp ?? ''));
  }

  #trimMessages() {
    const excess = this.#messages.size - this.#maxMessages;
    if (excess <= 0) return;

    const oldest = [...this.#messages.entries()]
      .sort(([, a], [, b]) => compareMessages(a, b))
      .slice(0, excess);

    for (const [storageKey] of oldest) this.#messages.delete(storageKey);
  }
}

function messageStorageKey(key) {
  if (!key?.id || !key?.remoteJid) return null;
  return `${key.remoteJid}\u0000${key.id}`;
}

function toMessageDto(message) {
  const seconds = numericTimestamp(message.messageTimestamp);
  return {
    id: message.key.id,
    chatId: message.key.remoteJid ?? null,
    participantId: message.key.participant ?? null,
    fromMe: Boolean(message.key.fromMe),
    timestamp: seconds ? new Date(seconds * 1000).toISOString() : null,
    type: message.message ? Object.keys(message.message)[0] ?? null : null,
    text: extractText(message.message),
  };
}

function extractText(content) {
  if (!content) return null;
  if (content.conversation) return content.conversation;
  if (content.extendedTextMessage?.text) return content.extendedTextMessage.text;
  if (content.imageMessage?.caption) return content.imageMessage.caption;
  if (content.videoMessage?.caption) return content.videoMessage.caption;
  if (content.documentMessage?.caption) return content.documentMessage.caption;
  if (content.ephemeralMessage?.message) return extractText(content.ephemeralMessage.message);
  if (content.viewOnceMessage?.message) return extractText(content.viewOnceMessage.message);
  if (content.viewOnceMessageV2?.message) return extractText(content.viewOnceMessageV2.message);
  return null;
}

function compareMessages(a, b) {
  const time = numericTimestamp(a.messageTimestamp) - numericTimestamp(b.messageTimestamp);
  if (time !== 0) return time;
  return (a.key?.id ?? '').localeCompare(b.key?.id ?? '');
}

function compareMessageDtos(a, b) {
  const time = (a.timestamp ?? '').localeCompare(b.timestamp ?? '');
  if (time !== 0) return time;
  const chat = (a.chatId ?? '').localeCompare(b.chatId ?? '');
  if (chat !== 0) return chat;
  return a.id.localeCompare(b.id);
}

function numericTimestamp(value) {
  if (value == null) return 0;
  if (typeof value === 'number') return value;
  if (typeof value === 'bigint') return Number(value);
  if (typeof value.toNumber === 'function') return value.toNumber();
  return Number(value) || 0;
}

function toTimestamp(value) {
  const seconds = numericTimestamp(value);
  return seconds ? new Date(seconds * 1000).toISOString() : null;
}
