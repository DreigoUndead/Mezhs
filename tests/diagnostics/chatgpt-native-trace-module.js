const ORIGIN = "https://chatgpt.com";
const CONVERSATION_PATH = "/backend-api/f/conversation";

const PROMPT_EDITOR_SELECTOR = [
  '#prompt-textarea',
  'textarea[name="prompt-textarea"]',
  '[data-testid="prompt-textarea"]',
  '.ProseMirror[contenteditable="true"]',
  '[contenteditable="true"][role="textbox"]',
  '[contenteditable="true"][data-virtualkeyboard="true"]'
].join(", ");

const SENSITIVE_HEADERS = [
  "openai-sentinel-chat-requirements-token",
  "openai-sentinel-chat-requirements-prepare-token",
  "openai-sentinel-turnstile-token",
  "openai-sentinel-proof-token",
  "openai-sentinel-so-token",
  "openai-sentinel-token",
  "oai-telemetry",
  "x-conduit-token",
  "x-oai-is-client-observation"
];

const SAFE_HEADERS = [
  "x-openai-web-frontend",
  "oai-session-id",
  "user-agent",
  "sec-ch-ua",
  "sec-ch-ua-platform"
];

module.exports = {
  name: "ChatGPT Native Trace",
  homeUrl: ORIGIN + "/",

  async isAuthorized(window) {
    try {
      const response = await window.webContents.session.fetch(ORIGIN + "/api/auth/session", {
        credentials: "include",
        cache: "no-store"
      });
      if (!response.ok) return false;
      const value = await response.json();
      return Boolean(value?.accessToken);
    } catch {
      return false;
    }
  },

  operations: {
    async traceNativeSend({ window, args, sleep }) {
      const prompt = String(args?.prompt || "").trim();
      if (!prompt)
        throw new Error("A trace prompt is required.");

      await window.loadURL(ORIGIN + "/");
      window.show();
      window.focus();

      return captureNativeConversationRequest(window, prompt, sleep);
    }
  }
};

async function captureNativeConversationRequest(window, prompt, sleep) {
  const debug = window.webContents.debugger;
  if (!debug ||
      typeof debug.isAttached !== "function" ||
      typeof debug.attach !== "function" ||
      typeof debug.sendCommand !== "function") {
    throw new Error("Electron debugger API is unavailable.");
  }

  const attachedByTrace = !debug.isAttached();
  if (attachedByTrace)
    debug.attach("1.3");

  const extraInfoByRequestId = new Map();
  let resolveRequest;
  const requestSeen = new Promise(resolve => {
    resolveRequest = resolve;
  });

  const onMessage = (_event, method, params) => {
    if (method === "Network.requestWillBeSentExtraInfo" && params?.requestId) {
      extraInfoByRequestId.set(params.requestId, params);
      return;
    }

    if (method !== "Network.requestWillBeSent")
      return;

    const request = params?.request;
    if (!isConversationRequest(request))
      return;

    resolveRequest(params);
  };

  debug.on("message", onMessage);

  try {
    await debug.sendCommand("Network.enable", { maxPostDataSize: 1024 * 1024 });
    await debug.sendCommand("Debugger.enable");
    try {
      await debug.sendCommand("Debugger.setAsyncCallStackDepth", { maxDepth: 32 });
    } catch {
      // Some Chromium revisions do not expose this command.
    }

    await triggerNativeSend(window, prompt);

    const event = await withTimeout(
      requestSeen,
      30000,
      "Timed out waiting for native ChatGPT /backend-api/f/conversation."
    );

    await sleep(500);

    let postData = event.request?.postData || "";
    if (!postData) {
      try {
        const result = await debug.sendCommand("Network.getRequestPostData", {
          requestId: event.requestId
        });
        postData = String(result?.postData || "");
      } catch {
        // Request summary can still be useful without the body.
      }
    }

    const frames = flattenInitiatorFrames(event.initiator?.stack);
    const sourceSnippets = [];
    for (const frame of frames.filter(isChatGptScriptFrame).slice(0, 4)) {
      try {
        const result = await debug.sendCommand("Debugger.getScriptSource", {
          scriptId: frame.scriptId
        });
        const snippet = sourceSnippet(
          String(result?.scriptSource || ""),
          frame.lineNumber,
          frame.columnNumber
        );
        if (snippet)
          sourceSnippets.push({
            ...safeFrame(frame),
            snippet
          });
      } catch {
        // Script source is supplemental; the stack URL/position is primary evidence.
      }
    }

    const extra = extraInfoByRequestId.get(event.requestId);
    return {
      capturedAt: new Date().toISOString(),
      pageUrl: typeof window.webContents.getURL === "function"
        ? window.webContents.getURL()
        : null,
      request: summarizeRequest(event.request, postData),
      headers: summarizeHeaders(event.request?.headers, extra?.headers),
      initiator: {
        type: event.initiator?.type || null,
        frames: frames.map(safeFrame),
        sourceSnippets
      }
    };
  } finally {
    debug.removeListener("message", onMessage);
    if (attachedByTrace && debug.isAttached())
      debug.detach();
  }
}

function isConversationRequest(request) {
  if (String(request?.method || "").toUpperCase() !== "POST")
    return false;
  try {
    const url = new URL(String(request?.url || ""));
    return url.origin === ORIGIN && url.pathname === CONVERSATION_PATH;
  } catch {
    return false;
  }
}

