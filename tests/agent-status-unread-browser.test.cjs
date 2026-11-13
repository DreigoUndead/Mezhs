const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawnSync } = require("node:child_process");
const { createRequire } = require("node:module");
const { test } = require("node:test");

const frontend = path.resolve(__dirname, "../src/Mezhs.Agent.Web");
const requireFrontend = createRequire(path.join(frontend, "package.json"));
const esbuild = requireFrontend("esbuild");

const browserSource = String.raw`
import React from "react";
import { createRoot } from "react-dom/client";
import App from "./src/App";
import "@mezhs/web-lib/styles.css";
import "./src/agent.css";

const date = "2026-11-13T14:00:00Z";
const chat = (id, title, executionStatus, executionStatusId) => ({
  chatId: id, policyId: "high", originSource: "Manual", paused: false,
  title, connectionId: "mock", createdAt: date, updatedAt: date,
  categoryId: null, executionStatus, executionStatusId,
});
window.fixtureChats = [
  chat("chat-a", "Agent A", "Completed", "execution-a1"),
  chat("chat-b", "Agent B", "Completed", "execution-b1"),
];
window.updateFixture = (id, status, executionId) => {
  const target = window.fixtureChats.find(value => value.chatId === id);
  target.executionStatus = status;
  target.executionStatusId = executionId;
};
const response = data => new Response(JSON.stringify(data), {
  status: 200, headers: { "Content-Type": "application/json" },
});
window.fetch = async input => {
  const url = new URL(String(input), location.href);
  const pathname = url.pathname;
  if (pathname === "/health") return response({ status: "ok" });
  if (pathname === "/v1/policies") return response([{
    id: "high", name: "High", connectionId: "mock", defaultModel: null,
    modelInstructions: "", snapshot: "",
  }]);
  if (pathname === "/v1/connections") return response([{
    id: "mock", name: "Mock", integration: "mock", requiresLogin: false,
    supportsModels: false, defaultModel: null,
    capabilities: { fileInput: false, imageInput: false, fileOutput: false, imageOutput: false },
  }]);
  if (pathname === "/v1/categories") return response([]);
  if (pathname === "/v1/agent-chats") return response(window.fixtureChats);
  if (/^\/v1\/agent-chats\/[^/]+\/messages$/.test(pathname)) return response([]);
  if (/^\/v1\/agent-chats\/[^/]+\/executions$/.test(pathname)) return response([]);
  if (/^\/v1\/agent-chats\/[^/]+\/runtime$/.test(pathname))
    return response({ messageCount: 0, executions: [] });
  throw new Error("Unexpected test request: " + pathname);
};
createRoot(document.getElementById("root")).render(<App />);

const sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
async function until(check, description, maxMs = 11000) {
  const deadline = Date.now() + maxMs;
  while (Date.now() < deadline) {
    if (check()) return;
    await sleep(100);
  }
  throw new Error("Timed out: " + description);
}
const requireCondition = (value, description) => { if (!value) throw new Error(description); };
const row = id => [...document.querySelectorAll(".agent-chat-entry")]
  .find(value => value.querySelector(".agent-chat-row")?.textContent.includes(id === "chat-a" ? "Agent A" : "Agent B"));
const dot = id => row(id)?.querySelector(".agent-status");
const unread = id => !!dot(id)?.classList.contains("agent-status-unread");
const open = id => row(id).querySelector(".agent-chat-row").click();

const checked = [];
async function run() {
  try {
    await until(() => dot("chat-a") && dot("chat-b"), "agent list");
    await until(() => !unread("chat-a"), "initially selected chat is read");
    requireCondition(unread("chat-b"), "Unopened completed Agent B must be blue/unread");
    requireCondition(dot("chat-b").getAttribute("aria-label").includes("unread"), "Unread state inaccessible");
    requireCondition(dot("chat-b").getAttribute("title").includes("unread"), "Unread tooltip missing");
    requireCondition(dot("chat-b").querySelector(".agent-status-label").textContent.includes("unread"), "Hover label missing");
    requireCondition(getComputedStyle(dot("chat-b").querySelector(".agent-status-dot")).backgroundColor === "rgb(57, 134, 244)", "Unread dot is not blue");
    await sleep(350);
    requireCondition(parseFloat(getComputedStyle(dot("chat-b").querySelector(".agent-status-label")).maxWidth) === 0, "Status label is always visible");
    const headerStatus = document.querySelector(".agent-header-meta .agent-status");
    requireCondition(headerStatus && headerStatus.tabIndex === 0, "Header status is not keyboard focusable");
    headerStatus.focus();
    await sleep(350);
    requireCondition(parseFloat(getComputedStyle(headerStatus.querySelector(".agent-status-label")).maxWidth) > 0, "Status text does not reveal on keyboard focus");
    checked.push("compact-hover-focus");

    open("chat-b");
    await until(() => !unread("chat-b"), "opening completed B marks read");
    requireCondition(getComputedStyle(dot("chat-b").querySelector(".agent-status-dot")).backgroundColor === "rgb(65, 163, 106)", "Read completion did not become green");
    requireCondition(JSON.parse(localStorage.getItem("mezhs.agent.read-executions.v1"))["chat-b"] === "execution-b1", "Read marker was not persisted");
    checked.push("blue-to-green-on-open");

    window.updateFixture("chat-b", "Completed", "execution-b2");
    await until(() => JSON.parse(localStorage.getItem("mezhs.agent.read-executions.v1"))["chat-b"] === "execution-b2", "selected new completion auto-read", 12500);
    requireCondition(!unread("chat-b"), "Selected chat completion became unread");
    checked.push("selected-auto-read");

    open("chat-a");
    window.updateFixture("chat-b", "Completed", "execution-b3");
    await until(() => unread("chat-b"), "unselected latest completion goes unread", 12500);
    requireCondition(JSON.parse(localStorage.getItem("mezhs.agent.read-executions.v1"))["chat-b"] === "execution-b2", "Unread latest completion was silently read");
    open("chat-b");
    await until(() => !unread("chat-b"), "opening later completion clears unread");
    checked.push("new-unselected-execution");

    const result = document.createElement("pre");
    result.id = "agent-status-browser-results";
    result.dataset.status = "pass";
    result.textContent = checked.join(", ");
    document.body.appendChild(result);
  } catch (error) {
    const result = document.createElement("pre");
    result.id = "agent-status-browser-results";
    result.dataset.status = "fail";
    result.textContent = String(error?.stack || error);
    document.body.appendChild(result);
  }
}
setTimeout(run, 100);
`;

