const fs = require("node:fs/promises");
const os = require("node:os");
const path = require("node:path");

const ORIGIN = "https://chatgpt.com";
const accountIds = new WeakMap();
const API = Object.freeze({
  session: "/api/auth/session",
  projects: "/backend-api/gizmos/snorlax/sidebar",
  models: "/backend-api/models?history_and_training_disabled=false",
  modelPreference: "/backend-api/settings/user_last_used_model_config",
  conversation: "/backend-api/f/conversation",
  conversationById: id => `/backend-api/conversation/${encodeURIComponent(id)}`,
  fileDownload: id => `/backend-api/files/${encodeURIComponent(id)}/download`
});

const PROMPT_EDITOR_SELECTOR = [
  '#prompt-textarea',
  'textarea[name="prompt-textarea"]',
  '[data-testid="prompt-textarea"]',
  '.ProseMirror[contenteditable="true"]',
  '[contenteditable="true"][role="textbox"]',
  '[contenteditable="true"][data-virtualkeyboard="true"]'
].join(', ');

const CONVERSATION_POLL_INTERVAL_MS = 2000;
const CONVERSATION_RATE_LIMIT_MAX_FALLBACK_MS = 30000;
const TURN_INACTIVITY_WATCHDOG_MS = 20000;

module.exports = {
  name: "ChatGPT",
  homeUrl: ORIGIN + "/",

  async isAuthorized(window) {
    return Boolean(await accessToken(window.webContents.session).catch(() => null));
  },

  operations: {
    async getProjects({ session }) {
      const token = await requireToken(session);
      const projects = [];
      let cursor = null;
      do {
        const url = new URL(API.projects, ORIGIN);
        url.searchParams.set("conversations_per_gizmo", "0");
        if (cursor) url.searchParams.set("cursor", cursor);
        const page = await apiJson(session, token, url.pathname + url.search);
        for (const item of page?.items || []) {
          const project = item?.gizmo?.gizmo || item?.gizmo;
          const id = String(project?.id || "");
          const name = String(project?.display?.name || "").trim();
          if (id.startsWith("g-p-") && name) projects.push({ id, name });
        }
        cursor = typeof page?.cursor === "string" && page.cursor ? page.cursor : null;
      } while (cursor);
      return projects;
    },

    async getModels({ session }) {
      const token = await requireToken(session);
      const response = await apiJson(session, token, API.models);
      return nativePickerModels(response);
    },

    newChat(context) {
      return sendAccountMessage(context, true);
    },

    send(context) {
      return sendAccountMessage(context, false);
    },

    // Anonymous ChatGPT remains on its old browser path.
    async sendPrompt({ window, args }) {
      if (args.newChat) await window.loadURL(module.exports.homeUrl);
      const prompt = JSON.stringify(String(args.prompt || ""));
      const promptEditorSelector = JSON.stringify(PROMPT_EDITOR_SELECTOR);
      return window.webContents.executeJavaScript(`
        (async () => {
          const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
          const selector = '[data-message-author-role="assistant"]';
          const before = document.querySelectorAll(selector).length;
          let editor = null;
          for (let i = 0; i < 120 && !editor; i++) {
            editor = document.querySelector(${promptEditorSelector});
            if (!editor) await sleep(250);
          }
          if (!editor) return { ok: false, error: 'ChatGPT prompt editor was not found.' };
          editor.focus();
          if (editor.tagName === 'TEXTAREA' || editor.tagName === 'INPUT') {
            const prototype = editor.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
            const setter = Object.getOwnPropertyDescriptor(prototype, 'value')?.set;
            if (setter) setter.call(editor, ${prompt});
            else editor.value = ${prompt};
            editor.dispatchEvent(new Event('input', { bubbles: true }));
          } else {
            document.execCommand('selectAll', false, null);
            document.execCommand('insertText', false, ${prompt});
            editor.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: ${prompt} }));
          }
          let send = null;
          for (let i = 0; i < 360 && (!send || send.disabled); i++) {
            send = document.querySelector('button[data-testid="send-button"], button[aria-label="Send prompt"], button[aria-label="Send message"]');
            if (!send || send.disabled) await sleep(250);
          }
          if (!send || send.disabled) return { ok: false, error: 'ChatGPT send button did not become available.' };
          send.click();
          let last = '';
          let stable = 0;
          while (true) {
            const messages = document.querySelectorAll(selector);
            const text = messages[messages.length - 1]?.innerText?.trim() || '';
            const stop = document.querySelector('button[data-testid="stop-button"], button[aria-label="Stop streaming"]');
            stable = text && text === last ? stable + 1 : 0;
            last = text;
            if (messages.length > before && text && !stop && stable >= 6)
              return { ok: true, text };
            await sleep(500);
          }
        })()
      `, true);
    }
  }
};

