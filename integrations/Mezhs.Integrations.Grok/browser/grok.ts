// Grok browser module: main-process navigation/auth plus renderer-attached DOM operations.
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
    async getModels(context) {
      await context.window.loadURL(module.exports.homeUrl);
      return context.page.invoke("models", {});
    },

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
  },

  pageOperations: {
    async models({ args, sleep }) {
      const visible = element => {
        if (!element) return false;
        const style = getComputedStyle(element);
        const rect = element.getBoundingClientRect();
        return style.display !== "none" &&
          style.visibility !== "hidden" &&
          rect.width > 0 &&
          rect.height > 0;
      };
      const text = element => String(
        element?.getAttribute?.("aria-label") ||
        element?.getAttribute?.("title") ||
        element?.textContent ||
        ""
      ).replace(/\s+/g, " ").trim();
      const looksLikeModel = value =>
        /(grok|model|auto|fast|expert|heavy|reason|think)/i.test(value);
      const optionId = element => String(
        element?.getAttribute?.("data-value") ||
        element?.getAttribute?.("data-model") ||
        element?.getAttribute?.("value") ||
        text(element)
      ).trim();
      const readOptions = () => {
        const nodes = [...document.querySelectorAll(
          '[role="menuitem"], [role="option"], [data-radix-collection-item]'
        )].filter(visible);
        const result = [];
        const seen = new Set();
        for (const node of nodes) {
          const name = text(node);
          const id = optionId(node);
          if (!name || !id || !looksLikeModel(name) || seen.has(id)) continue;
          seen.add(id);
          result.push({ id, name, node });
        }
        return result;
      };
      const candidateSelectors = [
        'button[aria-label*="model" i]',
        'button[title*="model" i]',
        'button[data-testid*="model" i]',
        '[data-testid*="model" i] button',
        'button[aria-haspopup="listbox"]',
        'button[aria-haspopup="menu"]'
      ];
      const findCandidates = () => {
        const result = [];
        const seen = new Set();
        for (const selector of candidateSelectors) {
          for (const candidate of document.querySelectorAll(selector)) {
            if (!visible(candidate) || seen.has(candidate)) continue;
            const label = text(candidate);
            if (!selector.includes("aria-haspopup") || looksLikeModel(label)) {
              seen.add(candidate);
              result.push(candidate);
            }
          }
        }
        return result;
      };

      let candidates = [];
      const pickerDeadline = Date.now() + 30000;
      while (candidates.length === 0 && Date.now() < pickerDeadline) {
        candidates = findCandidates();
        if (candidates.length === 0) await sleep(250);
      }

      for (const picker of candidates) {
        HTMLElement.prototype.click.call(picker);
        let options = [];
        for (let i = 0; i < 30 && options.length === 0; i++) {
          await sleep(100);
          options = readOptions();
        }
        if (options.length === 0) {
          HTMLElement.prototype.click.call(picker);
          continue;
        }

        const requested = String(args?.select || "").trim();
        if (requested) {
          const normalized = requested.toLocaleLowerCase();
          const match = options.find(option =>
            option.id.toLocaleLowerCase() === normalized ||
            option.name.toLocaleLowerCase() === normalized
          );
          if (!match)
            throw new Error(`Grok model '${requested}' is no longer available.`);
          HTMLElement.prototype.click.call(match.node);
          return true;
        }

        HTMLElement.prototype.click.call(picker);
        return options.map(({ id, name }) => ({ id, name }));
      }

      if (args?.select)
        throw new Error("Grok model picker was not found.");
      return [];
    },

    async sendPrompt({ args, sleep }) {
      const prompt = String(args.prompt || "");

      const visible = element => {
        if (!element) return false;
        const style = getComputedStyle(element);
        const rect = element.getBoundingClientRect();
        return style.display !== "none" &&
          style.visibility !== "hidden" &&
          rect.width > 0 &&
          rect.height > 0;
      };

      const findEditor = () => {
        const selectors = [
          "textarea",
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
        const markdown = [...document.querySelectorAll(".response-content-markdown")]
          .filter(visible);
        if (markdown.length)
          return markdown[markdown.length - 1];

        const lastReply = document.querySelector("#last-reply-container");
        if (!lastReply) return null;
        const bubbles = [...lastReply.querySelectorAll(".message-bubble")].filter(visible);
        return bubbles[bubbles.length - 1] || lastReply;
      };

      const editorDeadline = Date.now() + 30000;
      let editor = null;
      while (!editor && Date.now() < editorDeadline) {
        editor = findEditor();
        if (!editor) await sleep(250);
      }
      if (!editor)
        throw new Error(`Grok prompt editor was not found at ${location.href}`);

      const beforeNode = latestResponse();
      const beforeText = beforeNode?.textContent?.trim() || "";
      const beforeMarkdownCount =
        document.querySelectorAll(".response-content-markdown").length;
      const beforeBubbleCount =
        document.querySelectorAll("#last-reply-container .message-bubble").length;
      HTMLElement.prototype.focus.call(editor);
      if (editor.tagName === "TEXTAREA") {
        HTMLTextAreaElement.prototype.select.call(editor);
      } else {
        const selection = getSelection();
        const range = document.createRange();
        range.selectNodeContents(editor);
        selection.removeAllRanges();
        selection.addRange(range);
      }
      if (!document.execCommand("insertText", false, prompt))
        throw new Error("Grok rejected prompt insertion.");

      const findSendButton = () => {
        const selectors = [
          'button[data-testid="send-button"]',
          'button[aria-label="Send"]',
          'button[aria-label="Submit"]'
        ];
        for (const selector of selectors) {
          const matches = [...document.querySelectorAll(selector)];
          const button = matches.find(item => visible(item) && !item.matches(":disabled"));
          if (button) return button;
        }
        return [...document.querySelectorAll("button")]
          .find(button =>
            visible(button) &&
            !button.matches(":disabled") &&
            /^(send|submit|发送|提交)$/i.test(
              String(button.getAttribute("aria-label") || button.textContent || "").trim()
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
        throw new Error("Grok send button did not become available.");

      HTMLElement.prototype.click.call(sendButton);

      let lastText = "";
      let stableSamples = 0;
      const responseDeadline = Date.now() + 180000;
      while (Date.now() < responseDeadline) {
        const node = latestResponse();
        const text = node?.textContent?.trim() || "";
        const markdownCount =
          document.querySelectorAll(".response-content-markdown").length;
        const bubbleCount =
          document.querySelectorAll("#last-reply-container .message-bubble").length;
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

        const stop = [...document.querySelectorAll("button")]
          .find(button =>
            visible(button) &&
            /stop/i.test(String(button.getAttribute("aria-label") || button.textContent || ""))
          );

        if (changed && !stop && stableSamples >= 6)
          return { text, chatUrl: location.href };

        await sleep(500);
      }

      throw new Error(`Grok response timed out at ${location.href}.`);
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

async function sendPrompt(context) {
  if (context.args.model)
    await context.page.invoke("models", { select: context.args.model });
  const result = await context.page.invoke("sendPrompt", context.args);
  if (!isGrokConversationUrl(result?.chatUrl))
    throw new Error("Grok did not return a valid conversation URL.");
  return result;
}
