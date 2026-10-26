const fs = require('node:fs/promises');
const { randomUUID } = require('node:crypto');
const os = require('node:os');
const path = require('node:path');
const { ORIGIN, API, apiFetch, apiJson } = require('./account-transport.js');

function imageDimensions(bytes, contentType) {
  if (contentType === 'image/png' && bytes.length >= 24 &&
      bytes.toString('ascii', 1, 4) === 'PNG')
    return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20) };
  if (contentType === 'image/gif' && bytes.length >= 10 &&
      bytes.toString('ascii', 0, 3) === 'GIF')
    return { width: bytes.readUInt16LE(6), height: bytes.readUInt16LE(8) };
  if (contentType === 'image/jpeg' && bytes[0] === 0xff && bytes[1] === 0xd8) {
    for (let i = 2; i + 9 < bytes.length;) {
      if (bytes[i] !== 0xff) break;
      const marker = bytes[i + 1];
      if (marker === 0xda || marker === 0xd9) break;
      const length = bytes.readUInt16BE(i + 2);
      if (length < 2) break;
      if ([0xc0, 0xc1, 0xc2, 0xc3].includes(marker) && i + 9 < bytes.length)
        return { width: bytes.readUInt16BE(i + 7), height: bytes.readUInt16BE(i + 5) };
      i += 2 + length;
    }
  }
  if (contentType === 'image/webp' && bytes.length >= 30 &&
      bytes.toString('ascii', 0, 4) === 'RIFF' && bytes.toString('ascii', 8, 12) === 'WEBP') {
    const kind = bytes.toString('ascii', 12, 16);
    if (kind === 'VP8X')
      return { width: 1 + bytes.readUIntLE(24, 3), height: 1 + bytes.readUIntLE(27, 3) };
    if (kind === 'VP8 ')
      return { width: bytes.readUInt16LE(26) & 0x3fff, height: bytes.readUInt16LE(28) & 0x3fff };
    if (kind === 'VP8L' && bytes[20] === 0x2f)
      return { width: 1 + (((bytes[22] & 0x3f) << 8) | bytes[21]),
        height: 1 + (((bytes[24] & 0x0f) << 10) | (bytes[23] << 2) | (bytes[22] >> 6)) };
  }
  return null;
}

