const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const root = path.resolve(__dirname, "..");

function loadChatGptModule() {
  const source = fs.readFileSync(
    path.join(root, "integrations", "Mezhs.Integrations.ChatGpt", "browser", "chatgpt.ts"),
    "utf8"
  );
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "mezhs-chatgpt-editor-"));
  const file = path.join(directory, "chatgpt.cjs");
  fs.writeFileSync(file, source);
  return require(file);
}

test("ChatGPT page submit supports the fallback textarea composer", async () => {
  const chatgpt = loadChatGptModule();
  let inputEvent = null;
  let clicked = false;

  class FakeEvent {
    constructor(type, options) {
      this.type = type;
      this.bubbles = options?.bubbles ?? false;
    }
  }

  class FakeTextarea {
    constructor() {
      this.tagName = "TEXTAREA";
      this.ownerDocument = { defaultView: view };
    }
    focus() {}
    dispatchEvent(event) { inputEvent = event; }
  }
  Object.defineProperty(FakeTextarea.prototype, "value", {
    get() { return this._value || ""; },
    set(value) { this._value = value; }
  });

  const view = {
    HTMLTextAreaElement: FakeTextarea,
    HTMLInputElement: class {},
    Event: FakeEvent
  };
  const editor = new FakeTextarea();
  const send = { disabled: false, click: () => { clicked = true; } };
  const previousDocument = global.document;
  const previousLocation = global.location;
  try {
    global.document = {
      querySelector: selector => {
        if (selector.includes('textarea[name="prompt-textarea"]')) return editor;
        if (selector.includes('send-button')) return send;
        return null;
      }
    };
    global.location = { href: "https://chatgpt.com/" };

    await chatgpt.pageOperations.submitPrompt({
      args: { prompt: "hello fallback" },
      sleep: async () => {}
    });
  } finally {
    global.document = previousDocument;
    global.location = previousLocation;
  }

  assert.equal(editor.value, "hello fallback");
  assert.equal(inputEvent?.type, "input");
  assert.equal(inputEvent?.bubbles, true);
  assert.equal(clicked, true);
});


test("ChatGPT page submit skips hidden composers and disabled send candidates", async () => {
  const chatgpt = loadChatGptModule();

  class FakeEvent {
    constructor(type, options) {
      this.type = type;
      this.bubbles = options?.bubbles ?? false;
    }
  }

  class FakeTextarea {
    constructor({ visible, form = null }) {
      this.tagName = "TEXTAREA";
      this.ownerDocument = { defaultView: view };
      this._visible = visible;
      this._form = form;
      this.isConnected = true;
      this.disabled = false;
    }
    focus() {}
    dispatchEvent() {}
    getAttribute() { return null; }
    getBoundingClientRect() {
      return this._visible
        ? { width: 320, height: 40 }
        : { width: 0, height: 0 };
    }
    closest(selector) {
      return selector === "form" ? this._form : null;
    }
  }
  Object.defineProperty(FakeTextarea.prototype, "value", {
    get() { return this._value || ""; },
    set(value) { this._value = value; }
  });

  const view = {
    HTMLTextAreaElement: FakeTextarea,
    HTMLInputElement: class {},
    Event: FakeEvent,
    getComputedStyle: () => ({ display: "block", visibility: "visible" })
  };

  let disabledClicks = 0;
  let enabledClicks = 0;
  const disabledSend = {
    disabled: true,
    isConnected: true,
    getAttribute: () => null,
    getBoundingClientRect: () => ({ width: 40, height: 40 }),
    click: () => { disabledClicks++; }
  };
  const enabledSend = {
    disabled: false,
    isConnected: true,
    getAttribute: () => null,
    getBoundingClientRect: () => ({ width: 40, height: 40 }),
    click: () => { enabledClicks++; }
  };
  const form = {
    querySelectorAll: () => [disabledSend, enabledSend]
  };

  const hiddenEditor = new FakeTextarea({ visible: false });
  const realEditor = new FakeTextarea({ visible: true, form });

  const previousDocument = global.document;
  const previousLocation = global.location;
  try {
    global.document = {
      querySelectorAll: selector => {
        if (selector === "#prompt-textarea") return [hiddenEditor];
        if (selector === 'textarea[name="prompt-textarea"]') return [realEditor];
        return [];
      }
    };
    global.location = { href: "https://chatgpt.com/" };

    await chatgpt.pageOperations.submitPrompt({
      args: { prompt: "real composer" },
      sleep: async () => {}
    });
  } finally {
    global.document = previousDocument;
    global.location = previousLocation;
  }

  assert.equal(hiddenEditor.value, "");
  assert.equal(realEditor.value, "real composer");
  assert.equal(disabledClicks, 0);
  assert.equal(enabledClicks, 1);
});