function nativePickerModels(catalog) {
  const result = [];
  const seen = new Set();
  const versions = Array.isArray(catalog?.versions) ? catalog.versions : [];

  for (const version of versions) {
    if (version?.enabled === false) continue;
    const versionName = String(
      version?.display_text_for_intelligence ||
      version?.display_text ||
      ""
    ).trim();
    const nativePresets = Array.isArray(version?.intelligence_presets)
      ? version.intelligence_presets
      : [];
    const presets = nativePresets.filter(preset =>
      preset?.preset_type === "available" &&
      preset?.enabled !== false
    );

    if (nativePresets.length) {
      for (const preset of presets) {
        const model = String(preset?.model_slug || "").trim();
        const effort = String(preset?.thinking_effort || "").trim();
        const presetName = String(
          preset?.selected_display_title ||
          preset?.title ||
          ""
        ).trim();
        addModelOption(
          result,
          seen,
          modelSelectionId(model, effort),
          [versionName, presetName].filter(Boolean).join(" · ")
        );
      }
      continue;
    }

    const model = (Array.isArray(version?.slugs) ? version.slugs : [])
      .map(value => String(value || "").trim())
      .find(Boolean);
    addModelOption(result, seen, model, versionName || model);
  }

  if (result.length)
    return result;

  for (const model of catalog?.models || []) {
    const id = String(model?.slug || model?.id || "").trim();
    const name = String(
      model?.title ||
      model?.display_name ||
      model?.name ||
      id
    ).trim();
    addModelOption(result, seen, id, name);
  }
  return result;
}

function addModelOption(result, seen, id, name) {
  const normalizedId = String(id || "").trim();
  const normalizedName = String(name || "").trim();
  const key = normalizedId.toLowerCase();
  if (!normalizedId || !normalizedName || seen.has(key)) return;
  seen.add(key);
  result.push({ id: normalizedId, name: normalizedName });
}

const MODEL_SELECTION_SEPARATOR = "::thinking-effort=";

function modelSelectionId(model, thinkingEffort) {
  return thinkingEffort
    ? `${model}${MODEL_SELECTION_SEPARATOR}${thinkingEffort}`
    : model;
}

function parseModelSelection(value) {
  const selected = String(value || "auto").trim() || "auto";
  const separator = selected.lastIndexOf(MODEL_SELECTION_SEPARATOR);
  return {
    model: separator > 0 ? selected.slice(0, separator) : selected,
    thinkingEffort: separator > 0
      ? selected.slice(separator + MODEL_SELECTION_SEPARATOR.length).trim() || null
      : null
  };
}

async function sendAccountMessage(context, isNew) {
  const token = await requireToken(context.session);
  const selection = parseModelSelection(context.args.model);
  if (context.args.files?.length) {
    throw new Error(
      "ChatGPT Account file input is temporarily unavailable while native composer submission is used."
    );
  }

  await setModelPreference(context.session, token, selection);
  return sendNativeAccountMessage(context, isNew, token, selection);
}

async function setModelPreference(session, token, selection) {
  if (!selection.model || selection.model === "auto") return;
  const url = new URL(API.modelPreference, ORIGIN);
  url.searchParams.set("model_slug", selection.model);
  if (selection.thinkingEffort)
    url.searchParams.set("thinking_effort", selection.thinkingEffort);
  await apiFetch(session, token, url.pathname + url.search, { method: "PATCH" });
}

async function sendNativeAccountMessage(
  { window, session, args, sleep, reportProgress },
  isNew,
  token,
  selection
) {
  reportProgress?.({
    state: "submitting",
    detail: "Submitting prompt through the native ChatGPT composer."
  });

  await window.loadURL(nativeConversationUrl(isNew, args));

  const execution = {
    requestedModel: selection.model === "auto" ? null : selection.model,
    requestedThinkingEffort: selection.thinkingEffort
  };
  const posted = await submitNativeConversationTurn(
    window,
    String(args.prompt || ""),
    selection,
    isNew ? null : args.conversationId,
    isNew ? args.projectId : null,
    reportProgress
  );
  mergeExecutionMetadata(execution, posted.execution);

  return completeAccountMessage(
    session,
    token,
    posted.conversationId,
    posted.requestMessageId,
    sleep,
    isNew,
    reportProgress,
    execution
  );
}

