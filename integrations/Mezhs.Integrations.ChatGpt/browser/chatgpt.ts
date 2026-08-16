const path = require("node:path");
const fs = require("node:fs/promises");
const os = require("node:os");

const CHATGPT_ORIGIN = "https://chatgpt.com";
const ENDPOINTS = Object.freeze({
  home: CHATGPT_ORIGIN + "/",
  session: "/api/auth/session",
  backend: CHATGPT_ORIGIN + "/backend-api/",
  projects: "/backend-api/gizmos/snorlax/sidebar",
  conversation: id => "/backend-api/conversation/" + encodeURIComponent(id),
  conversationSend: Object.freeze([
    "/backend-api/conversation",
    "/backend-api/f/conversation"
  ])
});

module.exports = {
  name: "ChatGPT",
  homeUrl: ENDPOINTS.home,

  async isAuthorized(window) {
    return Boolean(await accessToken(window));
  },

  async prepareWebRequest({ window, target, headers }) {
    if (!target.startsWith(ENDPOINTS.backend) || headers.has("Authorization")) return;
    const token = await accessToken(window);
    if (token) headers.set("Authorization", "Bearer " + token);
  },

  async sendPrompt({ window, request, sleep }) {
    await navigate({ window, request });
    await setFiles({ window, filePaths: request.filePaths, sleep });
    const projectId = request.newChat && request.workspace
      ? await resolveProjectId(window, request.workspace)
      : null;
    const projectRequest = projectId
      ? await applyProjectToConversationRequest(window, projectId)
      : null;
    const prompt = JSON.stringify(String(request.prompt || ""));
    const result = await window.webContents.executeJavaScript(`
      (async () => {
        try {
        const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
        const prompt = ${prompt};
        const assistantSelector = '[data-message-author-role="assistant"]';
        const beforeCount = document.querySelectorAll(assistantSelector).length;
        const editorDeadline = Date.now() + 30000;
        let editor = null;
        while (!editor && Date.now() < editorDeadline) {
          editor = document.querySelector('#prompt-textarea, [contenteditable="true"][data-virtualkeyboard="true"]');
          if (!editor) await sleep(250);
        }
        if (!editor)
          return { ok: false, error: 'ChatGPT prompt editor was not found at ' + location.href };

        editor.focus();
        if (editor instanceof HTMLTextAreaElement) {
          const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')?.set;
          setter?.call(editor, prompt);
          editor.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: prompt }));
        } else {
          document.execCommand('selectAll', false, null);
          document.execCommand('insertText', false, prompt);
          editor.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: prompt }));
        }

        const sendDeadline = Date.now() + 90000;
        let sendButton = null;
        while (Date.now() < sendDeadline) {
          sendButton = document.querySelector(
            'button[data-testid="send-button"], button[aria-label="Send prompt"], button[aria-label="Send message"]'
          );
          if (sendButton && !sendButton.disabled) break;
          await sleep(250);
        }
        if (!sendButton || sendButton.disabled)
          return { ok: false, error: 'ChatGPT send button did not become available.' };
        sendButton.click();

        const startedDeadline = Date.now() + 45000;
        while (Date.now() < startedDeadline) {
          if (document.querySelectorAll(assistantSelector).length > beforeCount) break;
          const visibleError = document.querySelector('[role="alert"]')?.innerText?.trim();
          if (visibleError) return { ok: false, error: visibleError };
          await sleep(250);
        }

        let lastText = '';
        let lastSignature = '';
        let stableSamples = 0;
        const responseDeadline = Date.now() + 240000;
        while (Date.now() < responseDeadline) {
          const messages = document.querySelectorAll(assistantSelector);
          const latest = messages[messages.length - 1];
          const text = latest?.innerText?.trim() || '';
          const stopButton = document.querySelector(
            'button[data-testid="stop-button"], button[aria-label="Stop streaming"]'
          );
          const artifacts = collectArtifacts(latest);
          const downloadButtons = [...(latest?.querySelectorAll('button.behavior-btn[aria-label]') || [])]
            .map(button => button.getAttribute('aria-label'))
            .filter(Boolean);
          const signature = JSON.stringify({
            text,
            artifacts: artifacts.map(artifact => artifact.url),
            downloadButtons
          });
          if (signature === lastSignature) stableSamples++;
          else stableSamples = 0;
          lastText = text;
          lastSignature = signature;
          if ((text || artifacts.length || downloadButtons.length) &&
              !stopButton && stableSamples >= 6) {
            return {
              ok: true,
              text,
              artifacts,
              downloadButtons
            };
          }
          await sleep(500);
        }
        return {
          ok: false,
          text: lastText,
          error: lastText
            ? 'Timed out while waiting for ChatGPT to finish.'
            : 'ChatGPT did not produce an assistant response.'
        };

        function collectArtifacts(container) {
          if (!container) return [];
          const artifacts = [];
          const seen = new Set();
          const add = (url, name, contentType) => {
            if (!url || seen.has(url)) return;
            seen.add(url);
            artifacts.push({ url, name: name || 'download', contentType: contentType || null });
          };
          for (const anchor of container.querySelectorAll('a[href]')) {
            const href = anchor.href || anchor.getAttribute('href') || '';
            if (!anchor.hasAttribute('download') &&
                !/backend-api\\/files|oaiusercontent|sandbox:|\\/mnt\\/data/i.test(href)) continue;
            add(href, anchor.getAttribute('download') || anchor.textContent?.trim(), null);
          }
          for (const image of container.querySelectorAll('img[src]')) {
            const src = image.currentSrc || image.src;
            if ((image.naturalWidth || 0) < 96 && !/backend-api|oaiusercontent/i.test(src)) continue;
            add(src, image.alt?.trim() || 'image.png', 'image/png');
          }
          for (const element of container.querySelectorAll('[data-download-url], [data-file-url]')) {
            const url = element.getAttribute('data-download-url') || element.getAttribute('data-file-url');
            add(url, element.getAttribute('download') || element.textContent?.trim(), null);
          }
          return artifacts;
        }
        } catch (error) {
          return {
            ok: false,
            error: 'ChatGPT page automation failed: ' + String(error?.stack || error)
          };
        }
      })()
    `, true).finally(() => projectRequest?.dispose());
    if (result.ok && projectRequest) {
      const projectError = projectRequest.error();
      if (projectError) return { ok: false, error: projectError };
      try {
        await verifyProjectConversation(window, projectId);
      } catch (error) {
        return { ok: false, error: String(error?.message || error) };
      }
    }
    if (result.ok && Array.isArray(result.downloadButtons)) {
      result.artifacts ||= [];
      for (const name of result.downloadButtons) {
        try {
          result.artifacts.push(await downloadButtonArtifact({ window, name }));
        } catch (error) {
          console.error(`Could not download ChatGPT artifact '${name}': ${error}`);
        }
      }
    }
    delete result.downloadButtons;
    return result;
  }
};

