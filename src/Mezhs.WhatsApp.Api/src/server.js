import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import QRCode from 'qrcode';
import { MessageAnchorNotFoundError, MessageStore } from './message-store.js';
import {
  WhatsAppAccount,
  WhatsAppAccountNotConnectedError,
} from './whatsapp-account.js';

export const API_HOST = '127.0.0.1';
export const DEFAULT_AUTH_DIR = fileURLToPath(new URL('../data/auth/', import.meta.url));

export function createWhatsAppApiServer({ account, store }) {
  return http.createServer(async (request, response) => {
    try {
      await route(request, response, { account, store });
    } catch (error) {
      const status = errorStatus(error);
      if (status >= 500) console.error(error);
      json(response, status, {
        error: status >= 500 ? 'Internal server error.' : error.message,
      });
    }
  });
}

async function route(request, response, { account, store }) {
  const url = new URL(request.url, 'http://localhost');

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
    const messageId = decodePathSegment(messageMatch[1]);
    const message = store.get(messageId, url.searchParams.get('chatId'));
    return message
      ? json(response, 200, message)
      : json(response, 404, { error: 'Message not found.' });
  }

  if (request.method === 'POST' && url.pathname === '/messages') {
    const body = await readJson(request);
    if (typeof body.chatId !== 'string' || !body.chatId.trim()) {
      throw new HttpError(400, 'chatId is required.');
    }
    if (typeof body.text !== 'string' || !body.text.trim()) {
      throw new HttpError(400, 'text is required.');
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
    if (size > 1024 * 1024) throw new HttpError(413, 'Request body is too large.');
    chunks.push(chunk);
  }

  if (!chunks.length) return {};

  let value;
  try {
    value = JSON.parse(Buffer.concat(chunks).toString('utf8'));
  } catch (error) {
    if (error instanceof SyntaxError) throw new HttpError(400, 'Request body must be valid JSON.');
    throw error;
  }

  if (!value || Array.isArray(value) || typeof value !== 'object') {
    throw new HttpError(400, 'Request body must be a JSON object.');
  }
  return value;
}

function integerQuery(url, name, fallback, min, max) {
  const value = url.searchParams.get(name);
  if (value == null) return fallback;
  const parsed = Number(value);
  if (!Number.isInteger(parsed) || parsed < min || parsed > max) {
    throw new HttpError(400, `${name} must be an integer from ${min} to ${max}.`);
  }
  return parsed;
}

function decodePathSegment(value) {
  try {
    return decodeURIComponent(value);
  } catch (error) {
    if (error instanceof URIError) throw new HttpError(400, 'Message id is not valid URL encoding.');
    throw error;
  }
}

function errorStatus(error) {
  if (error instanceof HttpError) return error.status;
  if (error instanceof MessageAnchorNotFoundError) return 404;
  if (error instanceof WhatsAppAccountNotConnectedError) return 409;
  return 500;
}

class HttpError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

function configuredPort() {
  const value = Number(process.env.MEZHS_WHATSAPP_PORT ?? 3217);
  if (!Number.isInteger(value) || value < 1 || value > 65535) {
    throw new Error('MEZHS_WHATSAPP_PORT must be an integer from 1 to 65535.');
  }
  return value;
}

export function startWhatsAppApi() {
  const store = new MessageStore();
  const account = new WhatsAppAccount({
    authDir: process.env.MEZHS_WHATSAPP_AUTH_DIR ?? DEFAULT_AUTH_DIR,
    store,
  });
  const server = createWhatsAppApiServer({ account, store });
  const port = configuredPort();

  server.listen(port, API_HOST, () => {
    console.log(`MEŽS WhatsApp API listening on http://${API_HOST}:${port}`);
    account.connect().catch(error => console.error('Initial WhatsApp connection failed:', error));
  });

  return server;
}

const entryPoint = process.argv[1] ? path.resolve(process.argv[1]) : null;
if (entryPoint === fileURLToPath(import.meta.url)) startWhatsAppApi();