async function uploadFiles(session, token, files) {
  const uploaded = [];
  for (const file of files) {
    const bytes = await fs.readFile(file.path);
    const contentType = String(file.contentType || 'application/octet-stream');
    const dimensions = contentType.startsWith('image/') ? imageDimensions(bytes, contentType) : null;
    if (contentType.startsWith('image/') && (!dimensions?.width || !dimensions?.height))
      throw new Error('Unsupported or invalid image format or dimensions.');
    const useCase = contentType.startsWith('image/') ? 'multimodal' : 'my_files';
    const created = await apiJson(session, token, API.files, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        file_name: file.name,
        file_size: bytes.length,
        use_case: useCase,
        mime_type: contentType,
        entry_surface: 'chat_composer',
        timezone_offset_min: new Date().getTimezoneOffset()
      })
    });
    if (created?.status === 'error' || !created?.file_id || !created?.upload_url)
      throw new Error('ChatGPT did not provide a usable file upload destination.');

    const strategy = created.direct_library_upload_strategy;
    if (strategy?.kind === 'direct_azure_multipart' && strategy.part_count >= 2)
      throw new Error('ChatGPT multipart file uploads are not supported by this integration.');
    const destination = new URL(created.upload_url);
    if (/^\/(?:backend-)?api\/estuary\//i.test(destination.pathname))
      throw new Error('ChatGPT estuary file uploads are not supported by this integration.');
    if (destination.protocol !== 'https:')
      throw new Error('ChatGPT file upload destination must use HTTPS.');
    const transferred = await session.fetch(destination.href, {
      method: 'PUT',
      credentials: 'omit',
      headers: {
        'Content-Type': contentType,
        // Azure-backed signed destinations can have custom hostnames.
        'x-ms-blob-type': 'BlockBlob',
        'x-ms-version': '2020-04-08',
        ...(created.upload_headers || {})
      },
      body: bytes
    });
    if (!transferred.ok) {
      // Log only validated storage error codes; signed upload URLs and response bodies are private.
      const code = transferred.headers?.get('x-ms-error-code');
      const safeCode = /^[A-Za-z][A-Za-z0-9_-]{0,63}$/.test(code || '') ? ' (' + code + ')' : '';
      throw new Error('ChatGPT file transfer failed with HTTP ' + transferred.status + safeCode + '.');
    }

    // Match the current frontend: processing stream, not /files/{id}/uploaded.
    const processed = await apiFetch(session, token, API.processFileUpload, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        file_id: created.file_id,
        use_case: useCase,
        index_for_retrieval: useCase === 'my_files',
        file_name: file.name,
        entry_surface: 'chat_composer',
        mime_type: contentType
      })
    });
    const processingBody = await processed.text();
    const rawType = String(processed.headers?.get?.('content-type') || '').split(';')[0].trim().toLowerCase();
    const safeType = ['text/event-stream', 'application/json', 'application/x-ndjson', 'text/plain'].includes(rawType) ? rawType : 'other';
    const trimmedBody = processingBody.trim();
    const bodyShape = !trimmedBody ? 'empty' : /^(data:|event:)/.test(trimmedBody) ? 'sse' : trimmedBody.startsWith('{') ? 'json-object' : trimmedBody.startsWith('[') ? 'json-array' : 'other';
    const safeState = value => {
      if (typeof value !== 'string') return 'absent';
      if (/^(?:pending|processing|started|in_progress|uploaded|ready|completed|complete|done|success|failed|error|cancelled|file_ready)$/.test(value)) return value;
      if (/^file[._-](?:processing|upload)[._-][a-z0-9_.-]{1,60}$/i.test(value)) return value;
      return 'other';
    };
    const jsonLines = processingBody.split(String.fromCharCode(10)).filter(line => line.trim());
    const allowedKeys = ['event', 'type', 'status', 'file_id', 'id', 'data', 'payload', 'result', 'error', 'message', 'progress', 'files'];
    const lineObservations = jsonLines.slice(0, 8).map(raw => {
      const line = raw.trim();
      if (line.startsWith('data:') || line.startsWith('event:')) return 'sse';
      let obj;
      try { obj = JSON.parse(line); } catch { return 'non-json'; }
      if (!obj || typeof obj !== 'object' || Array.isArray(obj)) return 'non-object-json';
      const keys = allowedKeys.filter(key => Object.prototype.hasOwnProperty.call(obj, key));
      const nested = [obj.data, obj.payload, obj.result].find(x => x && typeof x === 'object' && !Array.isArray(x));
      const nestedKeys = nested ? allowedKeys.filter(key => Object.prototype.hasOwnProperty.call(nested, key)) : [];
      return 'object(keys=' + (keys.join('|') || 'none') + ',event=' + safeState(obj.event) + ',type=' + safeState(obj.type) + ',status=' + safeState(obj.status) + ',nestedKeys=' + (nestedKeys.join('|') || 'none') + ',nestedStatus=' + safeState(nested?.status) + ')';
    });
    let jsonStatus = 'none';
    if (bodyShape === 'json-object') {
      try {
        const parsed = JSON.parse(trimmedBody);
        if (typeof parsed?.status === 'string' && /^[A-Za-z][A-Za-z0-9._-]{0,39}$/.test(parsed.status)) jsonStatus = parsed.status;
      } catch { jsonStatus = 'invalid-json'; }
    }
    let ready = false;
    let failed = false;
    const observations = [];
    let invalidJsonFrames = 0;
    let frameEvent = '';
    for (const raw of processingBody.split(String.fromCharCode(10))) {
      const line = raw.trim();
      if (line.startsWith('event:')) frameEvent = line.slice(6).trim();
      const sseData = line.startsWith('data:');
      // The live processing endpoint emits bare newline-delimited JSON despite its SSE content type.
      if (!sseData && !line.startsWith('{')) continue;
      let event;
      try { event = JSON.parse(sseData ? line.slice(5).trim() : line); } catch { invalidJsonFrames++; continue; }
      const eventName = event?.event || (sseData ? frameEvent : '');
      if (observations.length < 8) {
        const safeName = typeof eventName === 'string' && /^[A-Za-z][A-Za-z0-9._-]{0,79}$/.test(eventName) ? eventName : 'unnamed';
        const idState = value => value == null ? 'absent' : value === created.file_id ? 'match' : 'different';
        const nested = event?.data && typeof event.data === 'object' ? event.data : null;
        observations.push(safeName + '(topId=' + idState(event?.file_id) + ',dataId=' + idState(nested?.file_id) + ')');
      }
      if (event?.file_id !== created.file_id || typeof eventName !== 'string') continue;
      if (eventName.endsWith('.error') || eventName.endsWith('.failed') ||
          eventName.endsWith('.cancelled') || eventName.endsWith('.unknown')) failed = true;
      if (eventName === 'file.processing.completed' ||
          eventName === 'file.processing.file_ready') ready = true;
    }
    if (failed || !ready)
      throw new Error('ChatGPT file processing did not confirm success (HTTP ' + processed.status + '; type=' + safeType + '; size=' + Buffer.byteLength(processingBody) + '; shape=' + bodyShape + '; jsonStatus=' + jsonStatus + '; lines=' + jsonLines.length + '; samples=' + (lineObservations.join(', ') || 'none') + '; events=' + (observations.join(', ') || 'none') + '; invalidJsonFrames=' + invalidJsonFrames + ').');
    uploaded.push({ id: created.file_id, name: file.name, contentType, size: bytes.length,
      ...(dimensions || {}) });
  }
  return uploaded;
}

function collectFileRefs(value, refs) {
  if (!value || typeof value !== "object") return;
  if (typeof value.asset_pointer === "string") {
    const match = /^(?:file-service|sediment):\/\/(.+)$/.exec(value.asset_pointer);
    if (match) refs.set(match[1], value.name || "download");
  }
  if (Array.isArray(value.attachments)) {
    for (const attachment of value.attachments)
      if (attachment?.id) refs.set(attachment.id, attachment.name || "download");
  }
  for (const nested of Object.values(value))
    if (nested && typeof nested === "object") collectFileRefs(nested, refs);
}

async function downloadFiles(session, token, refs) {
  const artifacts = [];
  for (const [id, requestedName] of refs) {
    try {
      const download = await apiJson(session, token, API.fileDownload(id));
      if (!download?.download_url) continue;
      const response = await session.fetch(download.download_url, {
        headers: download.download_url.startsWith(ORIGIN)
          ? { Authorization: `Bearer ${token}` }
          : undefined,
        credentials: "include"
      });
      if (!response.ok) continue;
      const name = path.basename(String(requestedName || "download")) || "download";
      // The importer deletes this file after copying it; avoid an orphaned directory.
      const localPath = path.join(os.tmpdir(), `mezhs-artifact-${randomUUID()}-${name}`);
      await fs.writeFile(localPath, new Uint8Array(await response.arrayBuffer()));
      artifacts.push({
        url: download.download_url,
        name,
        contentType: response.headers.get("content-type") || null,
        localPath
      });
    } catch (error) {
      console.error(`Could not download ChatGPT artifact '${id}': ${error}`);
    }
  }
  return artifacts;
}

module.exports = { imageDimensions, uploadFiles, collectFileRefs, downloadFiles };
