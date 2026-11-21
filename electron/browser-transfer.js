// Bounded binary transfer on the existing Electron authenticated session.
// Signed cross-origin storage always omits account credentials and rejects redirects.
const fs = require("node:fs");
const fsp = require("node:fs/promises");
const os = require("node:os");
const path = require("node:path");
const { randomUUID } = require("node:crypto");
const { Readable } = require("node:stream");

const MAX_FILE_BYTES = 100 * 1024 * 1024;
const SIGNED_STORAGE_SUFFIXES = [
  "blob.core.windows.net", "blob.core.usgovcloudapi.net",
  "blob.core.chinacloudapi.cn", "oaiusercontent.com",
  "openai.com", "chatgpt.com"
];

function safeSignedUrl(value, origin) {
  if (typeof value !== "string" || value.length > 8192)
    throw new Error("Invalid signed transfer destination.");
  const destination = new URL(value);
  if (destination.protocol !== "https:" || destination.username ||
      destination.password || destination.port && destination.port !== "443" ||
      destination.hash || !destination.hostname ||
      !SIGNED_STORAGE_SUFFIXES.some(s =>
        destination.hostname === s || destination.hostname.endsWith("." + s)))
    throw new Error("Signed transfer destination is not an authorized HTTPS storage host.");
  return destination;
}

function safeTransferHeaders(input, contentType) {
  if (input != null && (typeof input !== "object" || Array.isArray(input)))
    throw new Error("Invalid signed transfer headers.");
  const output = {};
  for (const [name, value] of Object.entries(input || {})) {
    if (!/^x-ms-[a-z0-9-]{1,64}$/i.test(name) ||
        typeof value !== "string" || value.length > 4096 ||
        /[\r\n]/.test(value))
      throw new Error("Unsafe signed transfer header.");
    output[name] = value;
  }
  if (typeof contentType !== "string" || contentType.length > 255 ||
      /[\r\n]/.test(contentType))
    throw new Error("Unsafe signed transfer content type.");
  output["Content-Type"] = contentType;
  output["x-ms-blob-type"] = "BlockBlob";
  output["x-ms-version"] = "2020-04-08";
  return output;
}

function safeFileName(value) {
  const base = path.basename(String(value || "download"))
    .replace(/[\\/:*?"<>|\x00-\x1f]/g, "_").slice(0, 150);
  return base && base !== "." && base !== ".." ? base : "download";
}

async function cancelUnreadBody(body) {
  try { await body?.cancel?.(); }
  catch { /* Preserve the original transfer failure. */ }
}

async function executeBinaryTransfer({ session, args, signal, reportProgress }, policy) {
  const action = args?.action;
  if (action !== "upload" && action !== "download")
    throw new Error("Binary transfer action is not supported.");
  if (typeof policy?.origin !== "string" || policy.origin !== new URL(policy.origin).origin)
    throw new Error("Provider signed transfer policy is unavailable.");
  const destination = safeSignedUrl(args.url, policy.origin);
  const isSameOrigin = destination.origin === policy.origin;
  signal?.throwIfAborted();

  if (action === "upload") {
    const headers = safeTransferHeaders(args.headers, args.contentType);
    if (typeof args.localPath !== "string" || !path.isAbsolute(args.localPath))
      throw new Error("Upload requires a local absolute file path.");
    const info = await fsp.stat(args.localPath);
    if (!info.isFile() || info.size > MAX_FILE_BYTES)
      throw new Error("Upload file is not a supported regular file or exceeds 100 MiB.");
    const source = fs.createReadStream(args.localPath, { signal });
    try {
      const response = await session.fetch(destination.href, {
        method: "PUT", credentials: "omit", redirect: "error",
        headers, body: Readable.toWeb(source), duplex: "half", signal
      });
      // Upload responses contain no needed data; release their body on success or failure.
      await cancelUnreadBody(response.body);
      if (!response.ok)
        throw new Error("Signed file upload failed with HTTP " + response.status + ".");
      reportProgress?.({ state: "retrieving", detail: "Signed file upload completed." });
      return { size: info.size, statusCode: response.status };
    } finally {
      source.destroy();
    }
  }

  if (typeof args.name !== "string" || typeof policy.sameOriginDownload !== "function")
    throw new Error("Signed download requires a filename and provider policy.");
  const response = isSameOrigin
    ? await policy.sameOriginDownload(session, destination.href, signal)
    : await session.fetch(destination.href, {
        method: "GET", credentials: "omit", redirect: "error", signal
      });
  if (!response.ok) {
    await cancelUnreadBody(response.body);
    throw new Error("Signed file download failed with HTTP " + response.status + ".");
  }
  const declaredSize = Number(response.headers.get("content-length"));
  if (Number.isFinite(declaredSize) && declaredSize > MAX_FILE_BYTES) {
    await cancelUnreadBody(response.body);
    throw new Error("Signed file download exceeds 100 MiB.");
  }
  const name = safeFileName(args.name);
  const localPath = path.join(os.tmpdir(), "mezhs-artifact-" + randomUUID() + "-" + name);
  let output;
  let total = 0;
  try {
    output = await fsp.open(localPath, "wx");
    if (!response.body?.getReader) {
      await cancelUnreadBody(response.body);
      throw new Error("Signed download did not provide an incremental body.");
    }
    const reader = response.body.getReader();
    let completed = false;
    try {
      while (true) {
        signal?.throwIfAborted();
        const { value, done } = await reader.read();
        if (done) { completed = true; break; }
        total += value.byteLength;
        if (total > MAX_FILE_BYTES)
          throw new Error("Signed file download exceeds 100 MiB.");
        await output.writeFile(value);
      }
    } finally {
      if (!completed) {
        try { await reader.cancel(); }
        catch { /* Preserve the original transfer failure. */ }
      }
      reader.releaseLock();
    }
    await output.close();
    output = null;
    reportProgress?.({ state: "retrieving", detail: "Signed file download completed." });
    const contentType = response.headers.get("content-type");
    return { size: total, statusCode: response.status, localPath,
      contentType: contentType && contentType.length < 256 ? contentType : null };
  } catch (error) {
    await output?.close().catch(() => {});
    await fsp.unlink(localPath).catch(() => {});
    throw error;
  }
}

module.exports = { executeBinaryTransfer, safeSignedUrl, safeTransferHeaders };
