const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const vm = require('node:vm');
const fs = require('node:fs');

const integration = require(path.join(__dirname, '..', 'integrations',
  'Mezhs.Integrations.ChatGpt', 'browser', 'modules', 'entry.js'));

function mockPage(mode, options = {}) {
  let clicked = 0;
  let submitted = false;
  let navigations = 0;
  let invokes = 0;
  let ticks = 0;
  let entered = '';
  let enabled = false;
  let loginVisible = Boolean(options.loginBeforeClick);
  const octane = mode === 'octane';
  const editorExists = mode !== 'missing';

  class TextArea {
    set value(value) { entered = value; }
    get value() { return entered; }
  }
  const send = {
    disabled: false,
    getAttribute: name => name === 'aria-disabled' && !enabled ? 'true' : null,
    click() {
      clicked++;
      if (options.loginAfterClick) loginVisible = true;
      if (options.clickThrows) throw new Error('Click failed after invocation');
      if (!options.noSubmission) submitted = true;
    }
  };
  const form = { querySelector: selector => selector.includes('Send message') ? send : null };
  const editor = new TextArea();
  editor.tagName = 'TEXTAREA';
  editor.focus = () => {};
  editor.closest = () => octane ? form : null;
  editor.dispatchEvent = event => {
    if (event.type === 'input') {
      if (!options.neverEnable) enabled = true;
      if (options.loginAfterInput) loginVisible = true;
    }
  };
  const assistant = octane
    ? {
        hasAttribute: name => name === 'data-message-complete' &&
          !options.neverComplete && ticks >= (options.completeAfterTicks ?? 0),
        children: [{ innerText: 'ChatGPT said:' },
          { get innerText() {
            return ticks >= (options.contentAfterTicks ?? 2) && !options.emptyReply
              ? 'OCTANE_OK' : '';
          } }]
      }
    : { get innerText() {
        return ticks >= (options.contentAfterTicks ?? 0) ? 'LEGACY_OK' : '';
      } };

  const document = {
    querySelector(selector) {
      if (selector.includes('#prompt-textarea')) return editorExists ? editor : null;
      if (selector.includes('send-button')) return octane ? null : send;
      if (selector.includes('stop-button')) return submitted && options.neverComplete ? {} : null;
      return null;
    },
    querySelectorAll(selector) {
      if (selector.includes('[role="dialog"]')) return loginVisible
        ? [{ innerText: 'Log in to continue chatting' }] : [];
      if (selector.includes('[data-message-role="assistant"]')) return submitted && octane ? [assistant] : [];
      if (selector === '[data-message-author-role="assistant"]') return submitted && !octane ? [assistant] : [];
      if (selector.includes('[data-message-role="user"]')) return submitted ? [{}] : [];
      return [];
    }
  };
  const pageOperation = vm.runInNewContext(
    `({${integration.pageOperations.sendPrompt.toString()}}).sendPrompt`, {
      document,
      HTMLTextAreaElement: TextArea,
      HTMLInputElement: TextArea,
      Event: class { constructor(type) { this.type = type; } },
      InputEvent: class { constructor(type) { this.type = type; } }
    }
  );
  const window = {
    loadURL: async () => { navigations++; submitted = false; ticks = 0; }
  };
  const page = {
    invoke: async (operation, args) => {
      invokes++;
      assert.equal(operation, 'sendPrompt');
      return pageOperation({ args, sleep: async () => { ticks++; } });
    }
  };
  return {
    window, page,
    get navigations() { return navigations; },
    get clicked() { return clicked; },
    get invokes() { return invokes; },
    get entered() { return entered; },
    get ticks() { return ticks; }
  };
}

function send(page, prompt = 'test', newChat = false) {
  return integration.operations.sendPrompt({
    window: page.window, page: page.page, args: { prompt, newChat }
  });
}

test('Octane sends once through preload and waits for completed, readable reply', async () => {
  const page = mockPage('octane');
  const result = await send(page, 'hello', true);
  assert.equal(page.entered, 'hello');
  assert.equal(page.clicked, 1);
  assert.equal(page.invokes, 1);
  assert.equal(page.navigations, 1);
  assert.equal(result.ok, true);
  assert.equal(result.text, 'OCTANE_OK');
  assert.equal(result.submissionAttempted, true);
  assert.ok(page.ticks >= 2);
});