const chromeCandidates = [
  process.env.CHROME_BIN,
  process.env.PROGRAMFILES && path.join(process.env.PROGRAMFILES, "Google", "Chrome", "Application", "chrome.exe"),
  process.env["PROGRAMFILES(X86)"] && path.join(process.env["PROGRAMFILES(X86)"], "Google", "Chrome", "Application", "chrome.exe"),
  process.env.PROGRAMFILES && path.join(process.env.PROGRAMFILES, "Microsoft", "Edge", "Application", "msedge.exe"),
  process.env["PROGRAMFILES(X86)"] && path.join(process.env["PROGRAMFILES(X86)"], "Microsoft", "Edge", "Application", "msedge.exe"),
  "/usr/bin/chromium", "/usr/bin/chromium-browser", "/usr/bin/google-chrome",
];
const chrome = chromeCandidates.find(candidate => candidate && fs.existsSync(candidate));

test("Agent status: compact label, unread completion and read-on-open in real Chromium", { timeout: 70000 }, () => {
  assert.ok(chrome, "Real Chromium is required for Agent status UI regression");
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), "mezhs-agent-status-"));
  try {
    const build = esbuild.buildSync({
      stdin: { contents: browserSource, resolveDir: frontend, sourcefile: "agent-status-browser-harness.tsx", loader: "tsx" },
      bundle: true, format: "iife", platform: "browser", jsx: "automatic", preserveSymlinks: true,
      write: false, outdir: folder,
    });
    for (const asset of build.outputFiles)
      fs.writeFileSync(path.join(folder, path.basename(asset.path)), asset.contents);
    const script = build.outputFiles.find(asset => asset.path.endsWith(".js"));
    const css = build.outputFiles.find(asset => asset.path.endsWith(".css"));
    assert.ok(script, "Browser script missing");
    const html = '<!doctype html><html><head>' +
      (css ? '<link rel="stylesheet" href="' + path.basename(css.path) + '">' : "") +
      '</head><body><div id="root"></div><script src="' + path.basename(script.path) + '"></script></body></html>';
    fs.writeFileSync(path.join(folder, "index.html"), html);
    const result = spawnSync(chrome, [
      "--headless=new", "--no-sandbox", "--disable-gpu", "--disable-background-networking",
      "--no-first-run", "--user-data-dir=" + path.join(folder, "profile"),
      "--virtual-time-budget=32000", "--dump-dom",
      "file:///" + path.join(folder, "index.html").replace(/\\/g, "/"),
    ], { encoding: "utf8", timeout: 60000, maxBuffer: 12 * 1024 * 1024 });
    assert.equal(result.status, 0, String(result.error || result.stderr));
    const report = /<pre id="agent-status-browser-results" data-status="([^"]+)">([^<]*)<\/pre>/.exec(result.stdout);
    assert.ok(report, "Browser did not report: " + result.stdout.slice(-1500) + " " + result.stderr);
    assert.equal(report[1], "pass", report[2]);
    for (const expected of ["compact-hover-focus", "blue-to-green-on-open", "selected-auto-read", "new-unselected-execution"])
      assert.ok(report[2].includes(expected), "Missing scenario " + expected);
  } finally {
    fs.rmSync(folder, { recursive: true, force: true });
  }
});
