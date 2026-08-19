// Grok browser module: API-backed model discovery plus native UI send.
// Grok's app-chat endpoint applies anti-bot rules that can reject a hand-written
// page fetch even when the same authenticated browser succeeds through the real UI.
const ORIGIN = "https://grok.com";
const MODES_ENDPOINT = ORIGIN + "/rest/modes";

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
      if (context.args.model)
        await context.page.invoke("selectModel", { model: context.args.model });
      const result = await context.page.invoke("sendPrompt", context.args);
      if (!isGrokConversationUrl(result?.chatUrl))
        throw new Error("Grok did not return a valid conversation URL.");
      return result;
    }
  },

  pageOperations: {
    async models() {
      return discoverModes();
    },

    async selectModel({ args, sleep }) {
      const requested = String(args.model || "").trim();
      if (!requested) return true;

      const modes = await discoverModes();
      const selected = modes.find(mode =>
        mode.id.toLocaleLowerCase() === requested.toLocaleLowerCase()
      );
      if (!selected)
        throw new Error(`Grok model '${requested}' is no longer available.`);

      const trigger = findModelTrigger(modes);
      if (!trigger)
        throw new Error("Grok model picker was not found.");

      const current = currentModelLabel(trigger);
      if (current && current.toLocaleLowerCase() === selected.name.toLocaleLowerCase())
        return true;

      if (trigger.getAttribute("aria-expanded") !== "true") {
        openPopover(trigger);
        const opened = await waitFor(
          () => trigger.getAttribute("aria-expanded") === "true",
          sleep,
          3000
        );
        if (!opened)
          throw new Error("Grok model picker did not open.");
      }

      const row = await waitFor(
        () => findModelRow(selected.name),
        sleep,
        3000
      );
      if (!row) {
        closeModelMenu(trigger);
        throw new Error(`Grok model '${selected.name}' was not found in the model picker.`);
      }
      if (row.matches?.(":disabled") || row.getAttribute?.("aria-disabled") === "true") {
        closeModelMenu(trigger);
        throw new Error(`Grok model '${selected.name}' is disabled for this account.`);
      }

      HTMLElement.prototype.click.call(row);
      const switched = await waitFor(
        () => currentModelLabel(trigger)?.toLocaleLowerCase() === selected.name.toLocaleLowerCase(),
        sleep,
        3000
      );
      closeModelMenu(trigger);
      if (!switched)
        throw new Error(`Grok model did not switch to '${selected.name}'.`);
      return true;
    },

    async sendPrompt({ args, sleep }) {
      const prompt = String(args.prompt || "");
      const editor = await waitFor(findEditor, sleep, 30000);
      if (!editor)
        throw new Error(`Grok prompt editor was not found at ${location.href}`);

      const before = responseSnapshot();
      HTMLElement.prototype.focus.call(editor);
      selectContents(editor);
      if (!document.execCommand("insertText", false, prompt))
        throw new Error("Grok rejected prompt insertion.");
      editor.dispatchEvent(new InputEvent("input", {
        bubbles: true,
        inputType: "insertText",
        data: prompt
      }));

      const send = await waitFor(findSendButton, sleep, 30000);
      if (!send)
        throw new Error("Grok send button did not become available.");
      HTMLElement.prototype.click.call(send);

      let lastText = "";
      let stableSamples = 0;
      const deadline = Date.now() + 180000;
      while (Date.now() < deadline) {
        const current = responseSnapshot();
        const changed = Boolean(current.text) && (
          current.count > before.count ||
          current.node !== before.node ||
          current.text !== before.text
        );
        if (changed && current.text === lastText)
          stableSamples++;
        else
          stableSamples = 0;
        lastText = current.text;

        if (changed && !findStopButton() && stableSamples >= 6) {
          const chatUrl = location.href;
          if (!isGrokConversationUrl(chatUrl))
            throw new Error(`Grok response completed without a conversation URL at ${chatUrl}.`);
          return { text: current.text, chatUrl };
        }
        await sleep(500);
      }

      throw new Error(`Grok response timed out at ${location.href}.`);
    }
  }
};

async function discoverModes() {
  const response = await fetch(MODES_ENDPOINT, {
    method: "POST",
    headers: {
      Accept: "application/json",
      "Content-Type": "application/json"
    },
    body: "{}",
    credentials: "include",
    cache: "no-store"
  });
  if (!response.ok)
    throw new Error(`Grok /rest/modes failed with HTTP ${response.status}.`);
  return normalizeModes(await response.json());
}

function findModelTrigger(modes = []) {
  const exact = document.getElementById("model-select-trigger") ||
    document.querySelector('button[aria-label="Model select"]') ||
    [...document.querySelectorAll('button[aria-haspopup="menu"]')]
      .find(button => String(button.id || "").toLocaleLowerCase().includes("model"));
  if (exact) return exact;

  const buttons = [...document.querySelectorAll("button")].filter(visible);
  const labelled = buttons.find(button =>
    /model/i.test(String(button.getAttribute?.("aria-label") || ""))
  );
  if (labelled) return labelled;

  const names = new Set(modes
    .map(mode => normalizeLabel(mode.name))
    .filter(Boolean));
  return buttons.find(button => names.has(normalizeLabel(button.textContent))) || null;
}

