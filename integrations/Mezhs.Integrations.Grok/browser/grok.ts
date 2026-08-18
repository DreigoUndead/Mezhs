// Grok browser module: authenticated provider protocol through the renderer session.
const ORIGIN = "https://grok.com";
const MODES_ENDPOINT = ORIGIN + "/rest/modes";
const CHAT_ENDPOINT = ORIGIN + "/rest/app-chat/conversations/new";
const STATSIG_ID =
  "ZTpUeXBlRXJyb3I6IENhbm5vdCByZWFkIHByb3BlcnRpZXMgb2YgdW5kZWZpbmVkIChyZWFkaW5nICdjaGlsZE5vZGVzJyk=";

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
      return context.page.invoke("chat", context.args);
    }
  },

  pageOperations: {
    async models() {
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
    },

    async chat({ args }) {
      const payload = buildChatPayload(
        String(args.prompt || ""),
        String(args.model || "auto")
      );
      const response = await fetch(CHAT_ENDPOINT, {
        method: "POST",
        headers: {
          Accept: "*/*",
          "Content-Type": "application/json",
          "x-statsig-id": STATSIG_ID,
          "x-xai-request-id": crypto.randomUUID()
        },
        body: JSON.stringify(payload),
        credentials: "include",
        cache: "no-store"
      });
      const body = await response.text();
      if (!response.ok)
        throw new Error(
          `Grok /rest/app-chat/conversations/new failed with HTTP ${response.status}: ${body.slice(0, 1000)}`
        );
      return parseChatResponse(body);
    }
  }
};

function buildChatPayload(message, modeId) {
  return {
    collectionIds: [],
    disabledConnectorIds: [],
    disableMemory: false,
    disableSearch: false,
    disableSelfHarmShortCircuit: false,
    disableTextFollowUps: false,
    enableImageGeneration: true,
    enableImageStreaming: true,
    enableSideBySide: true,
    fileAttachments: [],
    forceConcise: false,
    forceSideBySide: false,
    imageAttachments: [],
    imageGenerationCount: 2,
    isAsyncChat: false,
    linkQuery: false,
    message,
    modeId,
    responseMetadata: {},
    returnImageBytes: false,
    returnRawGrokInXaiRequest: false,
    sendFinalMetadata: true,
    temporary: false
  };
}

function parseChatResponse(body) {
  const finalText = [];
  const fallbackText = [];
  let conversationId = null;

  for (const rawLine of String(body || "").split(/\r?\n/)) {
    let line = rawLine.trim();
    if (!line || line.startsWith("event:")) continue;
    if (line.startsWith("data:")) line = line.slice(5).trim();
    if (!line || line === "[DONE]" || !line.startsWith("{")) continue;

    let frame;
    try {
      frame = JSON.parse(line);
    } catch {
      continue;
    }

    if (frame?.error) {
      const message = frame.error.message || frame.error.error || JSON.stringify(frame.error);
      throw new Error(`Grok chat failed: ${message}`);
    }

    conversationId ||= findConversationId(frame);
    const value = frame?.result?.response || frame?.result;
    if (!value || typeof value !== "object" || value.isThinking === true) continue;

    const token = value.token ?? value.message;
    if (token == null) continue;
    const text = String(token);
    if (value.messageTag === "final")
      finalText.push(text);
    else if (!value.messageTag && String(value.sender || "").toLowerCase() === "assistant")
      fallbackText.push(text);
  }

  const text = (finalText.length ? finalText : fallbackText).join("").trim();
  if (!text)
    throw new Error("Grok returned no final response text.");

  return {
    text,
    chatUrl: conversationId ? `${ORIGIN}/c/${conversationId}` : null
  };
}

function findConversationId(frame) {
  const values = [
    frame?.conversationId,
    frame?.conversation_id,
    frame?.result?.conversationId,
    frame?.result?.conversation_id,
    frame?.result?.conversation?.id
  ];
  const value = values.find(item => typeof item === "string" && item.trim());
  return value ? value.trim() : null;
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