async function accessToken(window) {
  const endpoint = JSON.stringify(ENDPOINTS.session);
  try {
    return await window.webContents.executeJavaScript(`
      (async () => {
        const response = await fetch(${endpoint}, {
          credentials: 'include',
          cache: 'no-store'
        });
        if (!response.ok) return null;
        const session = await response.json();
        return session?.accessToken || null;
      })()
    `, true);
  } catch {
    return null;
  }
}

async function apiJson(window, endpoint) {
  const token = await accessToken(window);
  if (!token) throw new Error("ChatGPT authorization is required.");

  const request = JSON.stringify({ endpoint, token });
  const response = await window.webContents.executeJavaScript(`
    (async () => {
      const request = ${request};
      const response = await fetch(request.endpoint, {
        headers: { Authorization: 'Bearer ' + request.token },
        credentials: 'include',
        cache: 'no-store'
      });
      return { status: response.status, body: await response.text() };
    })()
  `, true);

  if (response.status < 200 || response.status >= 300)
    throw new Error(`ChatGPT API ${endpoint} failed with HTTP ${response.status}.`);
  return response.body ? JSON.parse(response.body) : null;
}

async function resolveProjectId(window, name) {
  const wanted = String(name).trim().toLocaleLowerCase();
  if (!wanted) return null;

  const matches = new Set();
  const cursors = new Set();
  let cursor = null;
  do {
    const url = new URL(ENDPOINTS.projects, ENDPOINTS.home);
    url.searchParams.set("conversations_per_gizmo", "0");
    if (cursor) url.searchParams.set("cursor", cursor);

    const page = await apiJson(window, url.pathname + url.search);
    for (const item of page?.items || []) {
      const project = item?.gizmo?.gizmo || item?.gizmo;
      const id = String(project?.id || "");
      const projectName = String(project?.display?.name || "").trim().toLocaleLowerCase();
      if (id.startsWith("g-p-") && projectName === wanted) matches.add(id);
    }

    cursor = typeof page?.cursor === "string" && page.cursor ? page.cursor : null;
    if (cursor && !cursors.add(cursor))
      throw new Error("ChatGPT project pagination repeated the same cursor.");
  } while (cursor);

  if (matches.size === 1) return [...matches][0];
  console.error(matches.size
    ? `ChatGPT project '${name}' is ambiguous; using no project.`
    : `ChatGPT project '${name}' was not found; using no project.`);
  return null;
}

