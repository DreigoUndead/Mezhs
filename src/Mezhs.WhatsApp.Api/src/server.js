import http from 'node:http';
import QRCode from 'qrcode';
import { MessageStore } from './message-store.js';
import { WhatsAppAccount } from './whatsapp-account.js';

const host = process.env.MEZHS_WHATSAPP_HOST ?? '127.0.0.1';
const port = Number(process.env.MEZHS_WHATSAPP_PORT ?? 3217);
const authDir = process.env.MEZHS_WHATSAPP_AUTH_DIR ?? new URL('../data/auth', import.meta.url).pathname;

const store = new MessageStore();
const account = new WhatsAppAccount({ authDir, store });

const server = http.createServer(async (request, response) => {
  try {
    await route(request, response);
  } catch (error) {
    console.error(error);
    json(response, 500, { error: error instanceof Error ? error.message : String(error) });
  }
});

async function route(request, response) {
  const url = new URL(request.url, `http://${request.headers.host ?? `${host}:${port}`}`);

  if (request.method === 'GET' && url.pathname === '/account/status') {
    return json(response, 200, account.status());
  }

  if (request.method === 'POST' && url.pathname === '/account/connect') {
    return json(response, 200, await account.connect());
  }

  if (request.method === 'GET' && url.pathname === '/account/qr') {
    const qr = account.getQr();
    if (!qr) return json(response, 404, { error: 'No QR code is currently available.' });

    const svg = await QRCode.toString(qr, { type: 'svg' });
    response.writeHead(200, { 'content-type': 'image/svg+xml; charset=utf-8' });
    return response.end(svg);
  }

  if (request.method === 'POST' && url.pathname === '/account/disconnect') {
    return json(response, 200, await account.disconnect());
  }

  if (request.method === 'DELETE' && url.pathname === '/account/session') {
    return json(response, 200, await account.deleteSession());
  }

  if (request.method === 'GET' && url.pathname === '/chats') {
    return json(response, 200, store.listChats());
  }

  if (request.method === 'GET' && url.pathname === '/messages') {
    const limit = integerQuery(url, 'limit', 20, 1, 500);
    return json(response, 200, store.list({
      chatId: url.searchParams.get('chatId'),
      limit,
      beforeId: url.searchParams.get('beforeId'),
      afterId: url.searchParams.get('afterId'),
      search: url.searchParams.get('q'),
    }));
  }

  const messageMatch = request.method === 'GET' && url.pathname.match(/^\/messages\/([^/]+)$/);
  if (messageMatch) {
    const message = store.get(decodeURIComponent(messageMatch[1]));
    return message
      ? json(response, 200, message)
      : json(response, 404, { error: 'Message not found.' });
  }

  if (request.method === 'POST' && url.pathname === '/messages') {
    const body = await readJson(request);
    if (typeof body.chatId !== 'string' || !body.chatId.trim()) {
      return json(response, 400, { error: 'chatId is required.' });
    }
    if (typeof body.text !== 'string' || !body.text.length) {
      return json(response, 400, { error: 'text is required.' });
    }

    return json(response, 201, await account.sendText(body.chatId, body.text));
  }

  return json(response, 404, { error: 'Not found.' });
}

function json(response, status, value) {
  response.writeHead(status, { 'content-type': 'application/json; charset=utf-8' });
  response.end(JSON.stringify(value));
}

async function readJson(request) {
  const chunks = [];
  let size = 0;
  for await (const chunk of request) {
    size += chunk.length;
    if (size > 1024 * 1024) throw new Error('Request body is too large.');
    chunks.push(chunk);
  }

  if (!chunks.length) return {};
  return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}

function integerQuery(url, name, fallback, min, max) {
  const value = url.searchParams.get(name);
  if (value == null) return fallback;
  const parsed = Number(value);
  if (!Number.isInteger(parsed) || parsed < min || parsed > max) {
    throw new Error(`${name} must be an integer from ${min} to ${max}.`);
  }
  return parsed;
}

server.listen(port, host, () => {
  console.log(`MEŽS WhatsApp API listening on http://${host}:${port}`);
  account.connect().catch(error => console.error('Initial WhatsApp connection failed:', error));
});
