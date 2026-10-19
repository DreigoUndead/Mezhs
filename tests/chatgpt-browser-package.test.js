const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { execFileSync } = require("node:child_process");

const browser = path.resolve(__dirname, "../integrations/Mezhs.Integrations.ChatGpt/browser");
const bundle = path.join(browser, "chatgpt.ts");
const builder = path.join(browser, "build.js");
const operationNames = ["getModels", "getProjects", "newChat", "send", "sendPrompt"];

test("ChatGPT embedded browser module is synchronized with source modules", () => {
  assert.match(execFileSync(process.execPath, [builder, "--check"], { encoding: "utf8" }), /bundle is current/);
  const generated = fs.readFileSync(bundle, "utf8");
  assert.match(generated, /module[.]exports = bundledRequire/);
  assert.match(generated, /Missing packaged ChatGPT module/);
});

test("ChatGPT browser resource runs without sibling modules and retains public operations", () => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "mezhs-chatgpt-package-"));
  try {
    const isolated = path.join(temp, "Mezhs.Integrations.ChatGpt.js");
    fs.copyFileSync(bundle, isolated);
    const packaged = require(isolated);
    const modular = require(path.join(browser, "modules", "entry.js"));
    assert.equal(packaged.name, "ChatGPT");
    assert.equal(packaged.homeUrl, "https://chatgpt.com/");
    assert.equal(typeof packaged.isAuthorized, "function");
    assert.deepEqual(Object.keys(packaged.operations).sort(), operationNames);
    assert.deepEqual(Object.keys(modular.operations).sort(), operationNames);
    for (const operation of operationNames) assert.equal(typeof packaged.operations[operation], "function");
  } finally {
    fs.rmSync(temp, { recursive: true, force: true });
  }
});