async function applyProjectToConversationRequest(window, projectId) {
  const debuggerApi = window.webContents.debugger;
  if (!debuggerApi.isAttached()) debuggerApi.attach("1.3");

  // ChatGPT constructs the complete private conversation request, including
  // transient request requirements. We change only its semantic project mode.
  const paths = new Set(ENDPOINTS.conversationSend);
  let applied = 0;
  let failure = null;
  const handler = async (_event, method, params) => {
    if (method !== "Fetch.requestPaused") return;

    const requestId = params.requestId;
    try {
      const request = params.request || {};
      const pathname = new URL(request.url).pathname;
      if (request.method !== "POST" || !paths.has(pathname) || !request.postData) {
        await debuggerApi.sendCommand("Fetch.continueRequest", { requestId });
        return;
      }

      const body = JSON.parse(request.postData);
      body.conversation_mode = { kind: "gizmo_interaction", gizmo_id: projectId };
      await debuggerApi.sendCommand("Fetch.continueRequest", {
        requestId,
        postData: Buffer.from(JSON.stringify(body), "utf8").toString("base64")
      });
      applied++;
    } catch (error) {
      failure ||= error;
      try { await debuggerApi.sendCommand("Fetch.continueRequest", { requestId }); }
      catch { }
    }
  };

  debuggerApi.on("message", handler);
  try {
    await debuggerApi.sendCommand("Fetch.enable", {
      patterns: ENDPOINTS.conversationSend.map(endpoint => ({
        urlPattern: CHATGPT_ORIGIN + endpoint + "*",
        requestStage: "Request"
      }))
    });
  } catch (error) {
    debuggerApi.removeListener("message", handler);
    throw error;
  }

  return {
    error() {
      if (failure)
        return "Could not apply ChatGPT project context: " + String(failure?.stack || failure);
      if (!applied)
        return "ChatGPT did not send the expected conversation API request.";
      return null;
    },
    async dispose() {
      debuggerApi.removeListener("message", handler);
      try { await debuggerApi.sendCommand("Fetch.disable"); }
      catch { }
    }
  };
}

async function verifyProjectConversation(window, projectId) {
  const conversationId = conversationIdFromUrl(window.webContents.getURL());
  if (!conversationId)
    throw new Error("ChatGPT did not expose the new conversation ID for project verification.");

  const conversation = await apiJson(window, ENDPOINTS.conversation(conversationId));
  if (conversation?.gizmo_id !== projectId)
    throw new Error(
      `ChatGPT conversation was created outside the configured project ` +
      `(expected ${projectId}, got ${conversation?.gizmo_id || "no project"}).`
    );
}

function conversationIdFromUrl(url) {
  try {
    const parts = new URL(url).pathname.split("/").filter(Boolean);
    const index = parts.indexOf("c");
    return index >= 0 ? parts[index + 1] || null : null;
  } catch {
    return null;
  }
}

