module.exports = {
  name: "Gemini",
  homeUrl: "https://gemini.google.com/app",

  async isAuthorized(window) {
    const url = new URL(window.webContents.getURL());
    if (url.origin !== "https://gemini.google.com" || !/^\/app(?:\/|$)/.test(url.pathname))
      return false;
    const cookies = await window.webContents.session.cookies.get({ url: module.exports.homeUrl });
    // Google documents SID and HSID as account authentication cookies.
    if (!["SID", "HSID"].every(name => cookies.some(cookie => cookie.name === name && cookie.value)))
      return false;
    // Expired sessions may retain cookies while the app displays its sign-in link.
    return window.webContents.executeJavaScript(`![...document.querySelectorAll('a[href]')].some(a => {
      const url = new URL(a.href);
      return url.hostname === 'accounts.google.com' && url.pathname === '/ServiceLogin';
    })`);
  },

  operations: {
    async sendPrompt({ window, args }) {
      if (args.newChat)
        await window.loadURL(module.exports.homeUrl);
      if (args.requireAccount && !await module.exports.isAuthorized(window))
        throw new Error("Gemini account authorization is required. Sign in again before sending.");
      const prompt = JSON.stringify(String(args.prompt || ""));
      return window.webContents.executeJavaScript(`
        (async () => {
          const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
          const prompt = ${prompt};
          const assistantSelector = '.model-response-text, message-content, [data-test-id="model-response"], .response-content';
          const beforeCount = document.querySelectorAll(assistantSelector).length;
          let editor = null;
          const editorDeadline = Date.now() + 30000;
          while (!editor && Date.now() < editorDeadline) {
            editor = document.querySelector(
              'rich-textarea .ql-editor, .ql-editor[contenteditable="true"], [contenteditable="true"][role="textbox"], textarea'
            );
            if (!editor) await sleep(250);
          }
          if (!editor) return { ok: false, error: 'Gemini prompt editor was not found at ' + location.href };
          editor.focus();
          document.execCommand('selectAll', false, null);
          document.execCommand('insertText', false, prompt);
          editor.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: prompt }));

          let sendButton = null;
          const sendDeadline = Date.now() + 30000;
          while (Date.now() < sendDeadline) {
            sendButton = document.querySelector(
              'button[aria-label*="Send"], button[aria-label="Sūtīt ziņojumu"], button.send-button, button[mattooltip*="Send"]'
            );
            if (sendButton && !sendButton.disabled) break;
            await sleep(250);
          }
          if (!sendButton || sendButton.disabled)
            return { ok: false, error: 'Gemini send button did not become available.' };
          sendButton.click();

          let lastText = '';
          let stableSamples = 0;
          while (true) {
            const messages = document.querySelectorAll(assistantSelector);
            const latest = messages[messages.length - 1];
            const text = latest?.innerText?.trim() || '';
            const stopButton = document.querySelector('button[aria-label*="Stop"], button[mattooltip*="Stop"]');
            if (messages.length > beforeCount && text && text === lastText) stableSamples++;
            else stableSamples = 0;
            lastText = text;
            if (text && !stopButton && stableSamples >= 6)
              return { ok: true, text, chatUrl: /^\\/app\\/[^/]+$/.test(location.pathname) ? location.origin + location.pathname : null };
            await sleep(500);
          }
        })()
      `, true);
    }
  }
};
