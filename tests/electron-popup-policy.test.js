const test = require("node:test");
const assert = require("node:assert/strict");
const { EventEmitter } = require("node:events");
const { popupAllowed, attachPopupPolicy } = require("../electron/popup-policy");

test("Only provider-specific authentication HTTPS origins are accepted", () => {
  const cases = [
    ["https://chatgpt.com/", "https://auth.openai.com/authorize?client=abc", true],
    ["https://chatgpt.com/", "https://accounts.google.com/o/oauth2/auth", true],
    ["https://chatgpt.com/", "https://appleid.apple.com/auth/authorize", true],
    ["https://chatgpt.com/", "https://login.microsoftonline.com/common/oauth2", true],
    ["https://chatgpt.com/", "https://login.live.com/oauth20_authorize.srf", true],
    ["https://grok.com/", "https://accounts.x.ai/", true],
    ["https://grok.com/", "https://auth.x.ai/signin", true],
    ["https://grok.com/", "https://auth.grok.com/", false],
    ["https://grok.com/", "https://x.com/i/flow/login", true],
    ["https://gemini.google.com/app", "https://accounts.google.com/", true],
    ["https://gemini.google.com/app", "https://auth.openai.com/", false],
    ["https://chatgpt.com/", "https://accounts.x.ai/", false],
    ["https://chatgpt.com/", "https://evil.example/", false],
    ["https://chatgpt.com/", "https://auth.openai.com.evil.example/", false],
    ["https://chatgpt.com/", "https://evil-auth.openai.com/", false],
    ["https://chatgpt.com/", "https://auth.openai.com@evil.example/", false],
    ["https://chatgpt.com/", "https://user@auth.openai.com/", false],
    ["https://chatgpt.com/", "http://auth.openai.com/", false],
    ["https://chatgpt.com/", "https://auth.openai.com:8443/", false],
    ["https://chatgpt.com/", "javascript:alert(1)", false],
    ["https://chatgpt.com/", "file:///C:/Windows/System32", false],
    ["https://chatgpt.com/", "data:text/html,hello", false],
    ["https://chatgpt.com/", "about:blank", false],
    ["https://chatgpt.com/", "blob:https://auth.openai.com/uuid", false],
    ["https://chatgpt.com/", "ftp://auth.openai.com/", false],
    ["https://chatgpt.com/", "http://127.0.0.1:5000/", false],
    ["https://chatgpt.com/", "not a url", false],
    ["data:text/html,test", "https://auth.openai.com/", false]
  ];
  for (const [home, url, expected] of cases)
    assert.equal(popupAllowed(home, url), expected, home + " -> " + url);
});

function contents(initialUrl) {
  const emitter = new EventEmitter();
  emitter.getURL = () => initialUrl;
  emitter.setWindowOpenHandler = handler => { emitter.open = handler; };
  return emitter;
}

test("Allowed child shares the provider session, uses auth-only preload and secure preferences", () => {
  const session = {};
  const parent = contents("https://chatgpt.com/");
  attachPopupPolicy(parent, "https://chatgpt.com/", session, "C:/temp/auth-preload.js");
  const denied = parent.open({ url: "https://unrelated.example/" });
  assert.equal(denied.action, "deny");
  const allowed = parent.open({ url: "https://accounts.google.com/oauth" });
  assert.equal(allowed.action, "allow");
  assert.deepEqual(allowed.overrideBrowserWindowOptions.webPreferences, {
    session,
    preload: "C:/temp/auth-preload.js",
    sandbox: true,
    contextIsolation: true,
    nodeIntegration: false
  });
  parent.getURL = () => "https://unrelated.example/";
  assert.equal(parent.open({ url: "https://accounts.google.com/" }).action, "deny");
});

test("Permitted children block unrelated redirects, document navigation and nested popups", () => {
  const parent = contents("https://grok.com/");
  attachPopupPolicy(parent, "https://grok.com/", {}, "auth.js");
  const child = contents("https://accounts.x.ai/");
  parent.emit("did-create-window", { webContents: child });

  for (const eventName of ["will-navigate", "will-redirect"]) {
    let prevented = false;
    child.emit(eventName, { preventDefault: () => { prevented = true; } },
      { url: "https://accounts.x.ai.evil.example/", isMainFrame: true });
    assert.equal(prevented, true, eventName + " should prevent a lookalike");
    prevented = false;
    child.emit(eventName, { preventDefault: () => { prevented = true; } },
      { url: "https://x.com/i/flow/login", isMainFrame: true });
    assert.equal(prevented, false, eventName + " should preserve supported authorization");
    child.emit(eventName, { preventDefault: () => { prevented = true; } },
      { url: "https://unrelated.example/", isMainFrame: false });
    assert.equal(prevented, false, "subframe navigation must remain outside the main-frame boundary");
  }

  assert.equal(child.open({ url: "https://accounts.google.com/" }).action, "allow");
  assert.equal(child.open({ url: "https://evil.example/" }).action, "deny");
  child.getURL = () => "https://evil.example/";
  assert.equal(child.open({ url: "https://accounts.google.com/" }).action, "deny");
});