function nativeConversationUrl(isNew, args) {
  if (!isNew && args.conversationId)
    return `${ORIGIN}/c/${encodeURIComponent(args.conversationId)}`;
  if (args.projectId)
    return `${ORIGIN}/g/${encodeURIComponent(args.projectId)}/project`;
  return ORIGIN + "/";
}

async function submitNativeConversationTurn(
  window,
  prompt,
  selection,
  expectedConversationId,
  expectedProjectId,
  reportProgress
) {
  const debug = window.webContents.debugger;
  if (!debug ||
      typeof debug.isAttached !== "function" ||
      typeof debug.attach !== "function" ||
      typeof debug.sendCommand !== "function" ||
      typeof debug.on !== "function" ||
      typeof debug.removeListener !== "function") {
    throw new Error("Electron debugger API is unavailable for native ChatGPT submission.");
  }
  if (typeof window.webContents.insertText !== "function" ||
      typeof window.webContents.sendInputEvent !== "function") {
    throw new Error("Electron native input APIs are unavailable for native ChatGPT submission.");
  }

  const attachedByMezhs = !debug.isAttached();
  if (attachedByMezhs)
    debug.attach("1.3");

  let requestEvent = null;
  let resolveRequest;
  let rejectCompletion;
  let resolveCompletion;
  const requestSeen = new Promise(resolve => { resolveRequest = resolve; });
  const completionSeen = new Promise((resolve, reject) => {
    resolveCompletion = resolve;
    rejectCompletion = reject;
  });

  const onMessage = (_event, method, params) => {
    if (method === "Network.requestWillBeSent" &&
        !requestEvent &&
        isNativeConversationRequest(params?.request)) {
      requestEvent = params;
      resolveRequest(params);
      return;
    }

    if (!requestEvent || params?.requestId !== requestEvent.requestId)
      return;
    if (method === "Network.loadingFinished")
      resolveCompletion();
    else if (method === "Network.loadingFailed")
      rejectCompletion(new Error(
        `Native ChatGPT request failed: ${params?.errorText || "network failure"}.`
      ));
  };

  debug.on("message", onMessage);
  try {
    await debug.sendCommand("Network.enable", { maxPostDataSize: 1024 * 1024 });
    await focusEmptyNativeComposer(window);
    await Promise.resolve(window.webContents.insertText(prompt));
    await verifyNativeComposerText(window, prompt);

    window.webContents.sendInputEvent({ type: "keyDown", keyCode: "Enter" });
    window.webContents.sendInputEvent({ type: "keyUp", keyCode: "Enter" });

    const event = await withTimeout(
      requestSeen,
      30000,
      "Timed out waiting for ChatGPT's native conversation request."
    );
    const body = await nativeConversationRequestBody(debug, event);
    validateNativeConversationRequest(
      body,
      prompt,
      selection,
      expectedConversationId,
      expectedProjectId
    );

    const requestMessageId = String(body?.messages?.[0]?.id || "").trim();
    if (!requestMessageId)
      throw new Error("ChatGPT native request did not contain a user message id.");

    reportProgress?.({
      state: "waiting",
      detail: "Native ChatGPT request accepted; waiting for model completion."
    });

    await completionSeen;

    let responseBody = "";
    try {
      const response = await debug.sendCommand("Network.getResponseBody", {
        requestId: event.requestId
      });
      responseBody = response?.base64Encoded
        ? Buffer.from(String(response.body || ""), "base64").toString("utf8")
        : String(response?.body || "");
    } catch (error) {
      if (!expectedConversationId)
        throw new Error(
          `Could not read the native ChatGPT response stream: ${error?.message || error}`
        );
    }

    const stream = inspectConversationStreamText(responseBody, reportProgress);
    const conversationId =
      stream.conversationId ||
      String(body?.conversation_id || "").trim() ||
      String(expectedConversationId || "").trim();
    if (!conversationId)
      throw new Error("ChatGPT native response did not reveal a conversation id.");

    return {
      conversationId,
      requestMessageId,
      execution: stream.execution
    };
  } finally {
    debug.removeListener("message", onMessage);
    if (attachedByMezhs && debug.isAttached())
      debug.detach();
  }
}

function isNativeConversationRequest(request) {
  if (String(request?.method || "").toUpperCase() !== "POST")
    return false;
  try {
    const url = new URL(String(request?.url || ""));
    return url.origin === ORIGIN && url.pathname === API.conversation;
  } catch {
    return false;
  }
}

