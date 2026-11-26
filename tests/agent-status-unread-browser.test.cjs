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
window.fixtureMessages = {};
window.fixtureExecutions = {};
// A nontrivial history makes transcript re-rendering during typing observable.
window.fixtureMessages["chat-b"] = Array.from({ length: 45 }, (_, index) => ({
  messageId: "history-" + index, chatId: "chat-b", connectionId: "mock",
  role: "assistant", origin: "model", content: "History entry " + index + " " + "x".repeat(700),
  displayContent: "History entry " + index, commands: [], fileIds: [],
  completionClaimed: false, status: "Completed", createdAt: date,
}));
window.updateFixture = (id, status, executionId, waitReason = null, maxParallel = null) => {
  const target = window.fixtureChats.find(value => value.chatId === id);
  target.executionStatus = status;
  target.executionStatusId = executionId;
  target.admissionWaitReason = waitReason;
  target.admissionMaxParallel = maxParallel;
};
window.installActiveTurn = id => {
  window.fixtureMessages[id] = [{
    messageId: "message-b4", chatId: id, connectionId: "mock", role: "user",
    origin: "command-result", content: "", displayContent: "", commands: [],
    fileIds: [], completionClaimed: false, status: "Running",
    activity: "waiting", activityDetail: "Prompt accepted; waiting for model activity.",
    createdAt: date, startedAt: date,
  }];
  window.fixtureExecutions[id] = [{
    executionId: "execution-b4", kind: "Agent", status: "Running", isTerminal: false,
    chatId: id, policyId: "high", connectionId: "mock", source: "Manual",
    correlationId: "execution-b4", request: "test", policySnapshot: "",
    createdAt: date, startedAt: date,
  }];
};
const response = data => new Response(JSON.stringify(data), {
  status: 200, headers: { "Content-Type": "application/json" },
});
window.fetch = async (input, init = {}) => {
  const requestUrl = String(input);
  // App API uses root-relative paths; file:// pages resolve "/health" to "/C:/health".
  const pathname = requestUrl.startsWith("/")
    ? requestUrl.split("?")[0]
    : new URL(requestUrl, location.href).pathname;
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
  if (pathname === "/v1/chats/chat-b" && init.method === "PATCH") {
    const target = window.fixtureChats.find(item => item.chatId === "chat-b");
    target.title = JSON.parse(init.body).title;
    return response(target);
  }
  const match = /^\/v1\/agent-chats\/([^/]+)\/(messages|executions|runtime)$/.exec(pathname);
  if (match) {
    const id = decodeURIComponent(match[1]);
    const messages = window.fixtureMessages[id] || [];
    const executions = window.fixtureExecutions[id] || [];
    if (match[2] === "messages") return response(messages);
    if (match[2] === "executions") return response(executions);
    const activeMessage = messages.find(message => message.status === "Running");
    return response({
      messageCount: messages.length,
      latestMessageId: messages.at(-1)?.messageId,
      latestMessageStatus: messages.at(-1)?.status,
      activeMessage,
      executions: executions.map(({ executionId, kind, status, isTerminal, completedAt }) =>
        ({ executionId, kind, status, isTerminal, completedAt })),
    });
  }
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

    // Instrument the actual shared transcript component, not a test-only stand-in.
    // Draft-only updates must not invoke it for every typed character.
    await until(() => document.querySelectorAll("article.message").length === 45, "long transcript");
    const editor = document.querySelector('textarea[aria-label="Message"]');
    requireCondition(editor && !editor.disabled, "Agent composer is not editable");
    const firstRenderCount = window.__transcriptRenderCount || 0;
    const firstAppRenderCount = window.__agentAppRenderCount || 0;
    requireCondition(firstRenderCount > 0 && firstAppRenderCount > 0, "Render probes did not mount");
    for (const value of ["a", "ab", "abc", "abcd", "abcde", "abcdef", "abcdefg", "abcdefgh"]) {
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value").set.call(editor, value);
      editor.dispatchEvent(new Event("input", { bubbles: true }));
      await sleep(45);
    }
    requireCondition(editor.value === "abcdefgh", "Composer dropped typed characters");
    requireCondition(window.__agentAppRenderCount === firstAppRenderCount,
      "Typing re-rendered the Agent app " + (window.__agentAppRenderCount - firstAppRenderCount) + " times");
    checked.push("draft-does-not-render-app");
    const draftRenderCount = window.__transcriptRenderCount;
    requireCondition(draftRenderCount - firstRenderCount <= 2,
      "Draft updates re-rendered the full transcript " + (draftRenderCount - firstRenderCount) + " times");
    checked.push("draft-skips-long-transcript");

    // Normal five-second chat-list polling must not redraw unchanged history.
    await sleep(5700);
    requireCondition(window.__transcriptRenderCount === draftRenderCount,
      "Unchanged chat-list polling re-rendered the transcript");
    checked.push("unchanged-chat-poll-skips-transcript");

    window.fixtureMessages["chat-b"].push({
      messageId: "history-refresh", chatId: "chat-b", connectionId: "mock",
      role: "assistant", origin: "model", content: "Transcript refresh marker",
      displayContent: "Transcript refresh marker", commands: [], fileIds: [],
      completionClaimed: false, status: "Completed", createdAt: date,
    });
    await until(() => [...document.querySelectorAll("article.message")]
      .some(element => element.textContent.includes("Transcript refresh marker")), "live transcript refresh");
    requireCondition(window.__transcriptRenderCount > draftRenderCount, "New messages did not invalidate transcript cache");
    checked.push("message-update-refreshes-transcript");

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
    // The API projects Waiting only for admission-blocked model turns.
    window.updateFixture("chat-b", "Waiting", "execution-b4");
    await until(() => dot("chat-b")?.classList.contains("agent-status-waiting"), "admission Waiting");
    requireCondition(dot("chat-b").getAttribute("aria-label") === "Agent status: Waiting", "Waiting aria status is missing");
    requireCondition(getComputedStyle(dot("chat-b").querySelector(".agent-status-dot")).backgroundColor === "rgb(221, 144, 46)", "Waiting dot is not orange");
    requireCondition(document.querySelector(".agent-header-meta .agent-status")?.classList.contains("agent-status-waiting"), "Header/sidebar Waiting status differs");
    requireCondition(!unread("chat-b"), "Waiting must not override completion-read semantics");
    window.updateFixture("chat-b", "Running", "execution-b4");
    await until(() => dot("chat-b")?.classList.contains("agent-status-running"), "admitted Running");
    requireCondition(document.querySelector(".agent-header-meta .agent-status")?.classList.contains("agent-status-running"), "Header/sidebar Running status differs");
    checked.push("waiting-orange-running-recovery");

    // Real throttling supersedes provider activity, but provider waiting is still Running.
    window.installActiveTurn("chat-b");
    open("chat-a");
    await until(() => document.querySelector(".agent-header h1")?.textContent === "Agent A", "switch away");
    window.updateFixture("chat-b", "Waiting", "execution-b4", "Capacity", 3);
    open("chat-b");
    await until(() => document.querySelector(".thinking span")?.textContent ===
      "Waiting for integration slot (3/3)…", "capacity status label", 15000);
    requireCondition(dot("chat-b")?.classList.contains("agent-status-waiting"),
      "Capacity blocked turn must have orange Waiting status");

    window.updateFixture("chat-b", "Waiting", "execution-b4", "Spacing");
    await until(() => document.querySelector(".thinking span")?.textContent ===
      "Waiting for integration start interval…", "spacing status label", 15000);
    requireCondition(dot("chat-b")?.classList.contains("agent-status-waiting"),
      "Start spacing must remain in Waiting");

    window.updateFixture("chat-b", "Running", "execution-b4");
    await until(() => document.querySelector(".thinking span")?.textContent ===
      "Prompt accepted; waiting for model activity.", "provider-only progress", 15000);
    requireCondition(dot("chat-b")?.classList.contains("agent-status-running"),
      "Provider waiting must remain Agent Running, not admission Waiting");
    checked.push("capacity-spacing-provider-separation");

    row("chat-b").querySelector(".agent-chat-menu-toggle").click();
    await sleep(100);
    const rename = document.querySelector(".agent-chat-menu-title");
    const full = "a".repeat(199) + "z";
    rename.focus(); rename.select(); document.execCommand("insertText", false, full);
    rename.setSelectionRange(0, 0); document.execCommand("insertText", false, "X");
    requireCondition(rename.value === "X" + "a".repeat(199) && rename.selectionStart === 1, "Prefix insertion or caret failed");
    rename.setSelectionRange(2, 2); document.execCommand("insertText", false, "Y");
    const expected = "XaY" + "a".repeat(197);
    requireCondition(rename.value === expected && rename.selectionStart === 3, "Middle insertion or caret failed");
    rename.setSelectionRange(200, 200); document.execCommand("insertText", false, "Z");
    requireCondition(rename.value === expected && rename.selectionStart === 200, "End insertion was not discarded");
    rename.blur();
    await until(() => window.fixtureChats[1].title === expected, "rename saved");
    checked.push("rename-tail-truncation");

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

test("Agent status: compact label, unread completion and read-on-open in real Chromium", { timeout: 110000 }, async () => {
  assert.ok(chrome, "Real Chromium is required for Agent status UI regression");
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), "mezhs-agent-status-"));
  try {
    const build = await esbuild.build({
      stdin: { contents: browserSource, resolveDir: frontend, sourcefile: "agent-status-browser-harness.tsx", loader: "tsx" },
      bundle: true, format: "iife", platform: "browser", jsx: "automatic", preserveSymlinks: true,
      write: false, outdir: folder,
      plugins: [{
        name: "count-real-transcript-renders",
        setup(build) {
          build.onLoad({ filter: /ChatSurface\.tsx$/ }, (args) => {
            const source = fs.readFileSync(args.path, "utf8");
            const needle = '  const apiBase = apiBaseUrl.replace(/\\/$/, "");';
            assert.ok(source.includes(needle), "Transcript instrumentation point missing");
            return {
              contents: source.replace(needle,
                '  (window as any).__transcriptRenderCount = ((window as any).__transcriptRenderCount || 0) + 1;\n' + needle),
              loader: "tsx",
            };
          });
          build.onLoad({ filter: /App\.tsx$/ }, (args) => {
            if (path.basename(args.path) !== "App.tsx") return;
            const source = fs.readFileSync(args.path, "utf8");
            const needle = "export default function App() {";
            assert.ok(source.includes(needle), "Agent app render instrumentation point missing");
            return {
              contents: source.replace(needle,
                needle + '\n  (window as any).__agentAppRenderCount = ((window as any).__agentAppRenderCount || 0) + 1;'),
              loader: "tsx",
            };
          });
        },
      }],
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
      // Test the completed hover/focus state without virtual-clock-dependent CSS transitions.
      // Normal-motion transitions remain in the application; reduced-motion is a user-supported mode.
      "--headless=new", "--force-prefers-reduced-motion", "--no-sandbox", "--disable-gpu", "--disable-background-networking",
      "--no-first-run", "--user-data-dir=" + path.join(folder, "profile"),
      "--virtual-time-budget=65000", "--dump-dom",
      "file:///" + path.join(folder, "index.html").replace(/\\/g, "/"),
    ], { encoding: "utf8", timeout: 100000, maxBuffer: 12 * 1024 * 1024 });
    assert.equal(result.status, 0, String(result.error || result.stderr));
    const report = /<pre id="agent-status-browser-results" data-status="([^"]+)">([^<]*)<\/pre>/.exec(result.stdout);
    assert.ok(report, "Browser did not report: " + result.stdout.slice(-1500) + " " + result.stderr);
    assert.equal(report[1], "pass", report[2]);
    for (const expected of ["compact-hover-focus", "blue-to-green-on-open", "selected-auto-read", "new-unselected-execution", "waiting-orange-running-recovery", "capacity-spacing-provider-separation", "draft-skips-long-transcript", "draft-does-not-render-app", "unchanged-chat-poll-skips-transcript", "message-update-refreshes-transcript", "rename-tail-truncation"])
      assert.ok(report[2].includes(expected), "Missing scenario " + expected);
  } finally {
    fs.rmSync(folder, { recursive: true, force: true });
  }
});
