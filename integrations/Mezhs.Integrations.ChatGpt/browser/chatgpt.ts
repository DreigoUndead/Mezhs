const fs = require("node:fs/promises");
const os = require("node:os");
const path = require("node:path");
const { randomUUID } = require("node:crypto");

const ORIGIN = "https://chatgpt.com";
const API = Object.freeze({
  session: "/api/auth/session",
  projects: "/backend-api/gizmos/snorlax/sidebar",
  requirements: "/backend-api/sentinel/chat-requirements",
  conversation: "/backend-api/conversation",
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

    newChat(context) {
      return sendAccountMessage(context, true);
    },

    send(context) {
      return sendAccountMessage(context, false);
    },

    // Anonymous ChatGPT remains on its old browser path. Account operations above
    // use only the private API through Electron's authenticated Chromium session.
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

async function sendAccountMessage({ session, args, sleep }, isNew) {
  const token = await requireToken(session);
  const mode = isNew && args.projectId ? "gizmo_interaction" : "primary_assistant";
  const requirements = await apiJson(session, token, API.requirements, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ conversation_mode_kind: mode })
  });
  if (requirements?.proofofwork?.required ||
      requirements?.arkose?.required ||
      requirements?.turnstile?.required ||
      requirements?.so?.required)
    throw new Error("ChatGPT requires an unsupported Sentinel challenge for this request.");

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
  const headers = {
    "Content-Type": "application/json",
    "Accept": "text/event-stream",
    "Oai-Language": "en-US"
  };
  if (requirements?.token)
    headers["Openai-Sentinel-Chat-Requirements-Token"] = requirements.token;

  const response = await apiFetch(session, token, API.conversation, {
    method: "POST",
    headers,
    body: JSON.stringify({
      action: "next",
      conversation_id: isNew ? undefined : args.conversationId,
      messages: [{
        id: messageId,
        author: { role: "user" },
        content: {
          content_type: imageParts.length ? "multimodal_text" : "text",
          parts: [...imageParts, String(args.prompt || "")]
        },
        metadata: attachments.length ? { attachments } : {}
      }],
      conversation_mode: isNew && args.projectId
        ? { kind: "gizmo_interaction", gizmo_id: args.projectId }
        : { kind: "primary_assistant" },
      model: "auto",
      parent_message_id: isNew ? randomUUID() : args.parentMessageId,
      timezone_offset_min: new Date().getTimezoneOffset()
    })
  });

  const conversationId = findConversationId(await response.text()) || args.conversationId;
  if (!conversationId) throw new Error("ChatGPT did not return a conversation id.");
  const result = await waitForConversation(session, token, conversationId, messageId, sleep);
  return {
    text: result.text,
    conversationId,
    parentMessageId: result.parentMessageId,
    projectId: result.projectId,
    chatUrl: `${ORIGIN}/c/${conversationId}`,
    artifacts: await downloadFiles(session, token, result.files)
  };
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
    headers: { Authorization: `Bearer ${token}`, ...(options.headers || {}) },
    credentials: "include",
    cache: "no-store"
  });
  if (response.ok) return response;
  const detail = (await response.text()).slice(0, 1000);
  throw new Error(`ChatGPT ${endpoint} failed with HTTP ${response.status}: ${detail}`);
}

async function apiJson(session, token, endpoint, options) {
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
    await apiJson(session, token, API.fileUploaded(upload.file_id), { method: "POST" });
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
      let node = current;
      while (node) {
        if (node.message?.id === requestMessageId) break;
        collectFileRefs(node.message, files);
        node = conversation.mapping[node.parent];
      }
      return {
        text: (message.content?.parts || []).filter(x => typeof x === "string").join("\n").trim(),
        parentMessageId: message.id,
        projectId: conversation.gizmo_id || null,
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
  for (const attachment of value.attachments || [])
    if (attachment?.id) refs.set(attachment.id, attachment.name || "download");
  for (const nested of Object.values(value))
    if (nested && typeof nested === "object") collectFileRefs(nested, refs);
}

async function downloadFiles(session, token, refs) {
  const artifacts = [];
  for (const [id, requestedName] of refs) {
    try {
      const download = await apiJson(session, token, API.fileDownload(id));
      if (!download?.download_url) continue;
      const response = await session.fetch(download.download_url, { credentials: "include" });
      if (!response.ok) continue;
      const directory = await fs.mkdtemp(path.join(os.tmpdir(), "mezhs-artifact-"));
      const name = path.basename(String(requestedName || "download")) || "download";
      const localPath = path.join(directory, name);
      await fs.writeFile(localPath, Buffer.from(await response.arrayBuffer()));
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
