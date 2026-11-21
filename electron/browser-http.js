// Transport-agnostic browser HTTP mechanics. The provider alone supplies authenticated
// fetch and explicitly named browser-native request hooks. No credential lives here.
const MAX_REQUEST_BYTES = 8 * 1024 * 1024;
const MAX_RESPONSE_BYTES = 16 * 1024 * 1024;
const MAX_FRAME_BYTES = 256 * 1024;
const ALLOWED_ACCEPT = new Set([
  "application/json", "text/event-stream", "application/x-ndjson", "*/*"
]);

function validateRequest(args, policy) {
  const method = String(args?.method || "").toUpperCase();
  const endpoint = args?.path;
  const body = args?.body;
  const accept = args?.accept;
  const stream = args?.stream === true;
  if (!["GET", "POST", "PATCH"].includes(method) ||
      typeof endpoint !== "string" ||
      !endpoint.startsWith(policy.pathPrefix) ||
      endpoint.startsWith("//") || endpoint.includes("\\") ||
      endpoint.includes("#") || /[\r\n]/.test(endpoint))
    throw new Error("Browser HTTP method or endpoint is not allowed.");

  const url = new URL(endpoint, policy.origin);
  if (url.origin !== policy.origin ||
      !url.pathname.startsWith(policy.pathPrefix) ||
      url.pathname + url.search !== endpoint ||
      (method === "GET" && body != null) ||
      (method !== "GET" && body != null && typeof body !== "string") ||
      (body != null && Buffer.byteLength(body, "utf8") > MAX_REQUEST_BYTES) ||
      (accept != null && !ALLOWED_ACCEPT.has(accept)) ||
      (stream && !["text/event-stream", "application/x-ndjson"].includes(accept)))
    throw new Error("Browser HTTP endpoint, request or accept header is not allowed.");

  const hook = args?.nativeHook;
  if (hook != null && (typeof hook !== "string" ||
      !Object.hasOwn(policy.nativeHooks || {}, hook)))
    throw new Error("Browser HTTP native preparation hook is not allowed.");
  return { method, endpoint, body, accept, stream, hook };
}

function checkedHeaders(value) {
  if (value == null) return {};
  if (typeof value !== "object" || Array.isArray(value))
    throw new Error("Native browser preparation returned invalid headers.");
  const headers = {};
  for (const [key, entry] of Object.entries(value)) {
    if (!/^[a-z][a-z0-9-]{0,70}$/i.test(key) ||
        ["authorization", "cookie", "host", "proxy-authorization", "content-length",
          "transfer-encoding"].includes(key.toLowerCase()) ||
        typeof entry !== "string" || /[\r\n]/.test(entry) ||
        entry.length > 8192)
      throw new Error("Native browser preparation returned unsafe headers.");
    headers[key] = entry;
  }
  return headers;
}

class StreamFramer {
  constructor(format, reportEvent) {
    this.format = format;
    this.reportEvent = reportEvent;
    this.decoder = new TextDecoder("utf-8", { fatal: true });
    this.pending = "";
    this.eventData = [];
    this.eventBytes = 0;
  }

  async write(bytes, final = false) {
    const text = typeof bytes === "string"
      ? bytes : this.decoder.decode(bytes || new Uint8Array(0), { stream: !final });
    this.pending += text;
    if (Buffer.byteLength(this.pending, "utf8") > MAX_FRAME_BYTES)
      throw new Error("Browser HTTP stream frame exceeded 256 KiB.");
    let newline;
    while ((newline = this.pending.indexOf("\n")) >= 0) {
      const line = this.pending.slice(0, newline).replace(/\r$/, "");
      this.pending = this.pending.slice(newline + 1);
      await this.line(line);
    }
    if (final) {
      if (this.pending) await this.line(this.pending.replace(/\r$/, ""));
      this.pending = "";
      if (this.format === "text/event-stream") await this.dispatch();
    }
  }

  async line(line) {
    if (Buffer.byteLength(line, "utf8") > MAX_FRAME_BYTES)
      throw new Error("Browser HTTP stream line exceeded 256 KiB.");
    if (this.format === "application/x-ndjson") {
      if (line.trim()) await this.reportEvent(line.trim());
      return;
    }
    if (line === "") { await this.dispatch(); return; }
    if (line.startsWith("data:")) {
      const data = line.slice(5).replace(/^ /, "");
      this.eventBytes += Buffer.byteLength(data, "utf8");
      if (this.eventBytes > MAX_FRAME_BYTES)
        throw new Error("Browser HTTP SSE event exceeded 256 KiB.");
      this.eventData.push(data);
    }
  }

