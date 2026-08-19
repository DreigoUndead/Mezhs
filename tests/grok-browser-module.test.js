// Contract tests for Grok authentication, API-backed model discovery, and native UI send.
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
        if (operation === "selectModel") return true;
        if (operation === "sendPrompt") {
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

async function withChangedModelPicker(run) {
  const names = [
    "fetch", "window", "document", "HTMLElement",
    "PointerEvent", "MouseEvent", "KeyboardEvent", "getComputedStyle"
  ];
  const originals = new Map(names.map(name => [name, global[name]]));
  let current = "Fast";
  let expanded = false;

  class FakeEvent {
    constructor(type, init = {}) {
      this.type = type;
      Object.assign(this, init);
    }
  }

  const trigger = {
    id: "",
    textContent: current,
    getAttribute(name) {
      if (name === "aria-label") return "Choose model";
      if (name === "aria-haspopup") return "dialog";
      if (name === "aria-expanded") return expanded ? "true" : "false";
      return null;
    },
    querySelector() {
      return null;
    },
    getBoundingClientRect() {
      return { left: 0, top: 0, width: 120, height: 32 };
    },
    dispatchEvent(event) {
      if (event.type === "pointerdown") expanded = true;
      if (event.type === "keydown" && event.key === "Escape") expanded = false;
      return true;
    }
  };

  const row = {
    id: "",
    textContent: "Expert",
    getAttribute(name) {
      if (name === "aria-disabled") return "false";
      return null;
    },
    matches() {
      return false;
    },
    querySelector(selector) {
      return selector === "span.font-semibold" ? { textContent: "Expert" } : null;
    },
    _click() {
      current = "Expert";
      trigger.textContent = current;
      expanded = false;
    }
  };

  global.fetch = async () => new Response(JSON.stringify({
    data: {
      modes: [
        { modeId: "fast", displayName: "Fast" },
        { modeId: "expert", displayName: "Expert" }
      ]
    }
  }), {
    status: 200,
    headers: { "content-type": "application/json" }
  });
  global.window = {};
  global.PointerEvent = FakeEvent;
  global.MouseEvent = FakeEvent;
  global.KeyboardEvent = FakeEvent;
  global.HTMLElement = class HTMLElement {};
  global.HTMLElement.prototype.click = function () {
    this._click?.();
  };
  global.getComputedStyle = () => ({ display: "block", visibility: "visible" });
  global.document = {
    getElementById() {
      return null;
    },
    querySelector() {
      return null;
    },
    querySelectorAll(selector) {
      if (selector === 'button[aria-haspopup="menu"]') return [];
      if (selector === "button") return [trigger];
      if (selector === '[role="menuitem"]') return expanded ? [row] : [];
      if (selector === '[role="menuitemradio"]' || selector === '[role="option"]') return [];
      if (selector === "span.font-semibold") return [];
      return [];
    }
  };

  try {
    return await run({ trigger, row, current: () => current });
  } finally {
    for (const [name, value] of originals) {
      if (value === undefined) delete global[name];
      else global[name] = value;
    }
  }
}

test("Grok keeps model discovery semantic but sends through the native UI", () => {
  const grok = loadGrokModule();
  const source = fs.readFileSync(grokFile, "utf8");

  assert.equal(typeof grok.pageOperations?.models, "function");
  assert.equal(typeof grok.pageOperations?.selectModel, "function");
  assert.equal(typeof grok.pageOperations?.sendPrompt, "function");
  assert.match(source, /\/rest\/modes/);
  assert.doesNotMatch(source, /\/rest\/app-chat\/conversations\/new/);
  assert.match(source, /PointerEvent\("pointerdown"/);
  assert.match(source, /data-testid=\\?"chat-submit/);
  assert.doesNotMatch(source, /executeJavaScript/);
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

test("Grok selectModel survives picker selector changes", async () => {
  const grok = loadGrokModule();
  await withChangedModelPicker(async ({ current }) => {
    const result = await grok.pageOperations.selectModel({
      args: { model: "expert" },
      sleep: async () => {}
    });
    assert.equal(result, true);
    assert.equal(current(), "Expert");
  });
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

test("Grok new chat selects the requested discovered mode before native send", async () => {
  const grok = loadGrokModule();
  const fake = fakeBrowser();

  const result = await grok.operations.newChat({
    window: fake.window,
    page: fake.page,
    args: { prompt: "hello", model: "expert" }
  });

  assert.equal(fake.loaded[0], "https://grok.com/");
  assert.equal(result.text, "reply");
  assert.deepEqual(fake.pageCalls, [
    { operation: "selectModel", args: { model: "expert" } },
    { operation: "sendPrompt", args: { prompt: "hello", model: "expert" } }
  ]);
});

test("Grok default send does not touch the model picker", async () => {
  const grok = loadGrokModule();
  const fake = fakeBrowser();

  await grok.operations.newChat({
    window: fake.window,
    page: fake.page,
    args: { prompt: "hello" }
  });

  assert.deepEqual(fake.pageCalls, [
    { operation: "sendPrompt", args: { prompt: "hello" } }
  ]);
});