test('legacy composer and reply extraction work through preload', async () => {
  const page = mockPage('legacy');
  const result = await send(page, 'legacy');
  assert.equal(page.clicked, 1);
  assert.equal(result.ok, true);
  assert.equal(result.text, 'LEGACY_OK');
});

test('missing editor fails before submission', async () => {
  const page = mockPage('missing');
  const result = await send(page);
  assert.equal(result.errorCode, 'guest_editor_unavailable');
  assert.equal(result.submissionAttempted, false);
  assert.equal(page.clicked, 0);
  assert.equal(page.ticks, 120);
});

test('disabled send button fails before submission', async () => {
  const page = mockPage('octane', { neverEnable: true });
  const result = await send(page);
  assert.equal(result.errorCode, 'guest_send_unavailable');
  assert.equal(result.submissionAttempted, false);
  assert.equal(page.clicked, 0);
});

test('completed blank Octane reply fails and never retries a new chat', async () => {
  const page = mockPage('octane', { emptyReply: true });
  const result = await send(page, 'Never replay me', true);
  assert.equal(result.errorCode, 'empty_guest_reply');
  assert.equal(result.submissionAttempted, true);
  assert.equal(page.clicked, 1);
  assert.equal(page.invokes, 1);
  assert.equal(page.navigations, 1);
});

test('Octane completion marker can precede visible text', async () => {
  const page = mockPage('octane', { contentAfterTicks: 9 });
  const result = await send(page, 'slow render');
  assert.equal(result.ok, true);
  assert.equal(result.text, 'OCTANE_OK');
  assert.ok(page.ticks >= 9);
  assert.equal(page.clicked, 1);
});

test('no detectable submission times out as uncertain delivery', async () => {
  const page = mockPage('octane', { noSubmission: true });
  const result = await send(page, 'uncertain', true);
  assert.equal(result.errorCode, 'guest_submission_timeout');
  assert.equal(result.submissionAttempted, true);
  assert.match(result.error, /uncertain/i);
  assert.equal(page.clicked, 1);
  assert.equal(page.invokes, 1);
  assert.equal(page.navigations, 1);
});

test('Octane reply that starts but never completes times out', async () => {
  const page = mockPage('octane', { neverComplete: true });
  const result = await send(page);
  assert.equal(result.errorCode, 'guest_reply_timeout');
  assert.equal(result.submissionAttempted, true);
  assert.equal(page.clicked, 1);
  assert.equal(page.ticks, 240);
});

test('legacy reply that keeps streaming cannot complete on stable text alone', async () => {
  const page = mockPage('legacy', { neverComplete: true });
  const result = await send(page);
  assert.equal(result.errorCode, 'guest_reply_timeout');
  assert.equal(page.clicked, 1);
});

test('visible login requirement before submission is reported accurately', async () => {
  const page = mockPage('octane', { loginAfterInput: true });
  const result = await send(page);
  assert.equal(result.errorCode, 'guest_login_required');
  assert.equal(result.submissionAttempted, false);
  assert.equal(page.clicked, 0);
});

test('visible login requirement after clicking Send is not retried', async () => {
  const page = mockPage('octane', { loginAfterClick: true, noSubmission: true });
  const result = await send(page, 'once', true);
  assert.equal(result.errorCode, 'guest_login_required');
  assert.equal(result.submissionAttempted, true);
  assert.equal(page.clicked, 1);
  assert.equal(page.invokes, 1);
  assert.equal(page.navigations, 1);
});

test('missing editor and no visible login wall must not fabricate force_login', async () => {
  const page = mockPage('missing');
  const result = await send(page);
  assert.notEqual(result.errorCode, 'guest_login_required');
});

test('a failed observation never replays an existing conversation', async () => {
  const page = mockPage('octane', { emptyReply: true });
  const result = await send(page, 'Return <DONE>', false);
  assert.equal(result.ok, false);
  assert.equal(page.clicked, 1);
  assert.equal(page.invokes, 1);
  assert.equal(page.navigations, 0);
});

test('guest orchestrator uses the normal named preload operation without injected JS or fetch interception', () => {
  const source = fs.readFileSync(path.join(__dirname, '..', 'integrations',
    'Mezhs.Integrations.ChatGpt', 'browser', 'modules', 'guest.js'), 'utf8');
  assert.doesNotMatch(source, /executeJavaScript|globalThis\.fetch/);
  assert.equal(typeof integration.pageOperations.sendPrompt, 'function');
  assert.match(source, /context\.page\.invoke\(['"]sendPrompt['"]/);
});
