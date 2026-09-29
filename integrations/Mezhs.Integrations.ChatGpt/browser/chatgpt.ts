const fs = require("node:fs/promises");
const os = require("node:os");
const path = require("node:path");
const { randomUUID } = require("node:crypto");

const ORIGIN = "https://chatgpt.com";
const accountIds = new WeakMap();
const API = Object.freeze({
  session: "/api/auth/session",
  projects: "/backend-api/gizmos/snorlax/sidebar",
  models: "/backend-api/models?history_and_training_disabled=false",
  modelPreference: "/backend-api/settings/user_last_used_model_config",
  conversationInit: "/backend-api/conversation/init",
  conversationPrepare: "/backend-api/f/conversation/prepare",
  conversation: "/backend-api/f/conversation",
  conversationById: id => `/backend-api/conversation/${encodeURIComponent(id)}`,
  files: "/backend-api/files",
  fileUploaded: id => `/backend-api/files/${encodeURIComponent(id)}/uploaded`,
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
const TURN_RECOVERY_RETRY_LIMIT = 1;

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

  await setModelPreference(context.session, token, selection);
  return sendApiAccountMessage(context, isNew, token, selection);
}

async function setModelPreference(session, token, selection) {
  if (!selection.model || selection.model === "auto") return;
  const url = new URL(API.modelPreference, ORIGIN);
  url.searchParams.set("model_slug", selection.model);
  if (selection.thinkingEffort)
    url.searchParams.set("thinking_effort", selection.thinkingEffort);
  await apiFetch(session, token, url.pathname + url.search, { method: "PATCH" });
}

async function sendApiAccountMessage({ window, session, args, sleep, reportProgress }, isNew, token, selection) {
  const uploaded = await uploadFiles(session, token, args.files || []);
  const imageParts = uploaded
    .filter(file => file.contentType.startsWith("image/"))
    .map(file => ({
      content_type: "image_asset_pointer",
      asset_pointer: `file-service://${file.id}`,
      size_bytes: file.size
    }));
  const attachments = uploaded.map(file => ({
    id: file.id,
    name: file.name,
    mimeType: file.contentType,
    size: file.size
  }));
  const metadata = {
    selected_sources: [],
    serialization_metadata: { custom_symbol_offsets: [] },
    submission_mode: "manual_send",
    ...(attachments.length ? { attachments } : {})
  };
  const projectMode = isNew && args.projectId
    ? { kind: "gizmo_interaction", gizmo_id: args.projectId }
    : isNew
      ? { kind: "primary_assistant" }
      : undefined;
  const parentMessageId = isNew ? "client-created-root" : args.parentMessageId;

  function buildPayload(messageId, conversationId) {
    const payload = {
      action: "next",
      conversation_id: conversationId,
      messages: [{
        id: messageId,
        author: { role: "user" },
        create_time: Date.now() / 1000,
        content: {
          content_type: imageParts.length ? "multimodal_text" : "text",
          parts: [...imageParts, String(args.prompt || "")]
        },
        metadata
      }],
      model: selection.model,
      parent_message_id: parentMessageId,
      client_prepare_state: "success",
      timezone_offset_min: new Date().getTimezoneOffset(),
      timezone: Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC",
      conversation_mode: projectMode,
      enable_message_followups: true,
      system_hints: [],
      supports_buffering: true,
      supported_encodings: ["v1"],
      client_contextual_info: clientContext(window),
      paragen_cot_summary_display_override: "allow",
      force_parallel_switch: "auto",
      local_function_names: ["local.continue_in_work"]
    };
    if (selection.thinkingEffort)
      payload.thinking_effort = selection.thinkingEffort;
    return payload;
  }

  reportProgress?.({
    state: "submitting",
    detail: "Submitting prompt to ChatGPT."
  });

  const clientSessionId = randomUUID();
  const execution = {
    requestedModel: selection.model === "auto" ? null : selection.model,
    requestedThinkingEffort: selection.thinkingEffort
  };
  if (isNew && args.projectId) {
    const initialized = await initializeConversation(
      session,
      token,
      args.projectId,
      clientSessionId
    );
    const initializedModel = String(
      initialized?.intended_default_model_slug ||
      initialized?.default_model_slug ||
      ""
    ).trim();
    if (initializedModel)
      execution.initializedDefaultModel = initializedModel;
  }

  let requestMessageId = randomUUID();
  const posted = await postConversationTurn(
    window,
    session,
    token,
    buildPayload(requestMessageId, isNew ? undefined : args.conversationId),
    args.conversationId,
    reportProgress,
    clientSessionId
  );
  let conversationId = posted.conversationId;
  mergeExecutionMetadata(execution, posted.execution);
  if (!conversationId)
    throw new Error("ChatGPT did not return a conversation id.");

  return completeAccountMessage(
    session,
    token,
    conversationId,
    requestMessageId,
    sleep,
    isNew,
    reportProgress,
    execution,
    async () => {
      const retryMessageId = randomUUID();
      reportProgress?.({
        state: "retrying",
        detail: `No active model generation was detected for ${TURN_INACTIVITY_WATCHDOG_MS / 1000}s; reposting the prompt once.`
      });
      const retry = await postConversationTurn(
        window,
        session,
        token,
        buildPayload(retryMessageId, conversationId),
        conversationId,
        reportProgress,
        clientSessionId
      );
      mergeExecutionMetadata(execution, retry.execution);
      if (retry.conversationId !== conversationId)
        throw new Error(
          `ChatGPT retry switched conversation from '${conversationId}' to '${retry.conversationId}'.`
        );
      return retryMessageId;
    }
  );
}

async function postConversationTurn(
  window,
  session,
  token,
  payload,
  fallbackConversationId = null,
  reportProgress,
  clientSessionId
) {
  const turnTraceId = randomUUID();
  const conduitToken = await getConduitToken(
    session,
    token,
    turnTraceId,
    conversationPreparePayload(payload),
    clientSessionId
  );
  const sentinelHeaders = await nativeChatRequirementsHeaders(window, payload);

  const headers = await webApiHeaders(
    session,
    API.conversation,
    clientSessionId,
    {
      "Content-Type": "application/json",
      "Accept": "text/event-stream",
      ...sentinelHeaders,
      "x-conduit-token": conduitToken,
      "x-oai-turn-trace-id": turnTraceId
    }
  );

  const response = await apiFetch(session, token, API.conversation, {
    method: "POST",
    headers,
    body: JSON.stringify(payload)
  });
  reportProgress?.({
    state: "waiting",
    detail: "Prompt accepted; waiting for model activity."
  });
  const stream = await readConversationStream(response, reportProgress);
  return {
    conversationId: stream.conversationId || fallbackConversationId,
    execution: stream.execution
  };
}

async function readConversationStream(response, reportProgress) {
  const state = {
    conversationId: null,
    execution: {},
    pending: "",
    thinkingReported: false,
    respondingReported: false
  };

  const consume = text => {
    state.pending += text;
    while (true) {
      const newline = state.pending.indexOf("\n");
      if (newline < 0) break;
      const line = state.pending.slice(0, newline).replace(/\r$/, "");
      state.pending = state.pending.slice(newline + 1);
      inspectConversationStreamLine(line, state, reportProgress);
    }
  };

  if (response.body?.getReader) {
    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      consume(decoder.decode(value, { stream: true }));
    }
    consume(decoder.decode());
  } else {
    consume(await response.text());
  }

  if (state.pending)
    inspectConversationStreamLine(state.pending.replace(/\r$/, ""), state, reportProgress);

  return {
    conversationId: state.conversationId,
    execution: state.execution
  };
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
  execution,
  retryTurn
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
      execution,
      retryTurn
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

function conversationPreparePayload(payload) {
  const message = payload.messages?.at(-1);
  const prepared = {
    action: payload.action,
    conversation_id: payload.conversation_id,
    parent_message_id: payload.parent_message_id || "client-created-root",
    model: payload.model,
    client_prepare_state: "none",
    client_prepare_dispatch: "debounced",
    client_prepare_source: "composer_editor_state",
    timezone_offset_min: payload.timezone_offset_min,
    timezone: payload.timezone,
    conversation_mode: payload.conversation_mode || { kind: "primary_assistant" },
    system_hints: payload.system_hints || [],
    partial_query: message ? {
      id: message.id,
      author: message.author,
      content: message.content
    } : undefined,
    supports_buffering: payload.supports_buffering,
    supported_encodings: payload.supported_encodings,
    client_contextual_info: {
      app_name: payload.client_contextual_info?.app_name || "chatgpt.com",
      has_web_push_capabilities: Boolean(
        payload.client_contextual_info?.has_web_push_capabilities
      ),
      web_push_notification_permission:
        payload.client_contextual_info?.web_push_notification_permission || "default"
    },
    local_function_names: payload.local_function_names || []
  };
  if (payload.thinking_effort)
    prepared.thinking_effort = payload.thinking_effort;
  return prepared;
}

async function initializeConversation(
  session,
  token,
  projectId,
  clientSessionId
) {
  const timezone = Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
  const headers = await webApiHeaders(
    session,
    API.conversationInit,
    clientSessionId,
    {
      "Accept": "*/*",
      "Content-Type": "application/json"
    }
  );
  return apiJson(session, token, API.conversationInit, {
    method: "POST",
    headers,
    body: JSON.stringify({
      gizmo_id: projectId,
      requested_default_model: null,
      conversation_id: null,
      timezone,
      timezone_offset_min: new Date().getTimezoneOffset(),
      conversation_origin: null
    })
  });
}

async function getConduitToken(
  session,
  token,
  turnTraceId,
  body,
  clientSessionId
) {
  const headers = await webApiHeaders(
    session,
    API.conversationPrepare,
    clientSessionId,
    {
      "Accept": "*/*",
      "Content-Type": "application/json",
      "x-oai-turn-trace-id": turnTraceId
    }
  );
  const response = await apiJson(session, token, API.conversationPrepare, {
    method: "POST",
    headers,
    body: JSON.stringify(body)
  });
  const conduitToken = String(response?.conduit_token || "").trim();
  if (!conduitToken)
    throw new Error("ChatGPT conversation prepare did not return a conduit token.");
  return conduitToken;
}

async function webApiHeaders(session, endpoint, clientSessionId, extra = {}) {
  const headers = {
    "Oai-Language": "en-US",
    "Oai-Session-Id": clientSessionId,
    "x-openai-web-frontend": "core_web",
    "x-openai-target-path": endpoint,
    "x-openai-target-route": endpoint,
    ...extra
  };
  const deviceId = (
    await session.cookies.get({ url: ORIGIN, name: "oai-did" })
  )[0]?.value;
  if (deviceId) headers["Oai-Device-Id"] = deviceId;
  return headers;
}

function clientContext(window) {
  const bounds = window?.getBounds?.() || {};
  const width = Number(bounds.width) || 1200;
  const height = Number(bounds.height) || 850;
  return {
    is_dark_mode: false,
    time_since_loaded: 0,
    page_height: height,
    page_width: width,
    pixel_ratio: 1,
    screen_height: height,
    screen_width: width,
    app_name: "chatgpt.com",
    has_web_push_capabilities: true,
    web_push_notification_permission: "default"
  };
}

async function nativeChatRequirementsHeaders(window, payload) {
  const securityMetadata = {
    systemHints: Array.isArray(payload?.system_hints) ? payload.system_hints : [],
    ...(payload?.conversation_mode?.gizmo_id
      ? { conversationMode: payload.conversation_mode }
      : {})
  };
  const metadata = JSON.stringify(securityMetadata);
  const script = `
    (async () => {
      const moduleUrls = [...new Set(
        performance.getEntriesByType("resource")
          .map(entry => String(entry.name || ""))
          .filter(url => /\\/cdn\\/assets\\/conversation-small-[^/?#]+\\.js(?:[?#].*)?$/i.test(url))
      )].reverse();

      for (const moduleUrl of moduleUrls) {
        const provider = await import(moduleUrl);
        const candidates = Object.values(provider).filter(value => {
          if (typeof value !== "function") return false;
          const source = Function.prototype.toString.call(value);
          return source.includes("chatReq") &&
            source.includes("turnstileToken") &&
            source.includes("proofToken") &&
            source.includes("getEnforcementTokenSync") &&
            source.includes("getEnforcementToken");
        });
        if (candidates.length !== 1)
          continue;

        const security = await Promise.resolve(candidates[0](${metadata}));
        if (!security?.chatReq || typeof security.chatReq !== "object")
          throw new Error("ChatGPT native chat requirements provider returned no requirements.");

        const telemetry = await Promise.resolve(window.SentinelSDK?.timing?.() ?? null);
        return {
          requirementsToken:
            typeof security.chatReq.token === "string" ? security.chatReq.token : null,
          prepareToken:
            typeof security.chatReq.prepare_token === "string"
              ? security.chatReq.prepare_token
              : null,
          forceLogin: security.chatReq.force_login === true,
          turnstileToken:
            typeof security.turnstileToken === "string"
              ? security.turnstileToken
              : null,
          proofToken:
            typeof security.proofToken === "string" ? security.proofToken : null,
          telemetry: typeof telemetry === "string" ? telemetry : null
        };
      }

      throw new Error("ChatGPT native chat requirements provider was not found in the loaded frontend.");
    })()
  `;

  const security = await window.webContents.executeJavaScript(script, true);
  if (!security || typeof security !== "object")
    throw new Error("ChatGPT native chat requirements returned an invalid result.");
  if (security.forceLogin)
    throw new Error("ChatGPT native chat requirements require login.");

  const headers = {};
  const addHeader = (name, value) => {
    if (value === null || value === undefined || value === "") return;
    if (typeof value !== "string" || /[\\r\\n]/.test(value))
      throw new Error(`ChatGPT native chat requirements returned an invalid '${name}' header.`);
    headers[name] = value;
  };

  addHeader(
    "OpenAI-Sentinel-Chat-Requirements-Token",
    security.requirementsToken
  );
  addHeader(
    "OpenAI-Sentinel-Chat-Requirements-Prepare-Token",
    security.prepareToken
  );
  addHeader("OpenAI-Sentinel-Turnstile-Token", security.turnstileToken);
  addHeader("OpenAI-Sentinel-Proof-Token", security.proofToken);
  addHeader("OAI-Telemetry", security.telemetry);

  if (!headers["OpenAI-Sentinel-Chat-Requirements-Token"] &&
      !headers["OpenAI-Sentinel-Chat-Requirements-Prepare-Token"]) {
    throw new Error("ChatGPT native chat requirements did not produce a requirements token.");
  }
  return headers;
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

const INTEGRITY_STATE_COOKIE = "__Secure-oai-is";
const INTEGRITY_STATE_PATTERN =
  /^ois1\.[A-Za-z0-9_-]+\.([A-Za-z0-9_-]{16})\.[A-Za-z0-9_-]+$/;
const INTEGRITY_STATE_MAX_AGE_SECONDS = 30 * 24 * 60 * 60;

async function apiFetch(session, token, endpoint, options = {}) {
  const accountId = accountIds.get(session);
  const headers = {
    Authorization: `Bearer ${token}`,
    ...(accountId ? { "ChatGPT-Account-Id": accountId } : {}),
    ...(options["headers"] || {})
  };
  const streamRequest = endpoint === API.conversation;
  headers["X-OAI-IS-Client-Observation"] =
    await integrityStateObservation(session, streamRequest ? "s" : "r");

  const response = await session.fetch(ORIGIN + endpoint, {
    ...options,
    headers,
    credentials: "include",
    cache: "no-store"
  });
  await applyIntegrityStateUpdate(session, response.headers);

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

async function integrityStateObservation(session, source) {
  try {
    const cookies = await session.cookies.get({
      url: ORIGIN,
      name: INTEGRITY_STATE_COOKIE
    });
    const value = String(cookies?.[0]?.value || "").trim();
    if (!value) return `v1.${source}.m`;
    const match = INTEGRITY_STATE_PATTERN.exec(value);
    return match
      ? `v1.${source}.p.${match[1]}`
      : `v1.${source}.i`;
  } catch {
    return `v1.${source}.r`;
  }
}

async function applyIntegrityStateUpdate(session, headers) {
  const update = String(headers?.get?.("x-oai-is-update") || "").trim();
  if (!INTEGRITY_STATE_PATTERN.test(update) || !session.cookies?.set)
    return;

  await session.cookies.set({
    url: ORIGIN,
    name: INTEGRITY_STATE_COOKIE,
    value: update,
    path: "/",
    secure: true,
    sameSite: "lax",
    expirationDate: Date.now() / 1000 + INTEGRITY_STATE_MAX_AGE_SECONDS
  });
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

async function uploadFiles(session, token, files) {
  const result = [];
  for (const file of files) {
    const bytes = await fs.readFile(file.path);
    const contentType = String(file.contentType || "application/octet-stream");
    const upload = await apiJson(session, token, API.files, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        file_name: file.name,
        file_size: bytes.length,
        use_case: contentType.startsWith("image/") ? "multimodal" : "my_files"
      })
    });
    const put = await session.fetch(upload.upload_url, {
      method: "PUT",
      headers: { "Content-Type": contentType, "x-ms-blob-type": "BlockBlob" },
      body: bytes
    });
    if (!put.ok) throw new Error(`ChatGPT file upload failed with HTTP ${put.status}.`);
    await apiJson(session, token, API.fileUploaded(upload.file_id), {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: "{}"
    });
    result.push({ id: upload.file_id, name: file.name, contentType, size: bytes.length });
  }
  return result;
}

async function waitForConversation(
  session,
  token,
  conversationId,
  requestMessageId,
  sleep,
  reportProgress,
  execution,
  retryTurn
) {
  const endpoint = API.conversationById(conversationId);
  let consecutiveRateLimits = 0;
  let inactiveWaitMs = 0;
  let lastProgressKey = null;
  let retries = 0;

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
      if (retries >= TURN_RECOVERY_RETRY_LIMIT)
        throw new Error(
          `ChatGPT showed no active generation for ${TURN_INACTIVITY_WATCHDOG_MS / 1000}s after the automatic retry.`
        );

      requestMessageId = await retryTurn();
      retries++;
      inactiveWaitMs = 0;
      lastProgressKey = null;
      reportProgress?.({
        state: "waiting",
        detail: "Prompt reposted; waiting for model activity."
      });
      continue;
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
