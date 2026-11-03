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

const appSource = String.raw`
import React, { useState } from "react";
import { createRoot } from "react-dom/client";
import { ConversationViewport } from "./node_modules/@mezhs/web-lib/src/ConversationViewport";
import { useConversationDrafts } from "./node_modules/@mezhs/web-lib/src/useConversationDrafts";
import { MarkdownContent } from "./node_modules/@mezhs/web-lib/src/MarkdownContent";
import "./node_modules/@mezhs/web-lib/src/styles.css";

const initial = (tag, count) => Array.from({ length: count }, (_, i) => ({
  messageId: tag + i,
  status: "Completed",
  content: tag + " entry " + i + " " + "x".repeat(100),
}));
function App() {
  const [selected, setSelected] = useState("A");
  const [newSequence, setNewSequence] = useState(0);
  const [messages, setMessages] = useState({ A: initial("alpha", 45), B: initial("beta", 30) });
  const { draft, setDraft } = useConversationDrafts(selected);
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
        <textarea id="draft" value={draft} onChange={e => setDraft(e.target.value)} />
      </div>
      <ConversationViewport conversationKey={selected} messages={messages[selected] || []} empty={!(messages[selected]?.length)}>
        {(messages[selected] || []).map(m => (
          <div key={m.messageId} className="browser-message" style={{ height: 69, padding: 4, boxSizing: "border-box" }}>
            {m.content}
          </div>
        ))}
      </ConversationViewport>
      <div id="inline-title-test" style={{ flexShrink: 0 }}>
        <MarkdownContent content={'Before <title size="xl" color="success" tabularNums>123.45</title> After'} />
      </div>
    </div>
  );
}

createRoot(document.getElementById("root")).render(<App />);

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
    check(title && ["inline", "inline-block"].includes(getComputedStyle(title).display),
      "inline <title> is laid out as a block");
    check(getComputedStyle(title).fontVariantNumeric === "tabular-nums", "tabular numeric title formatting lost");
    results.push("inline-rich-title");
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

test("real Chromium rendering: drafts, scroll transitions, controls and title layout", { timeout: 70000, skip: !chrome && "Chrome or Edge is not installed" }, () => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "mezhs-chat-ui-"));
  try {
    const result = esbuild.buildSync({
      stdin: { contents: appSource, resolveDir: frontend, sourcefile: "chat-ui-harness.tsx", loader: "tsx" },
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
      "--virtual-time-budget=12000", "--dump-dom", "file:///" + path.join(temp, "index.html").replace(/\\/g, "/"),
    ], { timeout: 60000, encoding: "utf8", maxBuffer: 12 * 1024 * 1024 });
    assert.equal(invocation.status, 0, `Chrome exited unsuccessfully: ${invocation.error || invocation.stderr}`);
    const dom = invocation.stdout;
    assert.match(dom, /id="browser-results"/, `Browser harness did not finish: ${dom.slice(-2500)} ${invocation.stderr}`);
    const report = /<pre id="browser-results" data-state="([^"]+)">([^<]*)<\/pre>/.exec(dom);
    assert.ok(report, `Browser result was malformed: ${dom.slice(-2000)}`);
    assert.equal(report[1], "pass", report[2]);
    assert.match(report[2], /equal-length-content-invalidates/);
  } finally {
    fs.rmSync(temp, { recursive: true, force: true });
  }
});
