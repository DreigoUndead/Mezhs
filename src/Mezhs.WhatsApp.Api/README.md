# MEŽS WhatsApp API

Thin local HTTP gateway over a single WhatsApp account. It owns WhatsApp connectivity only; agent behavior, message identity markers, listener policy, and processing state belong outside this application.

The gateway uses Baileys as the WhatsApp Web transport. Baileys is unofficial and is not affiliated with WhatsApp.

## Run

Requires Node.js 20 or newer.

```bash
npm install
npm start
```

By default the API listens on `127.0.0.1:3217` and stores the linked-device credentials under `data/auth`.

Configuration:

- `MEZHS_WHATSAPP_HOST`
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
POST /messages
```

Send text:

```json
{
  "chatId": "37120000000@s.whatsapp.net",
  "text": "hello"
}
```

Returned messages expose WhatsApp facts such as `fromMe`; they do not try to decide whether an outgoing message was typed by the human or sent by an agent.

Messages and chats are currently held only in memory. They are populated from Baileys history-sync and live events after the process connects. The auth session is persisted separately.
