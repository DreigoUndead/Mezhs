const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

function loadModule() {
  const module = { exports: {} };
  const source = fs.readFileSync(path.join(__dirname,
    '../integrations/Mezhs.Integrations.Gemini/browser/gemini.ts'), 'utf8');
  new Function('module', source)(module);
  return module.exports;
}

function browser({ cookies = [], signedOut = false, url = 'https://gemini.google.com/app' } = {}) {
  return {
    async loadURL(target) { url = target; },
    webContents: {
      getURL: () => url,
      session: { cookies: { get: async () => cookies } },
      async executeJavaScript(script) {
        // Compile every emitted script, including the nested response URL regex.
        new Function(script);
        if (script.includes('querySelectorAll(\'a[href]\')')) {
          const document = { querySelectorAll: () => signedOut
            ? [{ href: 'https://accounts.google.com/ServiceLogin?continue=gemini' }] : [] };
          return new Function('document', `return (${script})`)(document);
        }
        return { ok: true, text: 'reply', chatUrl: 'https://gemini.google.com/app/test' };
      }
    }
  };
}

const accountCookies = [{ name: 'SID', value: 'test' }, { name: 'HSID', value: 'test' }];

test('account authorization requires Google authentication cookies and the Gemini app', async () => {
  const gemini = loadModule();
  assert.equal(await gemini.isAuthorized(browser()), false);
  assert.equal(await gemini.isAuthorized(browser({ cookies: [{ name: 'NID', value: 'preferences' }] })), false);
  assert.equal(await gemini.isAuthorized(browser({ cookies: accountCookies })), true);
  assert.equal(await gemini.isAuthorized(browser({ cookies: accountCookies, signedOut: true })), false);
  assert.equal(await gemini.isAuthorized(browser({ cookies: accountCookies, url: 'https://accounts.google.com/' })), false);
});

test('account send refuses signed-out state instead of sending anonymously', async () => {
  const gemini = loadModule();
  await assert.rejects(gemini.operations.sendPrompt({ window: browser(),
    args: { newChat: true, requireAccount: true, prompt: 'test' } }), /authorization is required/);
});

test('guest send remains anonymous and account send returns provider conversation URL', async () => {
  for (const requireAccount of [false, true]) {
    const gemini = loadModule();
    const result = await gemini.operations.sendPrompt({
      window: browser({ cookies: requireAccount ? accountCookies : [] }),
      args: { newChat: true, requireAccount, prompt: 'test' }
    });
    assert.equal(result.text, 'reply');
    assert.equal(result.chatUrl, 'https://gemini.google.com/app/test');
  }
});

test('Latvian Gemini composer sends through its localized button', async () => {
  const gemini = loadModule();
  const window = browser();
  let sent = false;
  let clock = 0;
  const editor = { focus() {}, dispatchEvent() {} };
  const button = { disabled: false, click() { sent = true; } };
  window.webContents.executeJavaScript = script => new Function(
    'document', 'location', 'InputEvent', 'setTimeout', 'Date', `return (${script})`
  )({
    querySelector(selector) {
      if (selector.includes('rich-textarea')) return editor;
      if (selector.includes('Sūtīt ziņojumu')) return button;
      return null;
    },
    querySelectorAll() { return sent ? [{ innerText: 'MEZHS_GEMINI_ACCOUNT_OK' }] : []; },
    execCommand() { return true; }
  }, { origin: 'https://gemini.google.com', pathname: '/app/test' },
  class InputEvent {}, callback => queueMicrotask(callback), { now: () => clock += 1000 });
  const result = await gemini.operations.sendPrompt({ window, args: { prompt: 'test' } });
  assert.equal(sent, true);
  assert.equal(result.ok, true);
  assert.equal(result.text, 'MEZHS_GEMINI_ACCOUNT_OK');
});
