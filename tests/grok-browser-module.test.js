const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

function loadGrokModule() {
  const file = path.join(
    __dirname,
    "..",
    "integrations",
    "Mezhs.Integrations.Grok",
    "browser",
    "grok.ts"
  );
  const source = fs.readFileSync(file, "utf8");
  const localModule = { exports: {} };
  new Function("module", "require", source)(localModule, require);
  return localModule.exports;
}

function fakeWindow({ authorized = true } = {}) {
  const loaded = [];
  const scripts = [];
  return {
    loaded,
    scripts,
    window: {
      async loadURL(url) {
        loaded.push(url);
      },
      webContents: {
        session: {
          cookies: {
            async get() {
              return authorized ? [{ name: "sso", value: "session" }] : [];
            }
          }
        },
        async executeJavaScript(source) {
          scripts.push(source);
          return {
            ok: true,
            text: "reply",
            chatUrl: "https://grok.com/c/test"
          };
        }
      }
    }
  };
}

test("Grok authorization follows the persistent Grok session cookie", async () => {
  const grok = loadGrokModule();
  assert.equal(await grok.isAuthorized(fakeWindow().window), true);
  assert.equal(
    await grok.isAuthorized(fakeWindow({ authorized: false }).window),
    false
  );
});

test("Grok new chat starts at the provider home page", async () => {
  const grok = loadGrokModule();
  const fake = fakeWindow();

  const result = await grok.operations.newChat({
    window: fake.window,
    args: { prompt: "hello" }
  });

  assert.equal(fake.loaded[0], "https://grok.com/");
  assert.equal(result.text, "reply");
  assert.match(fake.scripts[0], /execCommand\('insertText'/);
});

test("Grok continuation reloads the exact stored Grok chat URL", async () => {
  const grok = loadGrokModule();
  const fake = fakeWindow();

  await grok.operations.send({
    window: fake.window,
    args: {
      prompt: "continue",
      chatUrl: "https://grok.com/c/existing"
    }
  });

  assert.equal(fake.loaded[0], "https://grok.com/c/existing");
});

test("Grok continuation rejects non-Grok URLs", async () => {
  const grok = loadGrokModule();
  const fake = fakeWindow();

  await assert.rejects(
    grok.operations.send({
      window: fake.window,
      args: {
        prompt: "continue",
        chatUrl: "https://example.com/"
      }
    }),
    /missing or invalid/
  );
});
