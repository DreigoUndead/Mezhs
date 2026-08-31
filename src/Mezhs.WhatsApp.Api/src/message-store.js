export class MessageStore {
  #messages = new Map();
  #chats = new Map();

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
    for (const id of ids ?? []) this.#chats.delete(id);
  }

  upsertMessages(messages) {
    for (const message of messages ?? []) {
      const id = message?.key?.id;
      if (!id) continue;
      this.#messages.set(id, message);

      const chatId = message.key.remoteJid;
      if (chatId && !this.#chats.has(chatId)) {
        this.#chats.set(chatId, { id: chatId });
      }
    }
  }

  getRaw(id) {
    return this.#messages.get(id) ?? null;
  }

  get(id) {
    const message = this.getRaw(id);
    return message ? toMessageDto(message) : null;
  }

  list({ chatId = null, limit = 20, beforeId = null, afterId = null, search = null } = {}) {
    const before = beforeId ? this.#messages.get(beforeId) : null;
    const after = afterId ? this.#messages.get(afterId) : null;
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
