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
const { canonicalDocument } = require("./helpers/canonical-rich-document.cjs");

// The real C# parser, not the browser, supplies completed-message rich UI.
const titleSource = 'Before <title size="xl" color="success" tabularNums>123.45</title> After';
const titleDocument = canonicalDocument(titleSource);
if (!titleDocument.valid) throw new Error("Canonical title fixture failed C# validation");

const appSource = String.raw`
import React, { useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { ConversationViewport } from "./node_modules/@mezhs/web-lib/src/ConversationViewport";
import { useConversationPending } from './node_modules/@mezhs/web-lib/src/useConversationPending';
import { useConversationDrafts, useConversationDraftStore, useConversationDraftValue } from "./node_modules/@mezhs/web-lib/src/useConversationDrafts";
import { MarkdownContent } from "./node_modules/@mezhs/web-lib/src/MarkdownContent";
import "./node_modules/@mezhs/web-lib/src/styles.css";

const titleSource = __MEZHS_TITLE_SOURCE__;
const titleDocument = __MEZHS_TITLE_DOCUMENT__;

const initial = (tag, count) => Array.from({ length: count }, (_, i) => ({
  messageId: tag + i,
  status: "Completed",
  content: tag + " entry " + i + " " + "x".repeat(100),
}));
function ScopedDraftInput({ store }) {
  const draft = useConversationDraftValue(store, "probe");
  return <textarea id="scoped-draft" value={draft} onChange={event => store.setDraft("probe", event.target.value)} />;
}

function ScopedDraftHarness() {
  const store = useConversationDraftStore();
  (window as any).__scopedDraftParentRenders = ((window as any).__scopedDraftParentRenders || 0) + 1;
  return <ScopedDraftInput store={store} />;
}

function App() {
  const [selected, setSelected] = useState("A");
  const [newSequence, setNewSequence] = useState(0);
  const [messages, setMessages] = useState({ A: initial("alpha", 45), B: initial("beta", 30) });
  const [tall, setTall] = useState(false);
  const [historyReady, setHistoryReady] = useState(true);
  const [footerRevisions, setFooterRevisions] = useState({ A: [{ state: 'queued' }], B: [] });
  const pendingRequest = useRef({ key: '', text: '' });
  const { draft, setDraft, clearDraft, moveDraft, restoreDraft } = useConversationDrafts(selected);
  const { pending, start, transfer, finish } = useConversationPending(selected);
  (window as any).draftTest = {
    pending,
    begin: () => { if (start(selected)) { pendingRequest.current = { key: selected, text: draft }; clearDraft(selected, draft); } },
    accept: () => { const key = pendingRequest.current.key; moveDraft(key, 'chat:migrated', ''); transfer(key, 'chat:migrated'); pendingRequest.current.key = 'chat:migrated'; setSelected('chat:migrated'); },
    fail: () => { restoreDraft(pendingRequest.current.key, pendingRequest.current.text); finish(pendingRequest.current.key); },
    done: () => finish(pendingRequest.current.key),
    grow: () => setTall(value => !value),
  };
  return (
    <div id="test-app" style={{ width: 550, height: 520, display: "flex", flexDirection: "column" }}>
      <div style={{ flexShrink: 0 }}>
        <button id="chat-a" onClick={() => setSelected("A")}>A</button>
        <button id="chat-b" onClick={() => setSelected("B")}>B</button>
        <button id="new-chat" onClick={() => { setNewSequence(n => n + 1); setSelected("new:" + (newSequence + 1)); }}>New</button>
        <button id="append-a" onClick={() => setMessages(current => ({
          ...current, A: [...current.A, { messageId: "more-" + current.A.length, status: "Completed", content: "A extra" }],
        }))}>Append</button>
        <button id="mutate-a" onClick={() => setMessages(current => ({
          ...current, A: current.A.map((item, index) => index === 0
            ? { ...item, content: item.content.replace("alpha", "bravo") } : item),
        }))}>Mutate equal length</button>
        <button id="open-a-partial" onClick={() => { setHistoryReady(false); setFooterRevisions(current => ({ ...current, A: [] })); setSelected("A"); }}>Open A while executions load</button>
        <button id="complete-a-history" onClick={() => { setFooterRevisions(current => ({ ...current, A: [{ state: "queued" }] })); setHistoryReady(true); }}>Finish A history</button>
        <button id="change-attachment-a" onClick={() => setMessages(current => ({ ...current, A: current.A.map((item, i) => i === 0 ? { ...item, files: [{ fileId: 'f-1', name: 'updated.png' }] } : item) }))}>Attachment</button>
        <button id="change-footer-a" onClick={() => setFooterRevisions(current => ({ ...current, A: [{ state: 'finished' }] }))}>Footer</button>
        <textarea id="draft" value={draft} onChange={e => setDraft(e.target.value)} />
      </div>
      <ConversationViewport conversationKey={selected} messages={messages[selected] || []} revisionItems={footerRevisions[selected] || []} historyReady={historyReady} empty={!(messages[selected]?.length)}>
        {(messages[selected] || []).map(m => (
          <div key={m.messageId} className="browser-message" style={{ height: tall ? 115 : 69, padding: 4, boxSizing: "border-box" }}>
            {m.content}
          </div>
        ))}
      </ConversationViewport>
      <div id="inline-title-test" style={{ flexShrink: 0 }}>
        <MarkdownContent content={titleSource} document={titleDocument} />
      </div>
      <div id="provisional-title-test" style={{ flexShrink: 0 }}>
        <MarkdownContent content={titleSource} />
      </div>
    </div>
  );
}

createRoot(document.getElementById("root")).render(<App />);
const scopedDraftRoot = document.createElement("div");
scopedDraftRoot.hidden = true;
document.body.appendChild(scopedDraftRoot);
createRoot(scopedDraftRoot).render(<ScopedDraftHarness />);

const sleep = duration => new Promise(resolve => setTimeout(resolve, duration));
async function settle(duration = 40) { await sleep(duration); }
function check(condition, message) { if (!condition) throw Error(message); }
function current() { return document.querySelector("section.conversation"); }
function near(value, expected, tolerance = 3) { return Math.abs(value - expected) <= tolerance; }
function atBottom() { const el = current(); return near(el.scrollTop, el.scrollHeight - el.clientHeight); }
function click(id) { document.getElementById(id).click(); }
async function typeText(value) {
  const el = document.getElementById("draft");
  Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value").set.call(el, value);
  el.dispatchEvent(new Event("input", { bubbles: true }));
  await settle();
}
const results = [];
async function run() {
  try {
    await settle(100);
    const scopedEditor = document.getElementById("scoped-draft");
    check(scopedEditor && window.__scopedDraftParentRenders > 0, "Scoped draft fixture did not mount");
    const scopedRenders = window.__scopedDraftParentRenders;
    for (const value of ["one", "one two", "one two three"]) {
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value").set.call(scopedEditor, value);
      scopedEditor.dispatchEvent(new Event("input", { bubbles: true }));
      await settle();
    }
    check(scopedEditor.value === "one two three", "Scoped draft lost input");
    check(window.__scopedDraftParentRenders === scopedRenders, "Scoped draft changes re-rendered the parent");
    results.push("scoped-draft-isolation");
    check(current().scrollHeight > current().clientHeight + 1000, "A is not a long scrollable conversation");
    check(atBottom(), "initial A conversation did not open at bottom");
    results.push("initial-bottom");
    await typeText("draft for A");
    click("chat-b"); await settle();
    check(atBottom(), "B did not open at bottom");
    check(document.getElementById("draft").value === "", "A draft leaked into B");
    await typeText("draft for B");
    click("chat-a"); await settle();
    check(document.getElementById("draft").value === "draft for A", "A draft was not restored");
    results.push("draft-isolation");
    const el = current();
    el.scrollTop = 240;
    el.dispatchEvent(new Event("scroll", { bubbles: false })); await settle();
    check(document.querySelector('button[aria-label="Scroll to bottom"]'), "missing scroll-to-bottom control when scrolled up");
    const savedTop = current().scrollTop;
    click("chat-b"); await settle();
    click("chat-a"); await settle();
    check(near(current().scrollTop, savedTop), "unchanged A scroll position was not restored");
    results.push("scroll-restoration");
    click("chat-b"); await settle();
    click("open-a-partial"); await settle();
    check(!atBottom(), "partial execution history incorrectly triggered bottom restoration");
    click("complete-a-history"); await settle();
    check(near(current().scrollTop, savedTop), "saved scroll position not restored when execution history completed");
    results.push("delayed-history-restores-only-after-ready");
    click("append-a"); await settle();
    check(near(current().scrollTop, savedTop), "appending messages snapped a manually scrolled conversation to bottom");
    results.push("manual-scroll-not-followed");
    click("chat-b"); await settle();
    check(document.getElementById("draft").value === "draft for B", "B draft not restored");
    click("new-chat"); await settle();
    check(document.getElementById("draft").value === "", "new conversation did not start empty");
    await typeText("unsent new");
    click("new-chat"); await settle();
    check(document.getElementById("draft").value === "", "second new conversation inherited first draft");
    results.push("new-chat-isolation");
    click("chat-a"); await settle();
    check(atBottom(), "changed A messages should invalidate saved position; top="+current().scrollTop+", height="+current().scrollHeight+", client="+current().clientHeight);
    results.push("changed-history-invalidates");
    const top = document.querySelector('button[aria-label="Scroll to top"]');
    check(top, "missing scroll-to-top control when at bottom");
    top.click(); await settle(500);
    check(current().scrollTop < 10, "scroll-to-top button did not reach the top; top="+current().scrollTop);
    check(document.querySelector('button[aria-label="Scroll to bottom"]'), "missing bottom button after scrolling to top");
    document.querySelector('button[aria-label="Scroll to bottom"]').click(); await settle(500);
    check(atBottom(), "scroll-to-bottom button did not reach the bottom");
    results.push("scroll-buttons");
    current().scrollTop = 320;
    current().dispatchEvent(new Event('scroll'));
    await settle();
    check(!atBottom(), 'equal-length test requires a saved manual position');
    click("chat-b"); await settle();
    click("mutate-a"); await settle();
    click("chat-a"); await settle();
    check(atBottom(), "same-length content change did not invalidate saved scroll");
    results.push("equal-length-content-invalidates");
    const title = document.querySelector("#inline-title-test .rich-title");
    check(title, "completed canonical document did not mount the inline rich title");
    check(["inline", "inline-block"].includes(getComputedStyle(title).display),
      "inline <title> is laid out as a block");
    check(getComputedStyle(title).fontVariantNumeric === "tabular-nums", "tabular numeric title formatting lost");
    results.push("inline-rich-title");
    const provisional = document.querySelector("#provisional-title-test");
    check(provisional && !provisional.querySelector(".rich-title"),
      "provisional source incorrectly mounted a rich title");
    check(provisional.textContent.includes('<title size="xl"'),
      "provisional source lost its literal markup");
    results.push("provisional-rich-title-inert");
    click('new-chat'); await settle();
    await typeText('sent prompt');
    (window as any).draftTest.begin(); await settle();
    check((window as any).draftTest.pending, 'submitted context was not pending');
    check(document.getElementById('draft').value === '', 'submitted text stayed in composer');
    await typeText('new text typed while request pending');
    (window as any).draftTest.accept(); await settle();
    check(document.getElementById('draft').value === 'new text typed while request pending', 'unsent text did not move into the real chat');
    check((window as any).draftTest.pending, 'pending request did not move to permanent chat');
    click('chat-b'); await settle();
    check(!(window as any).draftTest.pending, 'request for one chat blocked another');
    (window as any).draftTest.done(); await settle();
    click('chat-a'); await settle();
    results.push('pending-ownership-and-draft-handoff');
    await typeText('failed request');
    (window as any).draftTest.begin(); await settle();
    await typeText('added after submission');
    (window as any).draftTest.fail(); await settle();
    check(document.getElementById('draft').value === 'failed request\n\nadded after submission', 'failed submit lost newer draft or original prompt');
    results.push('failed-send-preserves-edits');
    current().scrollTop = 200; current().dispatchEvent(new Event('scroll')); await settle();
    const manualBeforeResize = current().scrollTop;
    (window as any).draftTest.grow(); await settle(150);
    check(near(current().scrollTop, manualBeforeResize), 'content resize changed manual position');
    const bottom = document.querySelector('button[aria-label="Scroll to bottom"]');
    check(bottom, 'bottom navigation unavailable during resize check');
    bottom.click(); await settle(150);
    (window as any).draftTest.grow(); await settle(150);
    check(atBottom(), 'content resize did not preserve bottom-follow');
    results.push('resize-preserves-manual-and-bottom-follow');
    current().scrollTop = 190; current().dispatchEvent(new Event('scroll')); await settle();
    check(!atBottom(), 'attachment revision test needs manually scrolled conversation');
    click('chat-b'); await settle();
    click('change-attachment-a'); await settle();
    click('chat-a'); await settle();
    check(atBottom(), 'attachment-only change did not invalidate saved position');
    results.push('attachment-revision-invalidates');
    current().scrollTop = 190; current().dispatchEvent(new Event('scroll')); await settle();
    click('chat-b'); await settle();
    click('change-footer-a'); await settle();
    click('chat-a'); await settle();
    check(atBottom(), 'agent execution footer change did not invalidate saved position');
    results.push('execution-revision-invalidates');
    report("pass", results.join(", "));
  } catch (error) {
    report("fail", String(error?.stack || error));
  }
}
function report(status, text) {
  const result = document.createElement("pre");
  result.id = "browser-results";
  result.setAttribute("data-state", status);
  result.textContent = text;
  document.body.appendChild(result);
}
setTimeout(run, 100);
`.replace("__MEZHS_TITLE_SOURCE__", JSON.stringify(titleSource))
  .replace("__MEZHS_TITLE_DOCUMENT__", JSON.stringify(titleDocument));

