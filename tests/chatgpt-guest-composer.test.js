const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '..', 'integrations', 'Mezhs.Integrations.ChatGpt', 'browser', 'chatgpt.ts'), 'utf8');
const moduleUnderTest = { exports: {} };
new Function('module', 'require', source)(moduleUnderTest, require);

function mockPage(mode, options = {}) {
  let clicked = 0;
  let submitted = false;
  let navigations = 0;
  let ticks = 0;
  let entered = '';
  let enabled = false;
  const octane = mode === 'octane';
  const editorExists = mode !== 'missing';
  class TextArea {
    set value(value) { entered = value; }
    get value() { return entered; }
  }
  const send = {
    disabled: false,
    getAttribute: name => name === 'aria-disabled' && octane && !enabled ? 'true' : null,
    click() { clicked++; submitted = true; }
  };
  const form = { querySelector: selector => selector.includes('Send message') ? send : null };
  const editor = new TextArea();
  editor.tagName = 'TEXTAREA';
  editor.focus = () => {};
  editor.closest = () => octane ? form : null;
  editor.dispatchEvent = event => { if (event.type === 'input' && !options.neverEnable) enabled = true; };
  const assistant = octane
    ? {
        hasAttribute: name => name === 'data-message-complete',
        children: [{ innerText: 'ChatGPT said:' }, { get innerText() { return ticks >= (options.contentAfterTicks ?? 2) && !options.emptyReply && !(options.emptyFirstReply && clicked === 1) ? 'OCTANE_OK' : ''; } }]
      }
    : { innerText: 'LEGACY_OK' };
  const document = {
    querySelector(selector) {
      if (selector.includes('#prompt-textarea'))
        return editorExists && (!octane || selector.includes('#mobile-composer-prompt')) ? editor : null;
      if (selector.includes('send-button')) return octane ? null : send;
      return null;
    },
    querySelectorAll(selector) {
      if (!submitted) return [];
      if (selector.includes('data-conversation-transcript')) return octane ? [assistant] : [];
      if (selector.includes('data-message-author-role')) return octane ? [] : [assistant];
      return [];
    }
  };
  const context = {
    document,
    HTMLTextAreaElement: TextArea,
    HTMLInputElement: TextArea,
    Event: class { constructor(type) { this.type = type; } },
    InputEvent: class { constructor(type) { this.type = type; } },
    setTimeout: callback => { ticks++; callback(); }
  };
  return {
    window: { loadURL: async () => { navigations++; submitted = false; ticks = 0; }, webContents: { executeJavaScript: script => vm.runInNewContext(script, context) } },
    get navigations() { return navigations; },
    get clicked() { return clicked; },
    get entered() { return entered; },
    get ticks() { return ticks; }
  };
}

test('Octane composer waits for complete, rendered reply content', async () => {
  const page = mockPage('octane');
  const result = await moduleUnderTest.exports.operations.sendPrompt({ window: page.window, args: { prompt: 'hello' } });
  assert.equal(page.entered, 'hello');
  assert.equal(page.clicked, 1);
  assert.equal(result.ok, true);
  assert.equal(result.text, 'OCTANE_OK');
  assert.ok(page.ticks >= 2);
});

test('legacy composer and reply extraction still work', async () => {
  const page = mockPage('legacy');
  const result = await moduleUnderTest.exports.operations.sendPrompt({ window: page.window, args: { prompt: 'legacy' } });
  assert.equal(page.clicked, 1);
  assert.equal(result.ok, true);
  assert.equal(result.text, 'LEGACY_OK');
});

test('aria-disabled send button is never clicked', async () => {
  const page = mockPage('octane', { neverEnable: true });
  const result = await moduleUnderTest.exports.operations.sendPrompt({ window: page.window, args: { prompt: 'not sent' } });
  assert.equal(result.ok, false);
  assert.match(result.error, /send button did not become available/i);
  assert.equal(page.clicked, 0);
});

test('missing editor fails without sending', async () => {
  const page = mockPage('missing');
  const result = await moduleUnderTest.exports.operations.sendPrompt({ window: page.window, args: { prompt: 'not sent' } });
  assert.equal(result.ok, false);
  assert.match(result.error, /prompt editor was not found/i);
  assert.equal(page.clicked, 0);
});

test('completed blank Octane reply returns an error instead of waiting forever', async () => {
  const page = mockPage('octane', { emptyReply: true });
  const result = await moduleUnderTest.exports.operations.sendPrompt({ window: page.window, args: { prompt: 'Reply with the completion tag.' } });
  assert.equal(result.ok, false);
  assert.match(result.error, /completed.*without readable text/i);
  assert.equal(page.clicked, 1);
  assert.ok(page.ticks >= 9);
});

test('Octane completion marker may precede text becoming visible', async () => {
  const page = mockPage('octane', { contentAfterTicks: 9 });
  const result = await moduleUnderTest.exports.operations.sendPrompt({ window: page.window, args: { prompt: 'slow render' } });
  assert.equal(result.ok, true);
  assert.equal(result.text, 'OCTANE_OK');
  assert.ok(page.ticks >= 9);
});

test('new anonymous chat recovers from a completed empty reply with one fresh conversation', async () => {
  const page = mockPage('octane', { emptyFirstReply: true });
  const prompt = '[User]\\nTest received.\\n[User]\\nReturn <DONE>';
  const result = await moduleUnderTest.exports.operations.sendPrompt({ window: page.window, args: { prompt, newChat: true } });
  assert.equal(result.ok, true);
  assert.equal(result.text, 'OCTANE_OK');
  assert.equal(page.clicked, 2);
  assert.equal(page.navigations, 2);
  assert.equal(page.entered, prompt);
});

test('anonymous guest retry is bounded and reports a second blank reply', async () => {
  const page = mockPage('octane', { emptyReply: true });
  const result = await moduleUnderTest.exports.operations.sendPrompt({ window: page.window, args: { prompt: '[User]\\nReturn <DONE>', newChat: true } });
  assert.equal(result.ok, false);
  assert.match(result.error, /completed.*without readable text/i);
  assert.match(result.error, /retry/i);
  assert.equal(page.clicked, 2);
  assert.equal(page.navigations, 2);
});

test('anonymous guest does not replay a failed reply into an existing conversation', async () => {
  const page = mockPage('octane', { emptyReply: true });
  const result = await moduleUnderTest.exports.operations.sendPrompt({ window: page.window, args: { prompt: 'Return <DONE>', newChat: false } });
  assert.equal(result.ok, false);
  assert.equal(page.clicked, 1);
  assert.equal(page.navigations, 0);
});
