const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const fsp = require("node:fs/promises");
const os = require("node:os");
const path = require("node:path");
const { executeBinaryTransfer, safeSignedUrl, safeTransferHeaders } = require("../electron/browser-transfer");

const policy = {
  origin: "https://chatgpt.com",
  sameOriginDownload: async () => { throw Error("unexpected same-origin download"); }
};
const signed = "https://example.blob.core.windows.net/container/blob?sv=version&sig=private-signature";

test("signed destinations reject insecure schemes, credentials, arbitrary targets and forged hosts", () => {
  for (const url of [
    "http://example.blob.core.windows.net/file",
    "https://username:password@example.blob.core.windows.net/file",
    "https://example.blob.core.windows.net.evil.example/file",
    "https://127.0.0.1/private",
    "https://169.254.169.254/metadata",
    "https://example.com/file",
    "https://example.blob.core.windows.net:8443/file",
    "https://example.blob.core.windows.net/file#fragment"
  ]) assert.throws(() => safeSignedUrl(url, policy.origin));
  assert.equal(safeSignedUrl(signed, policy.origin).hostname,
    "example.blob.core.windows.net");
});

test("signed storage headers disallow credential, cookie and newline injection", () => {
  for (const headers of [
    { Authorization: "Bearer secret" },
    { Cookie: "secret" },
    { "x-ms-custom": "data\r\nAuthorization: secret" },
    { "x-other": "unexpected" },
    [1, 2]
  ]) assert.throws(() => safeTransferHeaders(headers, "application/octet-stream"));
  const headers = safeTransferHeaders({ "x-ms-meta-note": "safe" }, "image/png");
  assert.equal(headers["x-ms-blob-type"], "BlockBlob");
  assert.equal(headers["x-ms-meta-note"], "safe");
  assert.equal(headers["Content-Type"], "image/png");
});

test("cross-origin upload streams bounded source without cookies or bearer credentials", async () => {
  const dir = await fsp.mkdtemp(path.join(os.tmpdir(), "mezhs-transfer-upload-"));
  const name = path.join(dir, "small.bin");
  await fsp.writeFile(name, "small upload content");
  const optionsSeen = [];
  try {
    const session = {
      async fetch(url, options) {
        optionsSeen.push({ url, options });
        assert.equal(options.credentials, "omit");
        assert.equal(options.redirect, "error");
        assert.equal(options.method, "PUT");
        assert.equal(options.headers.Authorization, undefined);
        assert.equal(options.headers.Cookie, undefined);
        const reader = options.body.getReader();
        const chunks = [];
        while (true) {
          const { value, done } = await reader.read();
          if (done) break;
          chunks.push(Buffer.from(value));
        }
        assert.equal(Buffer.concat(chunks).toString(), "small upload content");
        return new Response("", { status: 201 });
      }
    };
    const result = await executeBinaryTransfer({
      session, args: { action: "upload", url: signed,
        localPath: name, contentType: "application/octet-stream",
        headers: { "x-ms-meta-file": "test" } }
    }, policy);
    assert.equal(result.size, 20);
    assert.equal(optionsSeen.length, 1);
  } finally { await fsp.rm(dir, { recursive: true, force: true }); }
});

test("signed download is streamed, sanitized, and no redirect is followed", async () => {
  let requests = 0;
  const session = {
    async fetch(_, options) {
      requests++;
      assert.equal(options.credentials, "omit");
      assert.equal(options.redirect, "error");
      assert.equal(options.headers, undefined);
      const data = new TextEncoder().encode("first-second-third");
      let offset = 0;
      const stream = new ReadableStream({
        pull(controller) {
          if (offset >= data.length) return controller.close();
          controller.enqueue(data.subarray(offset, offset + 3));
          offset += 3;
        }
      });
      return new Response(stream, { status: 200,
        headers: { "content-type": "application/octet-stream" } });
    }
  };
  const result = await executeBinaryTransfer({
    session, args: { action: "download", url: signed, name: "../../secret.txt" }
  }, policy);
  try {
    assert.equal(requests, 1);
    assert.equal(result.size, "first-second-third".length);
    assert.equal(path.basename(result.localPath).includes("secret.txt"), true);
    assert.equal(await fsp.readFile(result.localPath, "utf8"), "first-second-third");
  } finally {
    await fsp.unlink(result.localPath).catch(() => {});
  }
});

test("signed download rejects oversized response and does not retain partial artifact", async () => {
  const existing = new Set((await fsp.readdir(os.tmpdir()))
    .filter(x => x.startsWith("mezhs-artifact-")));
  const session = { async fetch() {
    const body = new ReadableStream({ start(controller) {
      controller.enqueue(new Uint8Array(100 * 1024 * 1024 + 1));
      controller.close();
    } });
    return new Response(body, { status: 200 });
  } };
  await assert.rejects(executeBinaryTransfer({
    session, args: { action: "download", url: signed, name: "oversized.dat" }
  }, policy), /exceeds 100 MiB/);
  const after = (await fsp.readdir(os.tmpdir()))
    .filter(x => x.startsWith("mezhs-artifact-") && !existing.has(x));
  assert.deepEqual(after, []);
});

test("signed download rejects redirects without attempting new destination", async () => {
  let calls = 0;
  const session = { async fetch(_, options) {
    calls++;
    assert.equal(options.redirect, "error");
    return new Response(null, { status: 302,
      headers: { Location: "https://external.example/private" } });
  } };
  await assert.rejects(executeBinaryTransfer({
    session, args: { action: "download", url: signed, name: "redirect.dat" }
  }, policy), /HTTP 302/);
  assert.equal(calls, 1);
});

test("signed upload file size is bounded before sending", async () => {
  const dir = await fsp.mkdtemp(path.join(os.tmpdir(), "mezhs-transfer-limit-"));
  const filename = path.join(dir, "large.bin");
  try {
    await fsp.writeFile(filename, "");
    await fsp.truncate(filename, 100 * 1024 * 1024 + 1);
    let calls = 0;
    await assert.rejects(executeBinaryTransfer({
      session: { fetch: async () => { calls++; throw Error("not allowed"); } },
      args: { action: "upload", url: signed, localPath: filename,
        contentType: "application/octet-stream" }
    }, policy), /exceeds 100 MiB/);
    assert.equal(calls, 0);
  } finally { await fsp.rm(dir, { recursive: true, force: true }); }
});
