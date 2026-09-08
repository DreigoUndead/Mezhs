import fs from 'node:fs/promises';
import path from 'node:path';
import makeWASocket, {
  DisconnectReason,
  fetchLatestWaWebVersion,
  useMultiFileAuthState,
} from '@whiskeysockets/baileys';

const defaultBaileys = {
  makeWASocket,
  DisconnectReason,
  fetchLatestWaWebVersion,
  useMultiFileAuthState,
};

export class WhatsAppAccountNotConnectedError extends Error {
  constructor() {
    super('WhatsApp account is not connected.');
    this.name = 'WhatsAppAccountNotConnectedError';
  }
}

export class WhatsAppAccount {
  #authDir;
  #store;
  #baileys;
  #reconnectDelayMs;
  #socket = null;
  #state = 'disconnected';
  #qr = null;
  #accountId = null;
  #desiredConnected = false;
  #generation = 0;
  #connectPromise = null;
  #reconnectTimer = null;

  constructor({ authDir, store, baileys = defaultBaileys, reconnectDelayMs = 1000 }) {
    this.#authDir = path.resolve(authDir);
    this.#store = store;
    this.#baileys = baileys;
    this.#reconnectDelayMs = reconnectDelayMs;
  }

  status() {
    return {
      state: this.#state,
      connected: this.#state === 'connected',
      qrAvailable: Boolean(this.#qr),
      accountId: this.#accountId,
    };
  }

  getQr() {
    return this.#qr;
  }

  async connect() {
    this.#desiredConnected = true;
    this.#clearReconnect();

    if (this.#state === 'connected' || this.#state === 'waitingForQr') {
      return this.status();
    }

    if (this.#connectPromise) {
      await this.#connectPromise;
      return this.status();
    }

    const generation = ++this.#generation;
    const promise = this.#open(generation);
    this.#connectPromise = promise;

    try {
      await promise;
      return this.status();
    } catch (error) {
      if (generation === this.#generation) this.#setDisconnected();
      throw error;
    } finally {
      if (this.#connectPromise === promise) this.#connectPromise = null;
    }
  }

  async #open(generation) {
    this.#state = 'connecting';
    this.#qr = null;

    await fs.mkdir(this.#authDir, { recursive: true });
    if (!this.#isCurrent(generation)) return;

    const { state, saveCreds } = await this.#baileys.useMultiFileAuthState(this.#authDir);
    if (!this.#isCurrent(generation)) return;

    const { version } = await this.#baileys.fetchLatestWaWebVersion();
    if (!this.#isCurrent(generation)) return;

    const socket = this.#baileys.makeWASocket({
      auth: state,
      version,
      markOnlineOnConnect: false,
      syncFullHistory: true,
      getMessage: async key => this.#store.getRaw(key)?.message,
    });

    if (!this.#isCurrent(generation)) {
      socket.end?.(new Error('Superseded WhatsApp connection'));
      return;
    }

    this.#socket = socket;
    socket.ev.on('creds.update', saveCreds);
    socket.ev.on('messaging-history.set', ({ chats, messages }) => {
      this.#store.upsertChats(chats);
      this.#store.upsertMessages(messages);
    });
    socket.ev.on('chats.upsert', chats => this.#store.upsertChats(chats));
    socket.ev.on('chats.update', chats => this.#store.updateChats(chats));
    socket.ev.on('chats.delete', ids => this.#store.deleteChats(ids));
    socket.ev.on('messages.upsert', ({ messages, requestId }) => {
      if (requestId) return;
      this.#store.upsertMessages(messages);
    });
    socket.ev.on('messages.update', updates => this.#store.updateMessages(updates));
    socket.ev.on('messages.delete', deletion => this.#store.deleteMessages(deletion));
    socket.ev.on('connection.update', update => this.#onConnectionUpdate(socket, generation, update));
  }

  #onConnectionUpdate(socket, generation, { connection, lastDisconnect, qr }) {
    if (!this.#isActiveSocket(socket, generation)) return;

    if (qr) {
      this.#qr = qr;
      this.#state = 'waitingForQr';
    }

    if (connection === 'open') {
      this.#qr = null;
      this.#state = 'connected';
      this.#accountId = socket.user?.id ?? null;
      return;
    }

    if (connection !== 'close') return;

    this.#setDisconnected();
    const statusCode = lastDisconnect?.error?.output?.statusCode;
    if (statusCode === this.#baileys.DisconnectReason.loggedOut) {
      this.#desiredConnected = false;
      return;
    }

    if (this.#desiredConnected) this.#scheduleReconnect(generation);
  }

  async disconnect() {
    this.#desiredConnected = false;
    ++this.#generation;
    this.#clearReconnect();
    this.#connectPromise = null;

    const socket = this.#socket;
    this.#setDisconnected();
    socket?.end(new Error('Disconnected by API'));
    return this.status();
  }

  async deleteSession() {
    this.#desiredConnected = false;
    ++this.#generation;
    this.#clearReconnect();

    const pendingConnect = this.#connectPromise;
    this.#connectPromise = null;
    const socket = this.#socket;
    this.#setDisconnected();

    if (socket) {
      try {
        await socket.logout();
      } catch {
        socket.end(new Error('Session deleted by API'));
      }
    }

    if (pendingConnect) {
      try {
        await pendingConnect;
      } catch {
        // A failed superseded connect must not prevent session deletion.
      }
    }

    await fs.rm(this.#authDir, { recursive: true, force: true });
    return this.status();
  }

  async sendText(chatId, text) {
    if (this.#state !== 'connected' || !this.#socket) {
      throw new WhatsAppAccountNotConnectedError();
    }

    const message = await this.#socket.sendMessage(chatId, { text });
    this.#store.upsertMessages([message]);
    return this.#store.get(message.key.id, message.key.remoteJid);
  }

  #isCurrent(generation) {
    return this.#desiredConnected && generation === this.#generation;
  }

  #isActiveSocket(socket, generation) {
    return this.#isCurrent(generation) && socket === this.#socket;
  }

  #setDisconnected() {
    this.#socket = null;
    this.#qr = null;
    this.#accountId = null;
    this.#state = 'disconnected';
  }

  #scheduleReconnect(generation) {
    this.#clearReconnect();
    this.#reconnectTimer = setTimeout(() => {
      this.#reconnectTimer = null;
      if (!this.#isCurrent(generation)) return;
      this.connect().catch(error => console.error('WhatsApp reconnect failed:', error));
    }, this.#reconnectDelayMs);
  }

  #clearReconnect() {
    if (this.#reconnectTimer) clearTimeout(this.#reconnectTimer);
    this.#reconnectTimer = null;
  }
}