function summarizeRequest(request, postData) {
  let body = null;
  try {
    body = postData ? JSON.parse(postData) : null;
  } catch {
  }

  const firstMessage = Array.isArray(body?.messages) ? body.messages[0] : null;
  const contracts = Array.isArray(body?.model_response_contracts)
    ? body.model_response_contracts
    : body?.model_response_contracts;

  return {
    url: request?.url || null,
    method: request?.method || null,
    bodyKeys: body && typeof body === "object" ? Object.keys(body).sort() : [],
    model: typeof body?.model === "string" ? body.model : null,
    thinkingEffort: typeof body?.thinking_effort === "string"
      ? body.thinking_effort
      : null,
    clientPrepareState: typeof body?.client_prepare_state === "string"
      ? body.client_prepare_state
      : null,
    conversationMode: body?.conversation_mode?.kind || null,
    projectConversation: Boolean(body?.conversation_mode?.gizmo_id),
    hasConversationId: Boolean(body?.conversation_id),
    parentMessageKind: body?.parent_message_id === "client-created-root"
      ? "client-created-root"
      : body?.parent_message_id
        ? "existing"
        : null,
    submissionMode: firstMessage?.metadata?.submission_mode || null,
    enableMessageFollowups: body?.enable_message_followups === true,
    systemHints: Array.isArray(body?.system_hints) ? body.system_hints : null,
    supportsBuffering: body?.supports_buffering === true,
    supportedEncodings: Array.isArray(body?.supported_encodings)
      ? body.supported_encodings
      : null,
    modelResponseContracts: contracts == null
      ? null
      : Array.isArray(contracts)
        ? contracts.map(value => typeof value === "string" ? value : value?.type || value?.name || "object")
        : Object.keys(contracts)
  };
}

function summarizeHeaders(...sets) {
  const headers = new Map();
  for (const set of sets) {
    if (!set || typeof set !== "object") continue;
    for (const [name, value] of Object.entries(set))
      headers.set(String(name).toLowerCase(), String(value ?? ""));
  }

  const result = {};
  for (const name of SENSITIVE_HEADERS) {
    const value = headers.get(name) || "";
    result[name] = {
      present: Boolean(value),
      length: value.length
    };
  }
  for (const name of SAFE_HEADERS) {
    const value = headers.get(name) || "";
    result[name] = {
      present: Boolean(value),
      value: value || null
    };
  }
  return result;
}

function flattenInitiatorFrames(stack) {
  const result = [];
  let current = stack;
  let depth = 0;
  while (current && depth++ < 12) {
    for (const frame of current.callFrames || []) {
      if (frame && typeof frame === "object")
        result.push(frame);
    }
    current = current.parent || null;
  }
  return result;
}

function safeFrame(frame) {
  return {
    functionName: frame?.functionName || "",
    url: frame?.url || "",
    line: Number.isFinite(frame?.lineNumber) ? frame.lineNumber + 1 : null,
    column: Number.isFinite(frame?.columnNumber) ? frame.columnNumber + 1 : null,
    scriptId: frame?.scriptId || null
  };
}

function isChatGptScriptFrame(frame) {
  return typeof frame?.scriptId === "string" &&
    typeof frame?.url === "string" &&
    frame.url.startsWith(ORIGIN + "/cdn/assets/");
}

function sourceSnippet(source, lineNumber, columnNumber) {
  if (!source) return null;
  const lines = source.split("\n");
  const line = Number.isFinite(lineNumber) ? Math.max(0, lineNumber) : 0;
  const column = Number.isFinite(columnNumber) ? Math.max(0, columnNumber) : 0;

  if (lines.length === 1) {
    const start = Math.max(0, column - 600);
    return lines[0].slice(start, column + 600);
  }

  const startLine = Math.max(0, line - 2);
  const endLine = Math.min(lines.length, line + 3);
  return lines.slice(startLine, endLine).join("\n").slice(0, 3000);
}

async function triggerNativeSend(window, prompt) {
  const selectorJson = JSON.stringify(PROMPT_EDITOR_SELECTOR);
  const focused = await window.webContents.executeJavaScript(`
    (async () => {
      const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
      const selector = ${selectorJson};
      let editor = null;
      for (let i = 0; i < 120 && !editor; i++) {
        editor = document.querySelector(selector);
        if (!editor) await sleep(250);
      }
      if (!editor)
        throw new Error("ChatGPT prompt editor was not found.");

      editor.focus();
      return {
        focused: document.activeElement === editor || editor.contains(document.activeElement),
        tagName: editor.tagName,
        contentEditable: editor.getAttribute("contenteditable")
      };
    })()
  `, true);

  if (!focused?.focused)
    throw new Error("ChatGPT prompt editor could not be focused.");

  if (typeof window.webContents.insertText !== "function" ||
      typeof window.webContents.sendInputEvent !== "function") {
    throw new Error("Electron native text/input APIs are unavailable.");
  }

  await Promise.resolve(window.webContents.insertText(prompt));
  await new Promise(resolve => setTimeout(resolve, 250));

  window.webContents.sendInputEvent({
    type: "keyDown",
    keyCode: "Enter"
  });
  window.webContents.sendInputEvent({
    type: "keyUp",
    keyCode: "Enter"
  });
}

function withTimeout(promise, milliseconds, message) {
  let timer;
  return Promise.race([
    promise,
    new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error(message)), milliseconds);
    })
  ]).finally(() => clearTimeout(timer));
}
