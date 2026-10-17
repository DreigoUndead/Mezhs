"use strict";
// Browser regression: built Agent Web UI + isolated in-process fake HTTP API.
// Requires: npm install in src/Mezhs.Agent.Web, then npm run build.
const assert = require("node:assert/strict");
const http = require("node:http");
const fs = require("node:fs");
const path = require("node:path");
const { chromium } = require("../src/Mezhs.Agent.Web/node_modules/playwright-core");

const dist = path.resolve(__dirname, "../src/Mezhs.Agent.Web/dist");
const browserExecutables = [
  process.env.MEZHS_BROWSER_EXECUTABLE,
  "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  "C:/Program Files/Microsoft/Edge/Application/msedge.exe",
  "C:/Program Files/Google/Chrome/Application/chrome.exe",
].filter(Boolean);
const png = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO1tE90AAAAASUVORK5CYII=", "base64");
const now = "2026-10-17T12:00:00Z";
const caps = (fileInput, imageInput) => ({ fileInput, imageInput, fileOutput: true, imageOutput: true });
const connections = [
  { id: "good", name: "Files + images", integration: "mock", requiresLogin: false, supportsModels: false, defaultModel: "", capabilities: caps(true, true) },
  { id: "text-only", name: "Documents only", integration: "mock", requiresLogin: false, supportsModels: false, defaultModel: "", capabilities: caps(true, false) },
  { id: "no-files", name: "No attachments", integration: "mock", requiresLogin: false, supportsModels: false, defaultModel: "", capabilities: caps(false, false) },
];
const policy = { id: "high", name: "High", connectionId: "good", defaultModel: null, modelInstructions: "Test policy", snapshot: "Test policy" };
const existing = { chatId: "chat-old", policyId: "high", originSource: "Manual", paused: false, title: "Old chat", connectionId: "good", createdAt: now, updatedAt: now };
const fresh = { ...existing, chatId: "chat-new", title: "New chat" };
const userMessage = { messageId: "msg-old", chatId: "chat-old", connectionId: "good", role: "user", origin: "human", content: "Previously attached", displayContent: "Previously attached", commands: [], completionClaimed: false, fileIds: ["file-existing-doc", "file-existing-image"], status: "Completed", createdAt: now };
const files = new Map([
  ["file-existing-doc", { name: "prior-document.txt", contentType: "text/plain", size: 13 }],
  ["file-existing-image", { name: "prior-image.png", contentType: "image/png", size: png.length }],
]);
const uploads = [];
const held = [];
const executionRequests = [];
let holdUploads = false;
let newChat = false;
const metadata = (id, connectionId = "good") => ({ fileId: id, connectionId, ...files.get(id), source: "User", createdAt: now, contentUrl: `/v1/files/${id}/content`, downloadUrl: `/v1/files/${id}/content?download=true` });
function respond(res, code, data) { res.writeHead(code, { "Content-Type": "application/json" }); res.end(JSON.stringify(data)); }
async function handler(req, res) {
  const pathname = new URL(req.url, "http://127.0.0.1").pathname;
  if (pathname.startsWith("/v1/") || pathname === "/health") {
    if (pathname === "/health") return respond(res, 200, { status: "ok" });
    if (pathname === "/v1/policies") return respond(res, 200, [policy]);
    if (pathname === "/v1/connections") return respond(res, 200, connections);
    if (pathname === "/v1/agent-chats") return respond(res, 200, newChat ? [fresh, existing] : [existing]);
    if (pathname === "/v1/agent-chats/chat-old/messages") return respond(res, 200, [userMessage]);
    if (pathname === "/v1/agent-chats/chat-new/messages") return respond(res, 200, []);
    if (/^\/v1\/agent-chats\/[^/]+\/executions$/.test(pathname)) return respond(res, 200, []);
    if (/^\/v1\/agent-chats\/[^/]+\/runtime$/.test(pathname)) return respond(res, 200, { messageCount: pathname.includes("chat-old") ? 1 : 0, latestMessageId: pathname.includes("chat-old") ? "msg-old" : null, latestMessageStatus: pathname.includes("chat-old") ? "Completed" : null, executions: [] });
    if (pathname === "/v1/connections/good/models" || pathname.endsWith("/models")) return respond(res, 200, []);
    const fileMatch = pathname.match(/^\/v1\/files\/([^/]+)(?:\/(content))?$/);
    if (fileMatch && files.has(fileMatch[1])) {
      if (fileMatch[2]) { res.writeHead(200, { "Content-Type": files.get(fileMatch[1]).contentType }); return res.end(files.get(fileMatch[1]).contentType === "image/png" ? png : "document data"); }
      return respond(res, 200, metadata(fileMatch[1]));
    }
    if (pathname === "/v1/files" && req.method === "POST") {
      const chunks = []; for await (const chunk of req) chunks.push(chunk);
      const multipart = Buffer.concat(chunks).toString("latin1");
      const name = /filename="([^"]+)"/.exec(multipart)?.[1];
      const connectionId = /name="connectionId"\r\n\r\n([^\r\n]+)/.exec(multipart)?.[1];
      assert(name && connectionId, "Browser must post multipart file and connectionId");
      uploads.push({ name, connectionId });
      const send = (code = 201) => {
        if (code !== 201) return respond(res, code, { error: "Rejected by attachment regression" });
        const id = `file-upload-${uploads.length}-${name.replace(/\W/g, "-")}`;
        files.set(id, { name, contentType: name.endsWith(".png") ? "image/png" : "text/plain", size: 9 });
        respond(res, code, metadata(id, connectionId));
      };
      if (holdUploads) held.push({ name, send }); else send();
      return;
    }
    if (pathname === "/v1/executions" && req.method === "POST") {
      const chunks = []; for await (const chunk of req) chunks.push(chunk);
      const submitted = JSON.parse(Buffer.concat(chunks).toString("utf8"));
      executionRequests.push(submitted);
      if (!submitted.chatId) newChat = true;
      return respond(res, 201, { executionId: "exec-new", chatId: submitted.chatId || "chat-new", kind: "Agent", status: "Completed", isTerminal: true, connectionId: "good", policyId: "high" });
    }
    if (pathname === "/v1/executions/exec-new") return respond(res, 200, { executionId: "exec-new", chatId: "chat-new", status: "Completed", isTerminal: true });
    return respond(res, 404, { error: `Unexpected API path: ${pathname}` });
  }
  const relative = pathname === "/" ? "index.html" : pathname.slice(1);
  const target = path.resolve(dist, relative);
  if (!target.startsWith(dist + path.sep) && target !== path.join(dist, "index.html")) { res.writeHead(403); return res.end(); }
  try {
    const body = fs.readFileSync(target);
    const ext = path.extname(target);
    res.writeHead(200, { "Content-Type": ext === ".js" ? "text/javascript" : ext === ".css" ? "text/css" : "text/html" });
    res.end(body);
  } catch { res.writeHead(404); res.end(); }
}
async function until(condition, label, timeout = 10000) {
  const end = Date.now() + timeout;
  while (!condition()) { if (Date.now() > end) throw new Error(`Timed out waiting for ${label}`); await new Promise(r => setTimeout(r, 40)); }
}
(async () => {
  assert(fs.existsSync(path.join(dist, "index.html")), "Build Agent Web before the browser test");
  const executablePath = browserExecutables.find(candidate => fs.existsSync(candidate));
  assert(executablePath, "Install Microsoft Edge / Chrome, or set MEZHS_BROWSER_EXECUTABLE to its path");
  const server = http.createServer((req, res) => { void handler(req, res).catch(error => { console.error(error); if (!res.headersSent) respond(res, 500, { error: error.message }); }); });
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  const port = server.address().port;
  let browser;
  try {
    browser = await chromium.launch({ executablePath, headless: true });
    const page = await browser.newPage();
    const errors = []; page.on("pageerror", error => errors.push(error.message));
    await page.goto(`http://127.0.0.1:${port}/`);
    await page.locator(".message-files .message-file").getByText("prior-document.txt").waitFor();
    await page.locator(".message-files .message-image img[alt='prior-image.png']").waitFor();
    assert.equal(await page.locator(".message-files .message-file").count(), 1);
    assert.equal(await page.locator(".message-files .message-image").count(), 1);
    console.log("PASS: existing chat restores document and image metadata and renders links/previews");

    await page.locator("textarea").fill("Follow up with attachment");
    await page.locator('input[aria-label="Select attachments"]').setInputFiles({ name: "existing-turn.txt", mimeType: "text/plain", buffer: Buffer.from("existing") });
    await page.locator(".pending-file").getByText("Uploaded").waitFor();
    await page.getByRole("button", { name: "Send message" }).click();
    await until(() => executionRequests.length === 1, "existing-chat execution request");
    assert.equal(executionRequests[0].chatId, "chat-old");
    assert.deepEqual(executionRequests[0].fileIds, ["file-upload-1-existing-turn-txt"]);
    assert.equal(await page.locator(".pending-file").count(), 0);
    console.log("PASS: existing-chat follow-up forwards exactly its uploaded fileId and clears pending files");

    await page.locator("button.new-chat").click();
    await page.locator("textarea").fill("Process both attachments");
    holdUploads = true;
    await page.locator('input[aria-label="Select attachments"]').setInputFiles([
      { name: "selected-photo.png", mimeType: "image/png", buffer: png },
      { name: "selected-notes.txt", mimeType: "text/plain", buffer: Buffer.from("test") },
    ]);
    await page.waitForFunction(() => document.querySelectorAll(".pending-file").length === 2);
    assert.equal(await page.getByRole("button", { name: "Send message" }).isDisabled(), true);
    await until(() => held.length === 1, "first upload");
    held.shift().send();
    await until(() => held.length === 1, "second upload");
    assert.equal(await page.getByRole("button", { name: "Send message" }).isDisabled(), true, "Send remains blocked while second upload is pending");
    held.shift().send();
    await page.getByRole("button", { name: "Send message" }).waitFor({ state: "visible" });
    await page.waitForFunction(() => !document.querySelector('button[aria-label="Send message"]').disabled);
    await page.getByRole("button", { name: "Send message" }).click();
    await until(() => executionRequests.length === 2, "new-chat execution request");
    assert.equal(executionRequests[1].policyId, "high");
    assert.equal(executionRequests[1].connectionId, "good");
    assert.equal(executionRequests[1].chatId, undefined);
    assert.deepEqual(executionRequests[1].fileIds, ["file-upload-2-selected-photo-png", "file-upload-3-selected-notes-txt"]);
    assert.deepEqual(uploads.slice(1, 3).map(x => x.name), ["selected-photo.png", "selected-notes.txt"]);
    assert.equal(await page.locator(".pending-file").count(), 0);
    console.log("PASS: multi-file selection blocks submission until ALL uploads finish and sends both fileIds once");

    await page.locator("button.new-chat").click();
    await page.locator("textarea").fill("Failure handling");
    await page.locator('input[aria-label="Select attachments"]').setInputFiles({ name: "bad.txt", mimeType: "text/plain", buffer: Buffer.from("bad") });
    await until(() => held.length === 1, "failed upload");
    held.shift().send(400);
    await page.getByText("Rejected by attachment regression").waitFor();
    assert.equal(await page.getByRole("button", { name: "Send message" }).isDisabled(), true);
    await page.getByRole("button", { name: "Remove bad.txt" }).click();
    assert.equal(await page.locator(".pending-file").count(), 0);
    console.log("PASS: upload failures block submission and the user can remove a failed attachment");

    await page.locator('input[aria-label="Select attachments"]').setInputFiles({ name: "clear-on-switch.txt", mimeType: "text/plain", buffer: Buffer.from("switch") });
    await until(() => held.length === 1, "upload to abandon");
    await page.locator(".connection-model-picker select").first().selectOption("no-files");
    assert.equal(await page.locator(".pending-file").count(), 0);
    assert.equal(await page.getByRole("button", { name: "Attach files" }).isDisabled(), true);
    held.shift().send();
    await page.waitForTimeout(200);
    assert.equal(await page.locator(".pending-file").count(), 0, "Late response must not restore abandoned upload");
    console.log("PASS: connection switch clears old uploads and file-incapable integrations disable the picker");

    await page.locator(".connection-model-picker select").first().selectOption("text-only");
    await page.locator('input[aria-label="Select attachments"]').setInputFiles({ name: "not-allowed.png", mimeType: "image/png", buffer: png });
    await page.getByText("This connection does not support image input.").waitFor();
    assert.equal(await page.getByRole("button", { name: "Send message" }).isDisabled(), true);
    console.log("PASS: image capability is enforced before upload");
    assert.equal(executionRequests.length, 2, "No unexpected execution from failed/abandoned attachments");
    assert.deepEqual(errors, [], `Browser page errors: ${errors.join("; ")}`);
    console.log("PASS: browser attachment regression completed without runtime errors");
  } finally {
    if (browser) await browser.close();
    await new Promise(resolve => server.close(resolve));
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
