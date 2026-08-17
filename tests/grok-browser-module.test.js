// Contract tests for Grok navigation/auth and its renderer-attached page operation boundary.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const grokFile = path.join(
  __dirname,
  "..",
  "integrations",
  "Mezhs.Integrations.Grok",
  "browser",
  "grok.ts"
);

function loadGrokModule() {
  const source = fs.readFileSync(grokFile, "utf8");
  const localModule = { exports: {} };
  new Function("module", "require", source)(localModule, require);
  return localModule.exports;
}

function fakeBrowser({ authorized = true } = {}) {
  const loaded = [];
  const pageCalls = [];
  return {
    loaded,
    pageCalls,
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
        }
      }
    },
    page: {
      async invoke(operation, args) {
        pageCalls.push({ operation, args });
        if (operation === "models") {
          if (args?.select) return true;
          return [
            { id: "Grok 4.5", name: "Grok 4.5" },
            { id: "Expert", name: "Expert" }
          ];
        }
        return {
          text: "reply",
          chatUrl: "https://grok.com/c/test"
        };
      }
    }
  };
}

test("Grok module exposes its DOM work as attached page operations", () => {
  const grok = loadGrokModule();
  assert.equal(typeof grok.pageOperations?.sendPrompt, "function");
  assert.equal(typeof grok.pageOperations?.models, "function");
  assert.doesNotMatch(fs.readFileSync(grokFile, "utf8"), /executeJavaScript/);
});

test("Grok authorization follows the persistent Grok session cookie", async () => {
  const grok = loadGrokModule();
  assert.equal(await grok.isAuthorized(fakeBrowser().window), true);
  assert.equal(
    await grok.isAuthorized(fakeBrowser({ authorized: false }).window),
    false
  );
});

test("Grok model discovery opens the provider home page and uses the page boundary", async () => {
  const grok = loadGrokModule();
  const fake = fakeBrowser();

  const result = await grok.operations.getModels({
    window: fake.window,
    page: fake.page,
    args: {}
  });

  assert.deepEqual(result, [
    { id: "Grok 4.5", name: "Grok 4.5" },
    { id: "Expert", name: "Expert" }
  ]);
  assert.deepEqual(fake.loaded, ["https://grok.com/"]);
  assert.deepEqual(fake.pageCalls, [{ operation: "models", args: {} }]);
});

test("Grok new chat starts at the provider home page and invokes the page operation", async () => {
  const grok = loadGrokModule();
  const fake = fakeBrowser();

  const result = await grok.operations.newChat({
    window: fake.window,
    page: fake.page,
    args: { prompt: "hello" }
  });

  assert.equal(fake.loaded[0], "https://grok.com/");
  assert.equal(result.text, "reply");
  assert.deepEqual(fake.pageCalls, [{
    operation: "sendPrompt",
    args: { prompt: "hello" }
  }]);
});

test("Grok applies an explicit model before sending the message", async () => {
  const grok = loadGrokModule();
  const fake = fakeBrowser();

  await grok.operations.newChat({
    window: fake.window,
    page: fake.page,
    args: { prompt: "hello", model: "Grok 4.5" }
  });

  assert.deepEqual(fake.pageCalls, [
    { operation: "models", args: { select: "Grok 4.5" } },
    { operation: "sendPrompt", args: { prompt: "hello", model: "Grok 4.5" } }
  ]);
});

test("Grok continuation reloads the exact stored Grok chat URL", async () => {
  const grok = loadGrokModule();
  const fake = fakeBrowser();

  await grok.operations.send({
    window: fake.window,
    page: fake.page,
    args: {
      prompt: "continue",
      chatUrl: "https://grok.com/c/existing"
    }
  });

  assert.equal(fake.loaded[0], "https://grok.com/c/existing");
  assert.equal(fake.pageCalls[0].operation, "sendPrompt");
});

test("Grok continuation rejects non-Grok URLs", async () => {
  const grok = loadGrokModule();
  const fake = fakeBrowser();

  await assert.rejects(
    grok.operations.send({
      window: fake.window,
      page: fake.page,
      args: {
        prompt: "continue",
        chatUrl: "https://example.com/"
      }
    }),
    /missing or invalid/
  );
});