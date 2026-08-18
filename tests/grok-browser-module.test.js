// Contract tests for Grok authentication and its semantic provider page-operation boundary.
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
          return [
            { id: "auto", name: "Auto" },
            { id: "expert", name: "Expert" }
          ];
        }
        if (operation === "chat") {
          return {
            text: "reply",
            chatUrl: "https://grok.com/c/test"
          };
        }
        throw new Error(`Unexpected page operation '${operation}'.`);
      }
    }
  };
}

test("Grok module uses semantic page operations instead of DOM model or chat automation", () => {
  const grok = loadGrokModule();
  const source = fs.readFileSync(grokFile, "utf8");
  assert.equal(typeof grok.pageOperations?.models, "function");
  assert.equal(typeof grok.pageOperations?.chat, "function");
  assert.doesNotMatch(source, /executeJavaScript|querySelector|execCommand|contenteditable|Model select/);
  assert.match(source, /\/rest\/app-chat\/conversations\/new/);
  assert.match(source, /modeId/);
});

test("Grok authorization follows the persistent Grok session cookie", async () => {
  const grok = loadGrokModule();
  assert.equal(await grok.isAuthorized(fakeBrowser().window), true);
  assert.equal(
    await grok.isAuthorized(fakeBrowser({ authorized: false }).window),
    false
  );
});

test("Grok model page operation discovers authenticated modes from POST /rest/modes", async () => {
  const grok = loadGrokModule();
  const originalFetch = global.fetch;
  let request;
  global.fetch = async (url, options) => {
    request = { url: String(url), options };
    return new Response(JSON.stringify({
      data: {
        modes: [
          { modeId: "auto", displayName: "Auto" },
          { id: "expert", name: "Expert" },
          { mode: { modeId: "heavy", displayName: "Heavy" } },
          { modeId: "disabled", displayName: "Disabled", available: false },
          { modeId: "auto", displayName: "Duplicate Auto" }
        ]
      }
    }), {
      status: 200,
      headers: { "content-type": "application/json" }
    });
  };

  try {
    assert.deepEqual(
      await grok.pageOperations.models({}),
      [
        { id: "auto", name: "Auto" },
        { id: "expert", name: "Expert" },
        { id: "heavy", name: "Heavy" }
      ]
    );
    assert.equal(request.url, "https://grok.com/rest/modes");
    assert.equal(request.options.method, "POST");
    assert.equal(request.options.headers["Content-Type"], "application/json");
    assert.equal(request.options.body, "{}");
    assert.equal(request.options.credentials, "include");
    assert.equal(request.options.cache, "no-store");
  } finally {
    global.fetch = originalFetch;
  }
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
    { id: "auto", name: "Auto" },
    { id: "expert", name: "Expert" }
  ]);
  assert.deepEqual(fake.loaded, ["https://grok.com/"]);
  assert.deepEqual(fake.pageCalls, [{ operation: "models", args: {} }]);
});

test("Grok new chat passes the discovered mode directly to the semantic chat operation", async () => {
  const grok = loadGrokModule();
  const fake = fakeBrowser();

  const result = await grok.operations.newChat({
    window: fake.window,
    page: fake.page,
    args: { prompt: "hello", model: "expert" }
  });

  assert.equal(fake.loaded[0], "https://grok.com/");
  assert.equal(result.text, "reply");
  assert.deepEqual(fake.pageCalls, [{
    operation: "chat",
    args: { prompt: "hello", model: "expert" }
  }]);
});

test("Grok semantic chat request sends modeId and parses final app-chat response", async () => {
  const grok = loadGrokModule();
  const originalFetch = global.fetch;
  let request;
  global.fetch = async (url, options) => {
    request = { url: String(url), options };
    return new Response([
      JSON.stringify({ conversationId: "conv-1" }),
      JSON.stringify({
        result: {
          sender: "assistant",
          message: "thinking",
          isThinking: true,
          messageTag: "assistant"
        }
      }),
      JSON.stringify({
        result: {
          sender: "assistant",
          message: "Answer",
          messageTag: "final"
        }
      })
    ].join("\n"), { status: 200 });
  };

  try {
    const result = await grok.pageOperations.chat({
      args: { prompt: "hello", model: "expert" }
    });
    const payload = JSON.parse(request.options.body);

    assert.equal(request.url, "https://grok.com/rest/app-chat/conversations/new");
    assert.equal(request.options.method, "POST");
    assert.equal(request.options.credentials, "include");
    assert.equal(request.options.headers["Content-Type"], "application/json");
    assert.ok(request.options.headers["x-statsig-id"]);
    assert.match(request.options.headers["x-xai-request-id"], /^[0-9a-f-]{36}$/i);
    assert.equal(payload.message, "hello");
    assert.equal(payload.modeId, "expert");
    assert.equal(payload.sendFinalMetadata, true);
    assert.deepEqual(result, {
      text: "Answer",
      chatUrl: "https://grok.com/c/conv-1"
    });
  } finally {
    global.fetch = originalFetch;
  }
});

test("Grok semantic chat defaults modeId to auto and accepts response.token frames", async () => {
  const grok = loadGrokModule();
  const originalFetch = global.fetch;
  let payload;
  global.fetch = async (_url, options) => {
    payload = JSON.parse(options.body);
    return new Response([
      JSON.stringify({ result: { conversationId: "conv-2" } }),
      JSON.stringify({
        result: {
          response: {
            token: "Modern answer",
            messageTag: "final",
            isThinking: false
          }
        }
      })
    ].join("\n"), { status: 200 });
  };

  try {
    const result = await grok.pageOperations.chat({ args: { prompt: "hello" } });
    assert.equal(payload.modeId, "auto");
    assert.equal(result.text, "Modern answer");
    assert.equal(result.chatUrl, "https://grok.com/c/conv-2");
  } finally {
    global.fetch = originalFetch;
  }
});

test("Grok semantic chat surfaces provider HTTP failures", async () => {
  const grok = loadGrokModule();
  const originalFetch = global.fetch;
  global.fetch = async () => new Response("denied", { status: 403 });
  try {
    await assert.rejects(
      grok.pageOperations.chat({ args: { prompt: "hello", model: "expert" } }),
      /failed with HTTP 403: denied/
    );
  } finally {
    global.fetch = originalFetch;
  }
});
