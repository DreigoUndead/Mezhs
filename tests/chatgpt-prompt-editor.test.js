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