const renameSource = String.raw`
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
  process.env.PROGRAMFILES && path.join(process.env.PROGRAMFILES, 'Google', 'Chrome', 'Application', 'chrome.exe'),
  process.env["PROGRAMFILES(X86)"] && path.join(process.env["PROGRAMFILES(X86)"], 'Google', 'Chrome', 'Application', 'chrome.exe'),
  process.env.PROGRAMFILES && path.join(process.env.PROGRAMFILES, 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
  process.env["PROGRAMFILES(X86)"] && path.join(process.env["PROGRAMFILES(X86)"], 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
  '/usr/bin/chromium', '/usr/bin/chromium-browser', '/usr/bin/google-chrome',
  '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
];
const chrome = chromeCandidates.find(candidate => candidate && fs.existsSync(candidate));

function runBrowser(source, sourcefile, prefix, virtualTimeBudget = 12000) {
  assert.ok(chrome, 'A real Chromium browser is required for this regression test');
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), prefix));
  try {
    const result = esbuild.buildSync({
      stdin: { contents: source, resolveDir: frontend, sourcefile, loader: "tsx" },
      bundle: true, format: "iife", platform: "browser", jsx: "automatic", preserveSymlinks: true,
      write: false, outdir: temp,
    });
    for (const asset of result.outputFiles) fs.writeFileSync(path.join(temp, path.basename(asset.path)), asset.contents);
    const script = result.outputFiles.find(asset => asset.path.endsWith(".js"));
    const stylesheet = result.outputFiles.find(asset => asset.path.endsWith(".css"));
    assert.ok(script, "browser script was not built");
    fs.writeFileSync(path.join(temp, "index.html"),
      `<!doctype html><html><head>${stylesheet ? `<link rel="stylesheet" href="${path.basename(stylesheet.path)}">` : ""}</head>` +
      `<body><div id="root"></div><script src="${path.basename(script.path)}"></script></body></html>`);
    const invocation = spawnSync(chrome, [
      "--headless=new", "--no-sandbox", "--disable-gpu", "--disable-background-networking",
      "--no-first-run", `--user-data-dir=${path.join(temp, "profile")}`,
      "--virtual-time-budget=" + virtualTimeBudget, "--dump-dom", "file:///" + path.join(temp, "index.html").replace(/\\/g, "/"),
    ], { timeout: 60000, encoding: "utf8", maxBuffer: 12 * 1024 * 1024 });
    assert.equal(invocation.status, 0, `Chrome exited unsuccessfully: ${invocation.error || invocation.stderr}`);
    const dom = invocation.stdout;
    assert.match(dom, /id="browser-results"/, `Browser harness did not finish: ${dom.slice(-2500)} ${invocation.stderr}`);
    const report = /<pre id="browser-results" data-state="([^"]+)">([^<]*)<\/pre>/.exec(dom);
    assert.ok(report, `Browser result was malformed: ${dom.slice(-2000)}`);
    assert.equal(report[1], "pass", report[2]);
    return report[2];
  } finally {
    fs.rmSync(temp, { recursive: true, force: true });
  }
}

test("real Chromium rendering: drafts, scroll transitions, controls and title layout", { timeout: 70000 }, () => {
  const report = runBrowser(appSource, "chat-ui-harness.tsx", "mezhs-chat-ui-");
  assert.match(report, /equal-length-content-invalidates/);
  assert.match(report, /inline-rich-title/);
  assert.match(report, /provisional-rich-title-inert/);
  for (const scenario of [
    "scoped-draft-isolation",
    "pending-ownership-and-draft-handoff",
    "failed-send-preserves-edits",
    "resize-preserves-manual-and-bottom-follow",
    "attachment-revision-invalidates",
    "execution-revision-invalidates",
    "delayed-history-restores-only-after-ready",
  ]) assert.ok(report.includes(scenario), "Missing Chromium scenario: " + scenario);
});

test("Chromium chat rename: tail truncation, caret and list position", { timeout: 70000 }, () => {
  const report = runBrowser(renameSource, "chat-rename-harness.tsx", "mezhs-chat-rename-", 15000);
  assert.ok(report.includes("rename-tail-truncation"));
});
