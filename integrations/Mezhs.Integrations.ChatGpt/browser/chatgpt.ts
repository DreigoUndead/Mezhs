const fs = require("node:fs/promises");
const os = require("node:os");
const path = require("node:path");
const { Buffer } = require("node:buffer");
const { randomUUID } = require("node:crypto");

const ORIGIN = "https://chatgpt.com";
const API = Object.freeze({
  session: "/api/auth/session",
  projects: "/backend-api/gizmos/snorlax/sidebar",
  models: "/backend-api/models?history_and_training_disabled=false",
  conversationPrepare: "/backend-api/f/conversation/prepare",
  requirementsPrepare: "/backend-api/sentinel/chat-requirements/prepare",
  requirementsFinalize: "/backend-api/sentinel/chat-requirements/finalize",
  conversation: "/backend-api/f/conversation",
  conversationById: id => `/backend-api/conversation/${encodeURIComponent(id)}`,
  files: "/backend-api/files",
  fileUploaded: id => `/backend-api/files/${encodeURIComponent(id)}/uploaded`,
  fileDownload: id => `/backend-api/files/${encodeURIComponent(id)}/download`
});

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

    // Anonymous ChatGPT remains on its old browser path. Account operations above
    // use the private API through Electron's authenticated Chromium session.
    async sendPrompt({ window, args }) {
      if (args.newChat) await window.loadURL(module.exports.homeUrl);
      const prompt = JSON.stringify(String(args.prompt || ""));
      return window.webContents.executeJavaScript(`
        (async () => {
          const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
          const selector = '[data-message-author-role="assistant"]';
          const before = document.querySelectorAll(selector).length;
          let editor = null;
          for (let i = 0; i < 120 && !editor; i++) {
            editor = document.querySelector('#prompt-textarea, [contenteditable="true"][data-virtualkeyboard="true"]');
            if (!editor) await sleep(250);
          }
          if (!editor) return { ok: false, error: 'ChatGPT prompt editor was not found.' };
          editor.focus();
          document.execCommand('selectAll', false, null);
          document.execCommand('insertText', false, ${prompt});
          editor.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: ${prompt} }));
          let send = null;
          for (let i = 0; i < 360 && (!send || send.disabled); i++) {
            send = document.querySelector('button[data-testid="send-button"], button[aria-label="Send prompt"], button[aria-label="Send message"]');
            if (!send || send.disabled) await sleep(250);
          }
          if (!send || send.disabled) return { ok: false, error: 'ChatGPT send button did not become available.' };
          send.click();
          let last = '';
          let stable = 0;
          for (let i = 0; i < 480; i++) {
            const messages = document.querySelectorAll(selector);
            const text = messages[messages.length - 1]?.innerText?.trim() || '';
            const stop = document.querySelector('button[data-testid="stop-button"], button[aria-label="Stop streaming"]');
            stable = text && text === last ? stable + 1 : 0;
            last = text;
            if (messages.length > before && text && !stop && stable >= 6)
              return { ok: true, text };
            await sleep(500);
          }
          return { ok: false, text: last, error: 'ChatGPT response timed out.' };
        })()
      `, true);
    }
  }
};

function nativePickerModels(catalog) {
  const models = new Map(
    (catalog?.models || []).map(model => [
      String(model?.slug || model?.id || "").trim().toLowerCase(),
      model
    ])
  );
  const result = [];
  const seen = new Set();
  for (const version of catalog?.versions || []) {
    if (version?.enabled === false) continue;
    const versionId = String(version?.id || "").trim();
    const versionName = String(
      version?.display_text_for_intelligence ||
      version?.display_text ||
      versionId
    ).trim();
    const nativePresets = version?.intelligence_presets || [];
    const presets = nativePresets
      .filter(preset => preset?.preset_type === "available" && preset?.enabled !== false);

    if (nativePresets.length) {
      for (const preset of presets) {
        const model = String(preset?.model_slug || "").trim();
        const effort = String(preset?.thinking_effort || "").trim();
        const presetName = String(
          preset?.selected_display_title ||
          preset?.title ||
          ""
        ).trim();
        const id = modelSelectionId(model, effort);
        const name = [versionName, presetName].filter(Boolean).join(" · ");
        const key = id.toLowerCase();
        if (!model || !name || seen.has(key)) continue;
        seen.add(key);
        result.push({ id, name });
      }
      continue;
    }

    const canonicalId = versionId.toLowerCase() === "o3"
      ? "o3"
      : `gpt-${versionId.replace(/\./g, "-")}`;
    const candidates = (version?.slugs || [])
      .map(value => String(value || "").trim())
      .filter(Boolean);
    const id = candidates.find(candidate => candidate.toLowerCase() === canonicalId.toLowerCase()) ||
      (models.has(canonicalId.toLowerCase()) ? canonicalId : null) ||
      candidates.find(candidate => models.has(candidate.toLowerCase())) ||
      candidates[0];
    const model = id ? models.get(id.toLowerCase()) : null;
    const name = String(
      versionName ||
      model?.title ||
      model?.display_name ||
      model?.name ||
      id ||
      ""
    ).trim();
    const key = String(id || "").toLowerCase();
    if (!id || !name || seen.has(key)) continue;
    seen.add(key);
    result.push({ id, name });
  }
  return result;
}

