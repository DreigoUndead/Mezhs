// Grok browser module: main-process navigation/auth plus renderer-attached page operations.
const ORIGIN = "https://grok.com";
const MODES_ENDPOINT = ORIGIN + "/rest/modes";
const MODEL_SELECTOR = "button[aria-label='Model select']";

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
      const response = await fetch(MODES_ENDPOINT, {
        method: "GET",
        headers: { Accept: "application/json" },
        credentials: "include",
        cache: "no-store"
      });
      if (!response.ok)
        throw new Error(`Grok ${new URL(MODES_ENDPOINT).pathname} failed with HTTP ${response.status}.`);

      const modes = normalizeModes(await response.json());
      if (!args?.select) return modes;

      const requested = String(args.select).trim();
      const selected = modes.find(mode =>
        mode.id.toLocaleLowerCase() === requested.toLocaleLowerCase()
      );
      if (!selected)
        throw new Error(`Grok model '${requested}' is no longer available.`);

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
      const optionId = element => String(
        element?.getAttribute?.("data-value") ||
        element?.getAttribute?.("data-model") ||
        element?.getAttribute?.("data-mode") ||
        element?.getAttribute?.("value") ||
        ""
      ).trim();

      const pickerDeadline = Date.now() + 30000;
      let picker = null;
      while (!picker && Date.now() < pickerDeadline) {
        const candidate = document.querySelector(MODEL_SELECTOR);
        picker = visible(candidate) ? candidate : null;
        if (!picker) await sleep(250);
      }
      if (!picker)
        throw new Error("Grok model picker was not found.");

      HTMLElement.prototype.click.call(picker);
      const optionSelector = '[role="menuitem"], [role="option"], [data-radix-collection-item]';
      const optionDeadline = Date.now() + 5000;
      let match = null;
      while (!match && Date.now() < optionDeadline) {
        const options = [...document.querySelectorAll(optionSelector)].filter(visible);
        match = options.find(option => {
          const id = optionId(option);
          const name = text(option);
          return id.toLocaleLowerCase() === selected.id.toLocaleLowerCase() ||
            name.toLocaleLowerCase() === selected.name.toLocaleLowerCase();
        }) || null;
        if (!match) await sleep(100);
      }
      if (!match)
        throw new Error(`Grok model '${selected.name}' was not found in the model picker.`);

      HTMLElement.prototype.click.call(match);
      return true;
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

function normalizeModes(payload) {
  const candidates = [];
  collectModeCandidates(payload, candidates);
  const result = [];
  const seen = new Set();
  for (const item of candidates) {
    if (item.enabled === false ||
        item.available === false ||
        item.isAvailable === false ||
        item.hidden === true)
      continue;
    const mode = item.mode && typeof item.mode === "object" ? item.mode : item;
    const id = String(
      mode.modeId || mode.id || mode.value || mode.slug || mode.modelName || ""
    ).trim();
    const name = String(
      mode.displayName || mode.name || mode.title || mode.label || id
    ).replace(/\s+/g, " ").trim();
    const key = id.toLocaleLowerCase();
    if (!id || !name || seen.has(key)) continue;
    seen.add(key);
    result.push({ id, name });
  }
  if (result.length === 0)
    throw new Error("Grok /rest/modes returned no recognizable models.");
  return result;
}

function collectModeCandidates(value, result) {
  if (Array.isArray(value)) {
    for (const item of value) collectModeCandidates(item, result);
    return;
  }
  if (!value || typeof value !== "object") return;

  const mode = value.mode && typeof value.mode === "object" ? value.mode : value;
  const hasId = [mode.modeId, mode.id, mode.value, mode.slug, mode.modelName]
    .some(item => typeof item === "string" && item.trim());
  if (hasId) {
    result.push(value);
    return;
  }

  for (const child of Object.values(value))
    collectModeCandidates(child, result);
}

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