async function nativeConversationRequestBody(debug, event) {
  let postData = String(event?.request?.postData || "");
  if (!postData) {
    const result = await debug.sendCommand("Network.getRequestPostData", {
      requestId: event.requestId
    });
    postData = String(result?.postData || "");
  }
  try {
    return JSON.parse(postData);
  } catch {
    throw new Error("ChatGPT native conversation request body was not valid JSON.");
  }
}

function validateNativeConversationRequest(
  body,
  prompt,
  selection,
  expectedConversationId,
  expectedProjectId
) {
  const submittedPrompt = body?.messages?.[0]?.content?.parts
    ?.filter(part => typeof part === "string")
    .join("\n")
    .trim();
  if (submittedPrompt !== prompt.trim())
    throw new Error("ChatGPT native request did not contain the submitted prompt.");

  if (selection.model && selection.model !== "auto" && body?.model !== selection.model) {
    throw new Error(
      `ChatGPT native composer selected model '${body?.model || "unknown"}' instead of '${selection.model}'.`
    );
  }
  if (selection.thinkingEffort &&
      body?.thinking_effort !== selection.thinkingEffort) {
    throw new Error(
      `ChatGPT native composer selected thinking effort '${body?.thinking_effort || "none"}' instead of '${selection.thinkingEffort}'.`
    );
  }

  if (expectedConversationId &&
      body?.conversation_id !== expectedConversationId) {
    throw new Error(
      `ChatGPT native continuation targeted '${body?.conversation_id || "new chat"}' instead of '${expectedConversationId}'.`
    );
  }
  if (expectedProjectId &&
      body?.conversation_mode?.gizmo_id !== expectedProjectId) {
    throw new Error(
      `ChatGPT native composer did not submit inside project '${expectedProjectId}'.`
    );
  }
}

async function focusEmptyNativeComposer(window) {
  const selector = JSON.stringify(PROMPT_EDITOR_SELECTOR);
  const result = await window.webContents.executeJavaScript(`
    (async () => {
      const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
      const selector = ${selector};
      let editor = null;
      for (let i = 0; i < 120 && !editor; i++) {
        editor = document.querySelector(selector);
        if (!editor) await sleep(250);
      }
      if (!editor)
        return { ok: false, error: "ChatGPT prompt editor was not found." };

      const text = editor.tagName === "TEXTAREA" || editor.tagName === "INPUT"
        ? editor.value
        : editor.innerText || editor.textContent || "";
      if (String(text || "").trim())
        return { ok: false, error: "ChatGPT prompt editor contains an existing draft." };

      editor.focus();
      return { ok: true };
    })()
  `, true);

  if (!result?.ok)
    throw new Error(result?.error || "ChatGPT prompt editor could not be focused.");
}

async function verifyNativeComposerText(window, prompt) {
  const selector = JSON.stringify(PROMPT_EDITOR_SELECTOR);
  const expected = String(prompt);
  const actual = await window.webContents.executeJavaScript(`
    (() => {
      const editor = document.querySelector(${selector});
      if (!editor) return null;
      return editor.tagName === "TEXTAREA" || editor.tagName === "INPUT"
        ? editor.value
        : editor.innerText || editor.textContent || "";
    })()
  `, true);
  if (String(actual ?? "") !== expected)
    throw new Error("ChatGPT native composer did not receive the exact prompt text.");
}