const MODEL_SELECTION_SEPARATOR = "::thinking-effort=";
const CHATGPT_WIRE_MODEL = Object.freeze({
  "gpt-5-6-instant": "gpt-5-5",
  "gpt-5-5-instant": "gpt-5-5"
});

function modelSelectionId(model, thinkingEffort) {
  return thinkingEffort
    ? `${model}${MODEL_SELECTION_SEPARATOR}${thinkingEffort}`
    : model;
}

function parseModelSelection(value) {
  const selected = String(value || "auto").trim() || "auto";
  const separator = selected.lastIndexOf(MODEL_SELECTION_SEPARATOR);
  const model = separator > 0 ? selected.slice(0, separator) : selected;
  const thinkingEffort = separator > 0
    ? selected.slice(separator + MODEL_SELECTION_SEPARATOR.length).trim() || null
    : null;
  return {
    model: CHATGPT_WIRE_MODEL[model.toLowerCase()] || model,
    thinkingEffort
  };
}

async function sendAccountMessage({ window, session, args, sleep }, isNew) {
  const token = await requireToken(session);
  const uploaded = await uploadFiles(session, token, args.files || []);
  const messageId = randomUUID();
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

  const projectMode = isNew && args.projectId;
  const selection = parseModelSelection(args.model);
  const model = selection.model;
  const metadata = {
    selected_sources: [],
    serialization_metadata: { custom_symbol_offsets: [] },
    ...(attachments.length ? { attachments } : {})
  };
  const payload = {
    action: "next",
    conversation_id: isNew ? undefined : args.conversationId,
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
    model,
    parent_message_id: isNew ? "client-created-root" : args.parentMessageId,
    client_prepare_state: "success",
    timezone_offset_min: new Date().getTimezoneOffset(),
    timezone: Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC",
    conversation_mode: isNew
      ? projectMode
        ? { kind: "gizmo_interaction", gizmo_id: args.projectId }
        : { kind: "primary_assistant" }
      : undefined,
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

  const config = sentinelConfig(window);
  const turnTraceId = randomUUID();
  const conduitToken = await getConduitToken(
    session,
    token,
    turnTraceId,
    conversationPreparePayload(payload)
  );
  const requirements = await getSentinelToken(session, token, config);

  const headers = {
    "Content-Type": "application/json",
    "Accept": "text/event-stream",
    "Oai-Language": "en-US",
    "Oai-Session-Id": randomUUID(),
    "openai-sentinel-chat-requirements-token": requirements.token,
    "x-conduit-token": conduitToken,
    "x-oai-turn-trace-id": turnTraceId,
    "x-openai-target-path": API.conversation,
    "x-openai-target-route": API.conversation
  };
  const deviceId = (await session.cookies.get({ url: ORIGIN, name: "oai-did" }))[0]?.value;
  if (deviceId) headers["Oai-Device-Id"] = deviceId;
  if (requirements.proofToken)
    headers["openai-sentinel-proof-token"] = requirements.proofToken;

  const response = await apiFetch(session, token, API.conversation, {
    method: "POST",
    headers,
    body: JSON.stringify(payload)
  });
  const conversationId = findConversationId(await response.text()) || args.conversationId;
  if (!conversationId) throw new Error("ChatGPT did not return a conversation id.");

  let result;
  try {
    result = await waitForConversation(session, token, conversationId, messageId, sleep);
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
    client_prepare_state: "success",
    client_prepare_dispatch: "immediate",
    client_prepare_source: "context_change",
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

async function getConduitToken(session, token, turnTraceId, body) {
  const response = await apiJson(session, token, API.conversationPrepare, {
    method: "POST",
    headers: {
      "Accept": "*/*",
      "Content-Type": "application/json",
      "x-oai-turn-trace-id": turnTraceId,
      "x-openai-target-path": API.conversationPrepare,
      "x-openai-target-route": API.conversationPrepare
    },
    body: JSON.stringify(body)
  });
  const conduitToken = String(response?.conduit_token || "").trim();
  if (!conduitToken)
    throw new Error("ChatGPT conversation prepare did not return a conduit token.");
  return conduitToken;
}

async function getSentinelToken(session, token, config) {
  const prepared = await apiJson(session, token, API.requirementsPrepare, {
    method: "POST",
    headers: targetHeaders(API.requirementsPrepare),
    body: JSON.stringify({ p: sentinelRequirementsToken(config) })
  });
  const prepareToken = String(prepared?.prepare_token || "").trim();
  if (!prepareToken)
    throw new Error("ChatGPT Sentinel prepare did not return a prepare token.");

  const proofToken = prepared?.proofofwork?.required
    ? sentinelProofToken(prepared.proofofwork, config)
    : null;
  const body = { prepare_token: prepareToken };
  if (proofToken) body.proofofwork = proofToken;

  const finalized = await apiJson(session, token, API.requirementsFinalize, {
    method: "POST",
    headers: targetHeaders(API.requirementsFinalize),
    body: JSON.stringify(body)
  });
  const sentinelToken = String(finalized?.token || "").trim();
  if (!sentinelToken)
    throw new Error("ChatGPT Sentinel finalize did not return a token.");
  return { token: sentinelToken, proofToken };
}

function targetHeaders(endpoint) {
  return {
    "Accept": "*/*",
    "Content-Type": "application/json",
    "x-openai-target-path": endpoint,
    "x-openai-target-route": endpoint
  };
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

function sentinelConfig(window) {
  const userAgent = window?.webContents?.getUserAgent?.() || "Mozilla/5.0";
  return [
    3000,
    new Date().toString(),
    4294705152,
    0,
    userAgent,
    "",
    "",
    "en-US",
    "en-US,en",
    0,
    "vendor−Google Inc.",
    "location",
    "navigator",
    0,
    randomUUID(),
    "",
    8,
    Date.now()
  ];
}

function sentinelRequirementsToken(config) {
  return solveSentinelProof(String(Math.random()), "0fffff", config, "gAAAAAC");
}

function sentinelProofToken(challenge, config) {
  const seed = String(challenge?.seed || "");
  const difficulty = String(challenge?.difficulty || "");
  if (!seed || !/^[0-9a-f]+$/i.test(difficulty) || difficulty.length % 2)
    throw new Error("ChatGPT returned an invalid Sentinel proof-of-work challenge.");
  return solveSentinelProof(seed, difficulty, config, "gAAAAAB");
}

function solveSentinelProof(seed, difficulty, config, prefix) {
  const target = Buffer.from(difficulty, "hex");
  for (let counter = 0; counter < 500000; counter++) {
    const candidate = [...config];
    candidate[3] = counter;
    candidate[9] = counter >> 1;
    const encoded = Buffer.from(JSON.stringify(candidate)).toString("base64");
    const digest = sha3_512(Buffer.from(seed + encoded));
    if (digest.subarray(0, target.length).compare(target) < 0)
      return prefix + encoded;
  }
  throw new Error("ChatGPT Sentinel proof-of-work could not be solved.");
}

const KECCAK_MASK = (1n << 64n) - 1n;
const KECCAK_ROTATION = [
  0, 1, 62, 28, 27,
  36, 44, 6, 55, 20,
  3, 10, 43, 25, 39,
  41, 45, 15, 21, 8,
  18, 2, 61, 56, 14
];
const KECCAK_ROUND_CONSTANTS = [
  0x0000000000000001n, 0x0000000000008082n, 0x800000000000808an,
  0x8000000080008000n, 0x000000000000808bn, 0x0000000080000001n,
  0x8000000080008081n, 0x8000000000008009n, 0x000000000000008an,
  0x0000000000000088n, 0x0000000080008009n, 0x000000008000000an,
  0x000000008000808bn, 0x800000000000008bn, 0x8000000000008089n,
  0x8000000000008003n, 0x8000000000008002n, 0x8000000000000080n,
  0x000000000000800an, 0x800000008000000an, 0x8000000080008081n,
  0x8000000000008080n, 0x0000000080000001n, 0x8000000080008008n
];

function sha3_512(input) {
  const rate = 72;
  const state = new Array(25).fill(0n);
  let offset = 0;

  while (offset + rate <= input.length) {
    absorbKeccakBlock(state, input.subarray(offset, offset + rate));
    offset += rate;
  }

  const block = Buffer.alloc(rate);
  input.copy(block, 0, offset);
  block[input.length - offset] = 0x06;
  block[rate - 1] |= 0x80;
  absorbKeccakBlock(state, block);

  const output = Buffer.alloc(64);
  for (let index = 0; index < output.length; index++)
    output[index] = Number((state[Math.floor(index / 8)] >> BigInt(8 * (index % 8))) & 0xffn);
  return output;
}

function absorbKeccakBlock(state, block) {
  for (let lane = 0; lane < 9; lane++) {
    let value = 0n;
    for (let byte = 0; byte < 8; byte++)
      value |= BigInt(block[lane * 8 + byte]) << BigInt(byte * 8);
    state[lane] ^= value;
  }
  keccakF1600(state);
}

function keccakF1600(state) {
  for (const roundConstant of KECCAK_ROUND_CONSTANTS) {
    const column = new Array(5);
    const delta = new Array(5);
    for (let x = 0; x < 5; x++)
      column[x] = state[x] ^ state[x + 5] ^ state[x + 10] ^ state[x + 15] ^ state[x + 20];
    for (let x = 0; x < 5; x++)
      delta[x] = column[(x + 4) % 5] ^ rotateKeccak(column[(x + 1) % 5], 1);
    for (let y = 0; y < 5; y++)
      for (let x = 0; x < 5; x++)
        state[x + 5 * y] ^= delta[x];

    const rotated = new Array(25).fill(0n);
    for (let y = 0; y < 5; y++)
      for (let x = 0; x < 5; x++)
        rotated[y + 5 * ((2 * x + 3 * y) % 5)] =
          rotateKeccak(state[x + 5 * y], KECCAK_ROTATION[x + 5 * y]);

    for (let y = 0; y < 5; y++)
      for (let x = 0; x < 5; x++)
        state[x + 5 * y] = rotated[x + 5 * y] ^
          ((~rotated[(x + 1) % 5 + 5 * y]) & rotated[(x + 2) % 5 + 5 * y]);
    state[0] ^= roundConstant;
  }
}

function rotateKeccak(value, bits) {
  if (!bits) return value;
  const shift = BigInt(bits);
  return ((value << shift) | (value >> (64n - shift))) & KECCAK_MASK;
}

async function accessToken(session) {
  const response = await session.fetch(ORIGIN + API.session, {
    credentials: "include",
    cache: "no-store"
  });
  if (!response.ok) return null;
  return (await response.json())?.accessToken || null;
}

async function requireToken(session) {
  const token = await accessToken(session);
  if (!token) throw new Error("ChatGPT authorization is required.");
  return token;
}

async function apiFetch(session, token, endpoint, options = {}) {
  const response = await session.fetch(ORIGIN + endpoint, {
    ...options,
    headers: { Authorization: `Bearer ${token}`, ...(options["headers"] || {}) },
    credentials: "include",
    cache: "no-store"
  });
  if (response.ok) return response;

  const detail = (await response.text()).slice(0, 1000);
  const error = new Error(`ChatGPT ${endpoint} failed with HTTP ${response.status}: ${detail}`);
  error.status = response.status;
  error.endpoint = endpoint;
  error.detail = detail;
  throw error;
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

function findConversationId(text) {
  for (const value of text.split(/\r?\n/)) {
    const json = value.startsWith("data:") ? value.slice(5).trim() : value.trim();
    if (!json || json === "[DONE]") continue;
    try {
      const id = JSON.parse(json)?.conversation_id;
      if (id) return id;
    } catch { }
  }
  return null;
}

async function waitForConversation(session, token, conversationId, requestMessageId, sleep) {
  for (let i = 0; i < 480; i++) {
    const conversation = await apiJson(session, token, API.conversationById(conversationId));
    const current = conversation?.mapping?.[conversation.current_node];
    const message = current?.message;
    if (message?.author?.role === "assistant" && message.status !== "in_progress") {
      const files = new Map();
      const assistantModel = String(
        message?.metadata?.resolved_model_slug ||
        message?.metadata?.model_slug ||
        ""
      ).trim() || null;
      let requestResolvedModel = null;
      let node = current;
      while (node) {
        if (node.message?.id === requestMessageId) {
          requestResolvedModel = String(
            node.message?.metadata?.resolved_model_slug || ""
          ).trim() || null;
          break;
        }
        collectFileRefs(node.message, files);
        node = conversation.mapping[node.parent];
      }
      return {
        text: (message.content?.parts || []).filter(x => typeof x === "string").join("\n").trim(),
        parentMessageId: message.id,
        projectId: conversation.gizmo_id || null,
        model: assistantModel || requestResolvedModel,
        files
      };
    }
    await sleep(500);
  }
  throw new Error("ChatGPT response timed out.");
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
