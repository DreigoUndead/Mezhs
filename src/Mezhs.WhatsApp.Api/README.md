# MEŽS WhatsApp API

Thin local HTTP gateway over a single WhatsApp account. It owns WhatsApp connectivity only; agent behavior, message identity markers, listener policy, and processing state belong outside this application.

The gateway uses Baileys as the WhatsApp Web transport. Baileys is unofficial and is not affiliated with WhatsApp.

## Run

Requires Node.js 20 or newer.

```bash
npm install
npm start
```

The API listens only on `127.0.0.1:3217`. This loopback-only binding is the API security boundary; the service intentionally does not expose an unauthenticated LAN listener.

The linked-device credentials are stored under `data/auth` by default.

Configuration:

- `MEZHS_WHATSAPP_PORT`
- `MEZHS_WHATSAPP_AUTH_DIR`

The auth directory contains long-lived credentials for the WhatsApp account and must be treated as secret material.

## Account

```text
GET    /account/status
POST   /account/connect
GET    /account/qr
POST   /account/disconnect
DELETE /account/session
```

`GET /account/qr` returns the current pairing QR as SVG when pairing is required. Deleting the session logs out the linked device and removes the local credentials.

## Chats and messages

```text
GET  /chats
GET  /messages?limit=20
GET  /messages?chatId=<jid>&limit=20
GET  /messages?beforeId=<id>&limit=20
GET  /messages?afterId=<id>&limit=20
GET  /messages?q=<text>&limit=20
GET  /messages/<id>
GET  /messages/<id>?chatId=<jid>
POST /messages
```

Use `chatId` when fetching one message if the WhatsApp message id could be ambiguous across chats.

Send text:

```json
{
  "chatId": "37120000000@s.whatsapp.net",
  "text": "hello"
}
```

Returned messages expose WhatsApp facts such as `fromMe`; they do not try to decide whether an outgoing message was typed by the human or sent by an agent.

Messages and chats are held only in memory. The store consumes history-sync and live events, applies message updates/deletions, and retains at most the newest 10,000 messages to keep process memory bounded. The auth session is persisted separately.

## Dependency note

`@whiskeysockets/baileys` is intentionally pinned to `7.0.0-rc13` rather than automatically following release candidates. The current lockfile includes the GPL-3.0 `libsignal` transitive dependency even though Baileys itself and `qrcode` are MIT. Treat redistribution/bundling as a separate licensing decision; this gateway is currently intended for private local use.
