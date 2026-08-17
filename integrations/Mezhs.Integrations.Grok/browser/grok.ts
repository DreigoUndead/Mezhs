const ORIGIN = "https://grok.com";

module.exports = {
  name: "Grok",
  homeUrl: ORIGIN + "/",

  async isAuthorized(window) {
    const cookies = await window.webContents.session.cookies.get({ url: ORIGIN });
    return cookies.some(cookie =>
      (cookie.name === "sso" || cookie.name === "sso-rw") &&
      Boolean(cookie.value)
    );
  },

  operations: {
    async newChat(context) {
      await context.window.loadURL(module.exports.homeUrl);
      return sendPrompt(context);
    },

    async send(context) {
      const chatUrl = String(context.args.chatUrl || "");
      if (!isGrokConversationUrl(chatUrl))
        throw new Error("Grok continuation URL is missing or invalid.");
      await context.window.loadURL(chatUrl);
      return sendPrompt(context);
    }
  }
};

function isGrokConversationUrl(value) {
  try {
    const url = new URL(value);
    return url.origin === ORIGIN &&
      (url.pathname !== "/" || Boolean(url.search) || Boolean(url.hash));
  } catch {
    return false;
  }
}

async function sendPrompt({ window, args }) {
  const prompt = JSON.stringify(String(args.prompt || ""));
  return window.webContents.executeJavaScript(`
    (async () => {
      const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
      const prompt = ${prompt};

      const visible = element => {
        if (!element) return false;
        const style = getComputedStyle(element);
        const rect = element.getBoundingClientRect();
        return style.display !== 'none' &&
          style.visibility !== 'hidden' &&
          rect.width > 0 &&
          rect.height > 0;
      };

      const findEditor = () => {
        const selectors = [
          'textarea',
          '[data-testid="text-input"]',
          'div[contenteditable="true"][role="textbox"]',
          '[contenteditable="true"][role="textbox"]',
          'div[contenteditable="true"]'
        ];
        for (const selector of selectors) {
          const matches = [...document.querySelectorAll(selector)];
          const editor = matches.find(visible);
          if (editor) return editor;
        }
        return null;
      };

      const latestResponse = () => {
        const markdown = [...document.querySelectorAll('.response-content-markdown')]
          .filter(visible);
        if (markdown.length)
          return markdown[markdown.length - 1];

        const lastReply = document.querySelector('#last-reply-container');
        if (!lastReply) return null;
        const bubbles = [...lastReply.querySelectorAll('.message-bubble')].filter(visible);
        return bubbles[bubbles.length - 1] || lastReply;
      };

      const editorDeadline = Date.now() + 30000;
      let editor = null;
      while (!editor && Date.now() < editorDeadline) {
        editor = findEditor();
        if (!editor) await sleep(250);
      }
      if (!editor)
        return { ok: false, error: 'Grok prompt editor was not found at ' + location.href };

      const beforeNode = latestResponse();
      const beforeText = beforeNode?.innerText?.trim() || '';
      const beforeMarkdownCount =
        document.querySelectorAll('.response-content-markdown').length;
      const beforeBubbleCount =
        document.querySelectorAll('#last-reply-container .message-bubble').length;
      editor.focus();
      if (editor.tagName === 'TEXTAREA') {
        editor.select();
        editor.value = '';
      } else {
        const selection = getSelection();
        const range = document.createRange();
        range.selectNodeContents(editor);
        selection.removeAllRanges();
        selection.addRange(range);
      }
      if (!document.execCommand('insertText', false, prompt))
        return { ok: false, error: 'Grok rejected prompt insertion.' };

      const findSendButton = () => {
        const selectors = [
          'button[data-testid="send-button"]',
          'button[aria-label="Send"]',
          'button[aria-label="Submit"]'
        ];
        for (const selector of selectors) {
          const matches = [...document.querySelectorAll(selector)];
          const button = matches.find(item => visible(item) && !item.disabled);
          if (button) return button;
        }
        return [...document.querySelectorAll('button')]
          .find(button =>
            visible(button) &&
            !button.disabled &&
            /^(send|submit|发送|提交)$/i.test(
              String(button.getAttribute('aria-label') || button.textContent || '').trim()
            )
          ) || null;
      };

      const sendDeadline = Date.now() + 30000;
      let sendButton = null;
      while (!sendButton && Date.now() < sendDeadline) {
        sendButton = findSendButton();
        if (!sendButton) await sleep(250);
      }
      if (!sendButton)
        return { ok: false, error: 'Grok send button did not become available.' };

      sendButton.click();

      let lastText = '';
      let stableSamples = 0;
      const responseDeadline = Date.now() + 180000;
      while (Date.now() < responseDeadline) {
        const node = latestResponse();
        const text = node?.innerText?.trim() || '';
        const markdownCount =
          document.querySelectorAll('.response-content-markdown').length;
        const bubbleCount =
          document.querySelectorAll('#last-reply-container .message-bubble').length;
        const changed = Boolean(text) && (
          markdownCount > beforeMarkdownCount ||
          bubbleCount > beforeBubbleCount ||
          (node && node !== beforeNode) ||
          text !== beforeText
        );
        if (changed && text === lastText)
          stableSamples++;
        else
          stableSamples = 0;
        lastText = text;

        const stop = [...document.querySelectorAll('button')]
          .find(button =>
            visible(button) &&
            /stop/i.test(String(button.getAttribute('aria-label') || button.textContent || ''))
          );

        if (changed && !stop && stableSamples >= 6)
          return { ok: true, text, chatUrl: location.href };

        await sleep(500);
      }

      return {
        ok: false,
        text: lastText,
        chatUrl: location.href,
        error: 'Grok response timed out.'
      };
    })()
  `, true).then(result => {
    if (!result?.ok)
      throw new Error(result?.error || "Grok browser request failed.");
    if (!isGrokConversationUrl(result.chatUrl))
      throw new Error("Grok did not return a valid conversation URL.");
    return result;
  });
}