function inspectConversationStreamText(text, reportProgress) {
  const state = {
    conversationId: null,
    execution: {},
    thinkingReported: false,
    respondingReported: false
  };
  for (const line of String(text || "").split(/\r?\n/))
    inspectConversationStreamLine(line, state, reportProgress);
  return {
    conversationId: state.conversationId,
    execution: state.execution
  };
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

function inspectConversationStreamLine(line, state, reportProgress) {
  if (!line.startsWith("data:")) return;
  const data = line.slice(5).trim();
  if (!data || data === "[DONE]") return;

  let value;
  try {
    value = JSON.parse(data);
  } catch {
    return;
  }

  if (!state.conversationId)
    state.conversationId = findConversationIdInValue(value);

  collectStreamExecutionMetadata(value, state.execution);

  if (value?.type === "message_marker" && value.marker === "cot_token") {
    state.execution.reasoningObserved = true;
    if (!state.thinkingReported) {
      state.thinkingReported = true;
      reportProgress?.({
        state: "thinking",
        detail: "ChatGPT reported reasoning activity."
      });
    }
  }

  if (value?.type === "message_marker" && value.marker === "final_channel_token") {
    if (!state.respondingReported) {
      state.respondingReported = true;
      reportProgress?.({
        state: "responding",
        detail: "ChatGPT is generating the visible response."
      });
    }
  }
}

function findConversationIdInValue(value) {
  if (!value || typeof value !== "object") return null;
  if (typeof value.conversation_id === "string" && value.conversation_id)
    return value.conversation_id;
  for (const nested of Object.values(value)) {
    const found = findConversationIdInValue(nested);
    if (found) return found;
  }
  return null;
}

function collectStreamExecutionMetadata(value, execution) {
  if (!value || typeof value !== "object") return;

  if (value?.content?.content_type === "reasoning_recap")
    execution.reasoningObserved = true;

  collectToolMetadata(value, execution);

  const metadata = value.metadata;
  if (metadata && typeof metadata === "object")
    collectMetadataFields(metadata, execution);

  if (value.type === "server_ste_metadata" && metadata) {
    const experience = String(metadata.requested_model_experience || "").trim();
    if (experience) execution.requestedModelExperience ??= experience;
    const ttfvt = finiteNumber(metadata.server_ttfvt_ms);
    if (ttfvt !== null) execution.serverTtfvtMs ??= ttfvt;
  }

  for (const nested of Object.values(value))
    if (nested && typeof nested === "object")
      collectStreamExecutionMetadata(nested, execution);
}

function collectToolMetadata(value, execution) {
  const role = String(value?.author?.role || "").trim().toLowerCase();
  const recipient = String(value?.recipient || "").trim();
  const authorName = String(value?.author?.name || "").trim();
  const metadata = value?.metadata || {};

  if (role === "assistant" && recipient && recipient.toLowerCase() !== "all")
    addExecutionTool(execution, recipient);
  if (role === "tool" && authorName)
    addExecutionTool(execution, authorName);

  const metadataTool = String(metadata.tool_name || "").trim();
  if (metadataTool && metadata.tool_invoked !== false)
    addExecutionTool(execution, metadataTool);
}

function addExecutionTool(execution, tool) {
  const value = String(tool || "").trim();
  if (!value) return;
  execution.tools ??= [];
  if (!execution.tools.includes(value))
    execution.tools.push(value);
}

function collectMetadataFields(metadata, execution) {
  const model = String(
    metadata.resolved_model_slug ||
    metadata.model_slug ||
    ""
  ).trim();
  const thinkingEffort = String(metadata.thinking_effort || "").trim();
  const reasoningStatus = String(metadata.reasoning_status || "").trim();
  const reasoningStart = finiteNumber(metadata.reasoning_start_time);
  const reasoningEnd = finiteNumber(metadata.reasoning_end_time);

  if (model) execution.model ??= model;
  if (thinkingEffort) execution.thinkingEffort ??= thinkingEffort;
  if (reasoningStatus) execution.reasoningStatus ??= reasoningStatus;
  if (reasoningStatus || reasoningStart !== null || reasoningEnd !== null)
    execution.reasoningObserved = true;
  if (reasoningStart !== null)
    execution.reasoningStart = execution.reasoningStart === undefined
      ? reasoningStart
      : Math.min(execution.reasoningStart, reasoningStart);
  if (reasoningEnd !== null)
    execution.reasoningEnd = execution.reasoningEnd === undefined
      ? reasoningEnd
      : Math.max(execution.reasoningEnd, reasoningEnd);
}

function mergeExecutionMetadata(target, source, authoritative = false) {
  if (!source) return target;
  if (source.model) {
    if (authoritative) {
      target.model = source.model;
      target.thinkingEffort = source.thinkingEffort || null;
    } else {
      target.model ??= source.model;
    }
  }
  if (!authoritative && source.thinkingEffort)
    target.thinkingEffort ??= source.thinkingEffort;
  if (source.reasoningStatus) target.reasoningStatus ??= source.reasoningStatus;
  if (source.requestedModelExperience)
    target.requestedModelExperience ??= source.requestedModelExperience;
  if (source.serverTtfvtMs !== undefined)
    target.serverTtfvtMs ??= source.serverTtfvtMs;
  if (source.reasoningObserved)
    target.reasoningObserved = true;
  for (const tool of source.tools || [])
    addExecutionTool(target, tool);

  const start = finiteNumber(source.reasoningStart);
  const end = finiteNumber(source.reasoningEnd);
  if (start !== null)
    target.reasoningStart = target.reasoningStart === undefined
      ? start
      : Math.min(target.reasoningStart, start);
  if (end !== null)
    target.reasoningEnd = target.reasoningEnd === undefined
      ? end
      : Math.max(target.reasoningEnd, end);
  return target;
}

function finiteNumber(value) {
  if (value === null || value === undefined || value === "") return null;
  const number = Number(value);
  return Number.isFinite(number) ? number : null;
}

function completionDetail(execution) {
  const facts = [];
  if (execution?.model) facts.push(`served model ${execution.model}`);
  if (execution?.requestedModel &&
      execution?.model &&
      execution.requestedModel !== execution.model)
    facts.push(`requested model ${execution.requestedModel}`);
  if (execution?.thinkingEffort) {
    facts.push(`effort ${execution.thinkingEffort}`);
  } else if (execution?.requestedThinkingEffort) {
    facts.push(`requested effort ${execution.requestedThinkingEffort}, not confirmed by provider`);
  }

  const start = finiteNumber(execution?.reasoningStart);
  const end = finiteNumber(execution?.reasoningEnd);
  if (start !== null && end !== null && end >= start) {
    facts.push(`reasoning ${(end - start).toFixed(1)}s`);
  } else if (execution?.reasoningObserved) {
    facts.push("reasoning observed");
  }
  if (execution?.tools?.length)
    facts.push(`tools ${execution.tools.join(", ")}`);

  return facts.length
    ? `Model response received (${facts.join(", ")}).`
    : "Model response received.";
}

async function completeAccountMessage(
  session,
  token,
  conversationId,
  requestMessageId,
  sleep,
  isNew,
  reportProgress,
  execution
) {
  let result;
  try {
    result = await waitForConversation(
      session,
      token,
      conversationId,
      requestMessageId,
      sleep,
      reportProgress,
      execution
    );
  } catch (error) {
    if (!isNew && isConversationUnavailable(error, conversationId))
      return { conversationUnavailable: true };
    throw error;
  }

  return {
    text: result.text,
    conversationId,
    parentMessageId: result.parentMessageId,
    projectId: result.projectId,
    chatUrl: `${ORIGIN}/c/${conversationId}`,
    artifacts: await downloadFiles(session, token, result.files),
    model: result.model
  };
}

async function accessToken(session) {
  const response = await session.fetch(ORIGIN + API.session, {
    credentials: "include",
    cache: "no-store"
  });
  if (!response.ok) {
    accountIds.delete(session);
    return null;
  }

  const auth = await response.json();
  const accountId = String(
    auth?.account?.id ||
    auth?.accountId ||
    auth?.account_id ||
    auth?.user?.id ||
    ""
  ).trim();
  if (accountId) accountIds.set(session, accountId);
  else accountIds.delete(session);
  return auth?.accessToken || null;
}

async function requireToken(session) {
  const token = await accessToken(session);
  if (!token) throw new Error("ChatGPT authorization is required.");
  return token;
}

async function apiFetch(session, token, endpoint, options = {}) {
  const accountId = accountIds.get(session);
  const headers = {
    Authorization: `Bearer ${token}`,
    ...(accountId ? { "ChatGPT-Account-Id": accountId } : {}),
    ...(options["headers"] || {})
  };

  const response = await session.fetch(ORIGIN + endpoint, {
    ...options,
    headers,
    credentials: "include",
    cache: "no-store"
  });
  if (response.ok) return response;

  const detail = (await response.text()).slice(0, 1000);
  const error = Object.assign(
    new Error(`ChatGPT ${endpoint} failed with HTTP ${response.status}: ${detail}`),
    {
      status: response.status,
      endpoint,
      detail,
      retryAfterMs: parseRetryAfterMs(response.headers.get("retry-after"))
    }
  );
  throw error;
}

function parseRetryAfterMs(value) {
  if (!value) return null;
  const seconds = Number(value);
  if (Number.isFinite(seconds) && seconds >= 0)
    return Math.ceil(seconds * 1000);
  const date = Date.parse(value);
  return Number.isNaN(date) ? null : Math.max(0, date - Date.now());
}

function isConversationUnavailable(error, conversationId) {
  return error?.status === 404 &&
    error?.endpoint === API.conversationById(conversationId) &&
    /"code"\s*:\s*"conversation_inaccessible"/.test(String(error?.detail || ""));
}

async function apiJson(session, token, endpoint, options = {}) {
  const response = await apiFetch(session, token, endpoint, options);
  const text = await response.text();
  return text ? JSON.parse(text) : null;
}

async function waitForConversation(
  session,
  token,
  conversationId,
  requestMessageId,
  sleep,
  reportProgress,
  execution
) {
  const endpoint = API.conversationById(conversationId);
  let consecutiveRateLimits = 0;
  let inactiveWaitMs = 0;
  let lastProgressKey = null;

  while (true) {
    let conversation;
    try {
      conversation = await apiJson(session, token, endpoint);
      consecutiveRateLimits = 0;
    } catch (error) {
      if (error?.status !== 429 || error?.endpoint !== endpoint)
        throw error;

      const fallbackDelay = Math.min(
        CONVERSATION_RATE_LIMIT_MAX_FALLBACK_MS,
        CONVERSATION_POLL_INTERVAL_MS * (2 ** Math.min(consecutiveRateLimits, 4))
      );
      const delay = Math.max(
        CONVERSATION_POLL_INTERVAL_MS,
        error.retryAfterMs ?? fallbackDelay
      );
      consecutiveRateLimits++;
      reportProgress?.({
        state: "rate-limited",
        detail: `ChatGPT rate limited state checks; retrying in ${Math.ceil(delay / 1000)}s.`
      });
      console.error(`ChatGPT conversation poll rate-limited; retrying in ${delay} ms.`);
      await sleep(delay);
      continue;
    }

    const turn = inspectConversationTurn(conversation, requestMessageId);
    if (turn.reply) {
      mergeExecutionMetadata(execution, turn.reply.execution, true);
      if (execution.model)
        turn.reply.model = modelSelectionId(execution.model, execution.thinkingEffort);
      turn.reply.execution = execution;
      reportProgress?.({
        state: "completed",
        detail: completionDetail(execution),
        analysis: turn.analysis
      });
      return turn.reply;
    }

    reportProgress?.({
      state: turn.state,
      detail: turn.detail,
      analysis: turn.analysis
    });

    const progressObserved = Boolean(
      turn.progressKey && turn.progressKey !== lastProgressKey
    );
    if (progressObserved)
      lastProgressKey = turn.progressKey;

    if (turn.active || progressObserved) {
      inactiveWaitMs = 0;
    } else if (inactiveWaitMs >= TURN_INACTIVITY_WATCHDOG_MS) {
      throw new Error(
        `ChatGPT showed no active generation for ${TURN_INACTIVITY_WATCHDOG_MS / 1000}s after the native request completed.`
      );
    }

    await sleep(CONVERSATION_POLL_INTERVAL_MS);
    if (!turn.active)
      inactiveWaitMs += CONVERSATION_POLL_INTERVAL_MS;
  }
}

function inspectConversationTurn(conversation, requestMessageId) {
  const reply = findVisibleAssistantReply(conversation, requestMessageId);
  if (reply)
    return {
      reply,
      active: false,
      progressKey: String(conversation?.current_node || reply.parentMessageId || ""),
      state: "responding",
      detail: "Model response completed.",
      analysis: collectTurnAnalysis(conversation, requestMessageId)
    };

  const mapping = conversation?.mapping || {};
  const progressKey = String(conversation?.current_node || "");
  let node = mapping[conversation?.current_node];
  let reachedRequest = false;
  let assistantObserved = false;
  let inProgress = null;
  const analysis = [];

  while (node) {
    const message = node.message;
    if (message?.id === requestMessageId) {
      reachedRequest = true;
      break;
    }

    if (message?.author?.role === "assistant") {
      assistantObserved = true;
      const channel = String(message.channel || "").trim().toLowerCase();
      if (channel === "analysis") {
        const text = visibleAssistantText(message);
        if (text) analysis.push(text);
      }
      if (!inProgress && message.status === "in_progress")
        inProgress = { channel, reasoning: hasReasoningMetadata(message) };
    }

    node = mapping[node.parent];
  }

  const analysisText = analysis.reverse().join("\n\n").trim() || null;
  if (!reachedRequest) {
    return {
      reply: null,
      active: false,
      progressKey: null,
      state: "waiting",
      detail: "Waiting for the submitted prompt to become the active ChatGPT turn.",
      analysis: null
    };
  }

  if (inProgress) {
    const thinking = inProgress.channel === "analysis" || inProgress.reasoning;
    return {
      reply: null,
      active: true,
      progressKey,
      state: thinking ? "thinking" : "responding",
      detail: thinking ? "Model is thinking." : "Model is generating a response.",
      analysis: analysisText
    };
  }

  if (assistantObserved) {
    return {
      reply: null,
      active: false,
      progressKey,
      state: "waiting",
      detail: "Model activity was observed, but no active generation is currently detected.",
      analysis: analysisText
    };
  }

  return {
    reply: null,
    active: false,
    progressKey,
    state: "waiting",
    detail: "Prompt is present, but no model activity has started yet.",
    analysis: null
  };
}

function collectTurnAnalysis(conversation, requestMessageId) {
  const mapping = conversation?.mapping || {};
  let node = mapping[conversation?.current_node];
  const values = [];
  while (node) {
    const message = node.message;
    if (message?.id === requestMessageId)
      return values.reverse().join("\n\n").trim() || null;
    if (message?.author?.role === "assistant" &&
        String(message.channel || "").trim().toLowerCase() === "analysis") {
      const text = visibleAssistantText(message);
      if (text) values.push(text);
    }
    node = mapping[node.parent];
  }
  return null;
}

function findVisibleAssistantReply(conversation, requestMessageId) {
  const mapping = conversation?.mapping || {};
  let node = mapping[conversation?.current_node];
  let assistant = null;
  const files = new Map();
  const execution = {};

  while (node) {
    const message = node.message;
    collectConversationExecutionMetadata(message, execution);

    if (message?.id === requestMessageId) {
      if (!assistant) return null;
      const requestResolvedModel = String(
        message.metadata?.resolved_model_slug || ""
      ).trim() || null;
      const resolvedModel = execution.model || requestResolvedModel;
      return {
        text: visibleAssistantText(assistant),
        parentMessageId: assistant.id,
        projectId: conversation.gizmo_id || null,
        model: resolvedModel
          ? modelSelectionId(resolvedModel, execution.thinkingEffort)
          : null,
        execution,
        files
      };
    }

    collectFileRefs(message, files);
    if (!assistant && isVisibleAssistantMessage(message))
      assistant = message;
    node = mapping[node.parent];
  }

  return null;
}

function collectConversationExecutionMetadata(message, execution) {
  if (!message) return;
  collectToolMetadata(message, execution);
  if (message?.author?.role !== "assistant") return;
  collectMetadataFields(message.metadata || {}, execution);
  if (message?.content?.content_type === "reasoning_recap")
    execution.reasoningObserved = true;
}

function hasReasoningMetadata(message) {
  const metadata = message?.metadata || {};
  return Boolean(
    message?.content?.content_type === "reasoning_recap" ||
    String(metadata.reasoning_status || "").trim() ||
    finiteNumber(metadata.reasoning_start_time) !== null ||
    finiteNumber(metadata.reasoning_end_time) !== null
  );
}

function isVisibleAssistantMessage(message) {
  if (message?.author?.role !== "assistant" || message.status === "in_progress")
    return false;
  if (message.metadata?.is_visually_hidden_from_conversation === true || message.weight === 0)
    return false;

  const channel = String(message.channel || "").trim().toLowerCase();
  if (channel && channel !== "final")
    return false;

  const recipient = String(message.recipient || "").trim().toLowerCase();
  if (recipient && recipient !== "all")
    return false;

  const contentType = String(message.content?.content_type || "text").trim().toLowerCase();
  if (contentType !== "text" && contentType !== "multimodal_text")
    return false;

  return Boolean(visibleAssistantText(message));
}

function visibleAssistantText(message) {
  return (message?.content?.parts || [])
    .filter(part => typeof part === "string")
    .join("\n")
    .trim();
}

function collectFileRefs(value, refs) {
  if (!value || typeof value !== "object") return;
  if (typeof value.asset_pointer === "string") {
    const match = /^(?:file-service|sediment):\/\/(.+)$/.exec(value.asset_pointer);
    if (match) refs.set(match[1], value.name || "download");
  }
  if (Array.isArray(value.attachments)) {
    for (const attachment of value.attachments)
      if (attachment?.id) refs.set(attachment.id, attachment.name || "download");
  }
  for (const nested of Object.values(value))
    if (nested && typeof nested === "object") collectFileRefs(nested, refs);
}

async function downloadFiles(session, token, refs) {
  const artifacts = [];
  for (const [id, requestedName] of refs) {
    try {
      const download = await apiJson(session, token, API.fileDownload(id));
      if (!download?.download_url) continue;
      const response = await session.fetch(download.download_url, {
        headers: download.download_url.startsWith(ORIGIN)
          ? { Authorization: `Bearer ${token}` }
          : undefined,
        credentials: "include"
      });
      if (!response.ok) continue;
      const directory = await fs.mkdtemp(path.join(os.tmpdir(), "mezhs-artifact-"));
      const name = path.basename(String(requestedName || "download")) || "download";
      const localPath = path.join(directory, name);
      await fs.writeFile(localPath, new Uint8Array(await response.arrayBuffer()));
      artifacts.push({
        url: download.download_url,
        name,
        contentType: response.headers.get("content-type") || null,
        localPath
      });
    } catch (error) {
      console.error(`Could not download ChatGPT artifact '${id}': ${error}`);
    }
  }
  return artifacts;
}