  async dispatch() {
    if (this.eventData.length) {
      const data = this.eventData.join("\n");
      this.eventData = [];
      this.eventBytes = 0;
      if (data !== "[DONE]") await this.reportEvent(data);
    }
  }
}

async function executeBrowserHttp({ window, session, args, signal, reportProgress, reportEvent }, policy) {
  if (!policy || typeof policy.fetch !== "function" || !policy.origin ||
      !policy.pathPrefix?.startsWith("/"))
    throw new Error("Provider does not declare a valid browser HTTP policy.");
  const request = validateRequest(args, policy);
  signal?.throwIfAborted();
  reportProgress?.({ state: "preparing", detail: "Preparing browser HTTP request." });
  const nativeHeaders = request.hook
    ? checkedHeaders(await policy.nativeHooks[request.hook]({
        window, session, args: args.nativeArgs, endpoint: request.endpoint, signal
      }))
    : {};
  // The provider's fetch callback owns account credentials/cookies and returns
  // the response without transferring secrets through the local C# protocol.
  const headers = {
    ...(request.body == null ? {} : { "Content-Type": "application/json" }),
    ...(request.accept == null ? {} : { Accept: request.accept }),
    ...nativeHeaders
  };
  signal?.throwIfAborted();
  let response;
  try {
    response = await policy.fetch({
      session, endpoint: request.endpoint, options: {
        method: request.method, headers,
        ...(request.body == null ? {} : { body: request.body }),
        signal
      }
    });
  } catch (error) {
    // Preserve only whitelisted status/retry metadata, never a private response,
    // URL, signed query, bearer token, challenge or arbitrary provider exception.
    if (!Number.isInteger(error?.status) || error.status < 400 || error.status > 599)
      throw error;
    const safeCode = typeof error.providerCode === "string" &&
      /^[a-z0-9_-]{1,80}$/i.test(error.providerCode) ? error.providerCode : null;
    const retryAfterMs = Number.isSafeInteger(error.retryAfterMs) &&
      error.retryAfterMs >= 0 ? error.retryAfterMs : null;
    return { statusCode: error.status, contentType: null, body: "",
      providerCode: safeCode, retryAfterMs };
  }
  reportProgress?.({ state: "retrieving", detail: "Receiving browser HTTP response." });

  if (request.stream) {
    if (typeof reportEvent !== "function")
      throw new Error("Streaming HTTP requires an ordered event consumer.");
    const framer = new StreamFramer(request.accept, value => reportEvent(value));
    if (response.body?.getReader) {
      const reader = response.body.getReader();
      try {
        while (true) {
          signal?.throwIfAborted();
          const { value, done } = await reader.read();
          if (done) break;
          await framer.write(value);
        }
        await framer.write(new Uint8Array(0), true);
      } finally {
        reader.releaseLock();
      }
    } else {
      const text = await response.text();
      if (Buffer.byteLength(text, "utf8") > MAX_RESPONSE_BYTES)
        throw new Error("Browser HTTP response exceeded the 16 MiB limit.");
      await framer.write(text, true);
    }
    return { statusCode: response.status, contentType: response.headers.get("content-type"), body: "" };
  }

  const chunks = [];
  const decoder = new TextDecoder("utf-8", { fatal: true });
  let size = 0;
  if (response.body?.getReader) {
    const reader = response.body.getReader();
    try {
      while (true) {
        signal?.throwIfAborted();
        const { value, done } = await reader.read();
        if (done) break;
        size += value.byteLength;
        if (size > MAX_RESPONSE_BYTES)
          throw new Error("Browser HTTP response exceeded the 16 MiB limit.");
        chunks.push(decoder.decode(value, { stream: true }));
        reportProgress?.({ state: "retrieving", detail: "Received " + size + " bytes." });
      }
      chunks.push(decoder.decode());
    } finally {
      reader.releaseLock();
    }
  } else {
    const text = await response.text();
    if (Buffer.byteLength(text, "utf8") > MAX_RESPONSE_BYTES)
      throw new Error("Browser HTTP response exceeded the 16 MiB limit.");
    chunks.push(text);
  }
  return {
    statusCode: response.status,
    contentType: response.headers.get("content-type"),
    body: chunks.join("")
  };
}

module.exports = { executeBrowserHttp, StreamFramer, validateRequest, checkedHeaders };
