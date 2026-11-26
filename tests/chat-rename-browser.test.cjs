const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawnSync } = require("node:child_process");
const { createRequire } = require("node:module");
const { test } = require("node:test");

const frontend = path.resolve(__dirname, "../src/Mezhs.Agent.Web");
const esbuild = createRequire(path.join(frontend, "package.json"))("esbuild");
const appSource = String.raw`
import React from "react";
import { createRoot } from "react-dom/client";
import MezhsChatApp from "./node_modules/@mezhs/web-lib/src/MezhsChatApp";
import "./node_modules/@mezhs/web-lib/src/styles.css";

const now = "2026-11-10T10:00:00Z";
const makeChat = (id, title) => ({
  chatId: id, title, categoryId: "group-a", connectionId: "no-provider",
  createdAt: now, updatedAt: now,
});
let categories = [{ categoryId: "group-a", name: "Alpha", color: "#abcdef", createdAt: now }];
let chats = [makeChat("chat-a", "First chat"), makeChat("chat-b", "Second chat")];
const json = value => Promise.resolve(new Response(JSON.stringify(value), {
  status: 200, headers: { "Content-Type": "application/json" },
}));
window.fetch = (url, init = {}) => {
  const endpoint = new URL(String(url), location.href).pathname;
  const method = String(init?.method || "GET").toUpperCase();
  if (endpoint === "/health") return json({ status: "ok" });
  if (endpoint === "/v1/connections" && method === "GET") return json([]);
  if (endpoint === "/v1/categories" && method === "GET") return json(categories);
  if (endpoint === "/v1/chats" && method === "GET") return json(chats);
  if (endpoint === "/v1/chats/chat-b" && method === "PATCH") {
    chats[1].title = JSON.parse(init.body).title;
    return json(chats[1]);
  }
  throw new Error("Unexpected request: " + method + " " + endpoint);
};

createRoot(document.getElementById("root")).render(<MezhsChatApp apiBaseUrl="http://test-api" />);

const sleep = duration => new Promise(resolve => setTimeout(resolve, duration));
async function settle(duration = 60) { await sleep(duration); }
function check(value, message) { if (!value) throw Error(message); }
const waitFor = async (predicate, label) => {
  for (let i = 0; i < 50; i++) {
    if (predicate()) return;
    await settle(30);
  }
  throw Error("Timed out waiting for " + label);
};
const report = (status, message) => {
  const node = document.createElement("pre");
  node.id = "browser-results";
  node.setAttribute("data-state", status);
  node.textContent = message;
  document.body.appendChild(node);
};
async function run() {
  const steps = [];
  try {
    await waitFor(() => document.querySelectorAll(".chat-row").length === 2, "initial chat inventory");
    document.querySelectorAll(".chat-options")[1].click();
    await settle();
    const rename = document.querySelector(".base-chat-menu-title");
    rename.focus(); rename.select(); document.execCommand("insertText", false, "a".repeat(199) + "z");
    rename.setSelectionRange(0, 0); document.execCommand("insertText", false, "X");
    check(rename.value === "X" + "a".repeat(199) && rename.selectionStart === 1, "Prefix insertion or caret failed");
    rename.setSelectionRange(2, 2); document.execCommand("insertText", false, "Y");
    const expected = "XaY" + "a".repeat(197);
    check(rename.value === expected && rename.selectionStart === 3, "Middle insertion or caret failed");
    rename.setSelectionRange(200, 200); document.execCommand("insertText", false, "Z");
    check(rename.value === expected && rename.selectionStart === 200, "End insertion was not discarded");
    rename.blur(); await waitFor(() => chats[1].title === expected, "rename saved");
    await settle();
    check(document.querySelectorAll(".chat-title")[1].textContent === expected, "Renamed chat changed position");
    document.querySelectorAll(".chat-options")[1].click();
    steps.push("rename-tail-truncation");
    report("pass", steps.join(", "));
  } catch (error) {
    report("fail", String(error?.stack || error));
  }
}
setTimeout(run, 80);
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

test("Chromium chat rename: tail truncation, caret and list position", { timeout: 70000 }, () => {
  assert.ok(chrome, "A real Chromium browser is required");
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "mezhs-chat-rename-"));
  try {
    const output = esbuild.buildSync({
      stdin: { contents: appSource, resolveDir: frontend, sourcefile: "chat-rename-harness.tsx", loader: "tsx" },
      bundle: true, format: "iife", platform: "browser", jsx: "automatic",
      preserveSymlinks: true, write: false, outdir: temp,
    }).outputFiles;
    for (const asset of output) fs.writeFileSync(path.join(temp, path.basename(asset.path)), asset.contents);
    const script = output.find(asset => asset.path.endsWith(".js"));
    const stylesheet = output.find(asset => asset.path.endsWith(".css"));
    assert.ok(script, "Browser JS was not built");
    fs.writeFileSync(path.join(temp, "index.html"),
      "<!doctype html><html><head>" +
      (stylesheet ? '<link rel="stylesheet" href="' + path.basename(stylesheet.path) + '">' : "") +
      '</head><body><div id="root"></div><script src="' +
      path.basename(script.path) + '"></script></body></html>');
    const invocation = spawnSync(chrome, [
      "--headless=new", "--no-sandbox", "--disable-gpu", "--disable-background-networking",
      "--no-first-run", "--user-data-dir=" + path.join(temp, "profile"),
      "--virtual-time-budget=15000", "--dump-dom",
      "file:///" + path.join(temp, "index.html").replace(/\\/g, "/"),
    ], { timeout: 60000, encoding: "utf8", maxBuffer: 12 * 1024 * 1024 });
    assert.equal(invocation.status, 0, "Chrome exit: " + (invocation.error || invocation.stderr));
    const report = /<pre id="browser-results" data-state="([^"]+)">([^<]*)<\/pre>/.exec(invocation.stdout);
    assert.ok(report, "Browser harness did not finish: " + invocation.stdout.slice(-2000));
    assert.equal(report[1], "pass", report[2]);
    assert.ok(report[2].includes("rename-tail-truncation"));
  } finally {
    fs.rmSync(temp, { recursive: true, force: true });
  }
});