function currentModelLabel(trigger) {
  if (!trigger) return null;
  const label = trigger.querySelector("span.truncate.font-semibold") ||
    trigger.querySelector("span.font-semibold");
  return normalizeLabel(label?.textContent || trigger.textContent) || null;
}

function normalizeLabel(value) {
  return String(value || "").replace(/\s+/g, " ").trim();
}

function openPopover(element) {
  const rect = element.getBoundingClientRect();
  const clientX = rect.left + rect.width / 2;
  const clientY = rect.top + rect.height / 2;
  const base = {
    bubbles: true,
    cancelable: true,
    composed: true,
    view: window,
    clientX,
    clientY
  };
  element.dispatchEvent(new PointerEvent("pointerdown", {
    ...base,
    pointerType: "mouse",
    pointerId: 1,
    isPrimary: true,
    button: 0,
    buttons: 1
  }));
  element.dispatchEvent(new MouseEvent("mousedown", { ...base, button: 0, buttons: 1 }));
  element.dispatchEvent(new PointerEvent("pointerup", {
    ...base,
    pointerType: "mouse",
    pointerId: 1,
    isPrimary: true,
    button: 0,
    buttons: 0
  }));
  element.dispatchEvent(new MouseEvent("mouseup", { ...base, button: 0, buttons: 0 }));
  element.dispatchEvent(new MouseEvent("click", { ...base, button: 0, buttons: 0 }));
}

function findModelRow(name) {
  const expected = String(name || "").trim().toLocaleLowerCase();
  if (!expected) return null;

  const roles = ["menuitem", "menuitemradio", "option"];
  for (const role of roles) {
    for (const row of document.querySelectorAll(`[role="${role}"]`)) {
      const label = row.querySelector("span.font-semibold");
      if (String(label?.textContent || "").trim().toLocaleLowerCase() === expected)
        return row;
    }
  }

  for (const label of document.querySelectorAll("span.font-semibold")) {
    if (String(label.textContent || "").trim().toLocaleLowerCase() !== expected)
      continue;
    const row = label.closest(
      '[role="menuitem"], [role="menuitemradio"], [role="option"], [data-radix-collection-item], button'
    );
    if (!row || row.id === "model-select-trigger" || row.getAttribute("aria-haspopup") === "menu")
      continue;
    return row;
  }
  return null;
}

function closeModelMenu(trigger) {
  if (!trigger || trigger.getAttribute("aria-expanded") !== "true") return;
  trigger.dispatchEvent(new KeyboardEvent("keydown", {
    key: "Escape",
    bubbles: true,
    cancelable: true
  }));
}

function visible(element) {
  if (!element) return false;
  const style = getComputedStyle(element);
  const rect = element.getBoundingClientRect();
  return style.display !== "none" &&
    style.visibility !== "hidden" &&
    rect.width > 0 &&
    rect.height > 0;
}

function findEditor() {
  const selectors = [
    'div[data-testid="chat-input"] div[contenteditable="true"]',
    '.tiptap.ProseMirror[contenteditable="true"]',
    'div[contenteditable="true"][translate="no"]',
    'div[contenteditable="true"][role="textbox"]',
    "textarea"
  ];
  for (const selector of selectors) {
    const editor = [...document.querySelectorAll(selector)].find(visible);
    if (editor) return editor;
  }
  return null;
}

function selectContents(editor) {
  if (editor.tagName === "TEXTAREA") {
    HTMLTextAreaElement.prototype.select.call(editor);
    return;
  }
  const selection = getSelection();
  const range = document.createRange();
  range.selectNodeContents(editor);
  selection.removeAllRanges();
  selection.addRange(range);
}

function findSendButton() {
  const selectors = [
    'button[data-testid="chat-submit"]',
    'button[type="submit"][aria-label="Submit"]',
    'button[aria-label="Submit"]',
    'button[aria-label="Send"]'
  ];
  for (const selector of selectors) {
    const button = [...document.querySelectorAll(selector)]
      .find(item => visible(item) && !item.matches(":disabled"));
    if (button) return button;
  }
  return null;
}

function findStopButton() {
  return [...document.querySelectorAll("button")].find(button =>
    visible(button) && /stop/i.test(String(
      button.getAttribute("aria-label") || button.textContent || ""
    ))
  ) || null;
}

function responseSnapshot() {
  const selectors = [
    ".response-content-markdown",
    '[data-message-author="grok"]',
    "#last-reply-container .message-bubble",
    "#last-reply-container"
  ];
  const nodes = [];
  for (const selector of selectors)
    for (const node of document.querySelectorAll(selector))
      if (visible(node) && !nodes.includes(node)) nodes.push(node);
  const node = nodes[nodes.length - 1] || null;
  return {
    count: nodes.length,
    node,
    text: String(node?.textContent || "").trim()
  };
}

async function waitFor(predicate, sleep, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const value = predicate();
      if (value) return value;
    } catch { }
    await sleep(50);
  }
  return null;
}

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
