import fs from 'node:fs/promises';
import path from 'node:path';
import makeWASocket, {
  DisconnectReason,
  fetchLatestWaWebVersion,
  useMultiFileAuthState,
} from '@whiskeysockets/baileys';

export class WhatsAppAccount {
  #authDir;
  #store;
  #socket = null;
  #state = 'disconnected';
  #qr = null;
  #accountId = null;
  #stopRequested = false;
  #connectPromise = null;

  constructor({ authDir, store }) {
    this.#authDir = path.resolve(authDir);
    this.#store = store;
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
    if (this.#state === 'connected' || this.#state === 'connecting' || this.#state === 'waitingForQr') {
      return this.status();
    }

    if (this.#connectPromise) {
      await this.#connectPromise;
      return this.status();
    }

    this.#connectPromise = this.#open();
    try {
      await this.#connectPromise;
      return this.status();
    } finally {
      this.#connectPromise = null;
    }
  }

  async #open() {
    this.#stopRequested = false;
    this.#state = 'connecting';
    this.#qr = null;

    await fs.mkdir(this.#authDir, { recursive: true });
    const { state, saveCreds } = await useMultiFileAuthState(this.#authDir);
    const { version } = await fetchLatestWaWebVersion();

    const socket = makeWASocket({
      auth: state,
      version,
      markOnlineOnConnect: false,
      syncFullHistory: true,
      getMessage: async key => this.#store.getRaw(key.id)?.message,
    });

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
    socket.ev.on('connection.update', update => this.#onConnectionUpdate(socket, update));
  }

  #onConnectionUpdate(socket, { connection, lastDisconnect, qr }) {
    if (socket !== this.#socket) return;

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

    const statusCode = lastDisconnect?.error?.output?.statusCode;
    this.#socket = null;
    this.#qr = null;
    this.#accountId = null;
    this.#state = 'disconnected';

    if (!this.#stopRequested && statusCode !== DisconnectReason.loggedOut) {
      setTimeout(() => this.connect().catch(error => console.error('WhatsApp reconnect failed:', error)), 1000);
    }
  }

  async disconnect() {
    this.#stopRequested = true;
    const socket = this.#socket;
    this.#socket = null;
    this.#qr = null;
    this.#accountId = null;
    this.#state = 'disconnected';
    socket?.end(new Error('Disconnected by API'));
    return this.status();
  }

  async deleteSession() {
    this.#stopRequested = true;
    const socket = this.#socket;
    this.#socket = null;

    if (socket) {
      try {
        await socket.logout();
      } catch {
        socket.end(new Error('Session deleted by API'));
      }
    }

    await fs.rm(this.#authDir, { recursive: true, force: true });
    this.#qr = null;
    this.#accountId = null;
    this.#state = 'disconnected';
    return this.status();
  }

  async sendText(chatId, text) {
    if (this.#state !== 'connected' || !this.#socket) {
      throw new Error('WhatsApp account is not connected.');
    }

    const message = await this.#socket.sendMessage(chatId, { text });
    this.#store.upsertMessages([message]);
    return this.#store.get(message.key.id);
  }
}