async function downloadButtonArtifact({ window, name }) {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), "mezhs-artifact-"));
  const requestedName = path.basename(String(name)) || "download";
  const savePath = path.join(directory, requestedName);
  const browserSession = window.webContents.session;
  let handler;
  let timeout;
  const download = new Promise((resolve, reject) => {
    timeout = setTimeout(() => {
      browserSession.removeListener("will-download", handler);
      reject(new Error("Timed out waiting for the browser download."));
    }, 60000);
    handler = (_event, item) => {
      clearTimeout(timeout);
      item.setSavePath(savePath);
      item.once("done", (_doneEvent, state) => {
        if (state === "completed") {
          resolve({
            url: "",
            name: item.getFilename() || requestedName,
            contentType: item.getMimeType() || "application/octet-stream",
            localPath: savePath
          });
        } else {
          reject(new Error(`Browser download ended in state '${state}'.`));
        }
      });
    };
    browserSession.once("will-download", handler);
  });

  const label = JSON.stringify(String(name));
  const clicked = await window.webContents.executeJavaScript(`
    (() => {
      const messages = document.querySelectorAll('[data-message-author-role="assistant"]');
      const latest = messages[messages.length - 1];
      const button = [...(latest?.querySelectorAll('button.behavior-btn[aria-label]') || [])]
        .find(candidate => candidate.getAttribute('aria-label') === ${label});
      if (!button) return false;
      button.click();
      return true;
    })()
  `, true);
  if (!clicked) {
    clearTimeout(timeout);
    browserSession.removeListener("will-download", handler);
    throw new Error("Generated file button was not found.");
  }
  await new Promise(resolve => setTimeout(resolve, 1000));
  await window.webContents.executeJavaScript(`
    (() => {
      const visible = element => {
        const style = getComputedStyle(element);
        const box = element.getBoundingClientRect();
        return style.visibility !== 'hidden' && style.display !== 'none' && box.width > 0 && box.height > 0;
      };
      const candidates = [...document.querySelectorAll('a[href], button')]
        .filter(visible)
        .filter(element => {
          const label = [
            element.getAttribute('aria-label'),
            element.getAttribute('title'),
            element.getAttribute('download'),
            element.textContent
          ].filter(Boolean).join(' ').trim();
          const href = element.getAttribute('href') || '';
          return /download|save/i.test(label) ||
            /backend-api\\/files|oaiusercontent|sandbox:/i.test(href);
        });
      const target = candidates[candidates.length - 1];
      if (!target) return false;
      target.click();
      return true;
    })()
  `, true);
  return await download;
}

async function navigate({ window, request }) {
  if (request.chatUrl) {
    if (window.webContents.getURL() !== request.chatUrl)
      await window.loadURL(request.chatUrl);
    return;
  }
  if (request.newChat) await window.loadURL(module.exports.homeUrl);
}

async function setFiles({ window, filePaths, sleep }) {
  const files = (filePaths || []).map(file => path.resolve(String(file)));
  if (files.length === 0) return;

  const debuggerApi = window.webContents.debugger;
  if (!debuggerApi.isAttached()) debuggerApi.attach("1.3");
  await debuggerApi.sendCommand("DOM.enable");
  let inputObjectId = null;
  const deadline = Date.now() + 15000;
  while (!inputObjectId && Date.now() < deadline) {
    const result = await debuggerApi.sendCommand("Runtime.evaluate", {
      expression: 'document.querySelector(\'input[type="file"]\')',
      objectGroup: "mezhs-file-input",
      returnByValue: false
    });
    inputObjectId = result.result?.objectId || null;
    if (!inputObjectId) await sleep(250);
  }
  if (!inputObjectId) throw new Error("ChatGPT file input was not found.");
  await debuggerApi.sendCommand("DOM.setFileInputFiles", { files, objectId: inputObjectId });
  await debuggerApi.sendCommand("Runtime.releaseObjectGroup", { objectGroup: "mezhs-file-input" });

  const expected = files.map(file => path.basename(file));
  const uploadDeadline = Date.now() + 90000;
  while (Date.now() < uploadDeadline) {
    const state = await window.webContents.executeJavaScript(`
      (() => ({
        names: [...document.querySelectorAll('input[type="file"]')]
          .flatMap(input => [...(input.files || [])].map(file => file.name)),
        text: document.body?.innerText || ''
      }))()
    `, true);
    if (expected.every(name => state.names.includes(name) || state.text.includes(name))) {
      await sleep(1000);
      return;
    }
    await sleep(250);
  }
  throw new Error(`ChatGPT did not finish attaching: ${expected.join(", ")}`);
}
