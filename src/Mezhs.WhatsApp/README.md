# MEŽS WhatsApp CLI

Thin C# console client for `Mezhs.WhatsApp.Api`. It exposes deterministic WhatsApp operations through the shared `Mezhs.Console` command framework. Agent reasoning, listener policy, message-processing checkpoints and duplicate-event handling belong outside this application.

The CLI talks only to the local gateway at `127.0.0.1`. It uses `MEZHS_WHATSAPP_PORT` when set, otherwise port `3217`.

Outgoing messages are automatically prefixed with `[<identity>] ` when `MEZHS_WHATSAPP_IDENTITY` is configured. The caller supplies only the message body; identity marking is owned by the CLI.

## Commands

```text
Status
Connect
Qr
Disconnect
DeleteSession
Chats
Get <messageId> [chatId]
GetLast <chatId> [count=10]
GetBefore <chatId> <messageId> [count=20]
GetAfter <chatId> <messageId> [count=20]
Search <chatId> <query> [count=20]
Send <chatId> <text>
Test
```

`Qr` returns the current pairing QR as SVG text and can be redirected to an `.svg` file for manual pairing.

Command output follows the active `Mezhs.Console` return-value behavior. The DTOs are intentionally kept as simple property-based objects so they can adopt the shared Console return-object format when that foundation is merged.

## Validation

```text
dotnet run --project src/Mezhs.WhatsApp -- Validate
dotnet run --project src/Mezhs.WhatsApp -- Test
```

`Test` uses an in-process fake HTTP handler and does not contact WhatsApp.
