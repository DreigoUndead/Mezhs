const path = require("node:path");
const fs = require("node:fs/promises");
const os = require("node:os");

module.exports = {
  name: "ChatGPT",
  homeUrl: "https://chatgpt.com/",

  async isAuthorized(window) {
    try {
      return await window.webContents.executeJavaScript(`
        (async () => {
          try {
            const response = await fetch('/api/auth/session', {
              credentials: 'include',
              cache: 'no-store'
            });
            if (!response.ok) return false;
            const session = await response.json();
            return Boolean(session?.accessToken);
          } catch { return false; }
        })()
      `, true);
    } catch {
      return false;
    }
  },

  async prepareWebRequest({ target, headers }) {
    if (target.startsWith('https://chatgpt.com/backend-api/') && !headers.has('Authorization')) {
      const sessionResponse = await fetch('/api/auth/session', {
        credentials: 'include',
        cache: 'no-store'
      });
      if (sessionResponse.ok) {
        const session = await sessionResponse.json();
        if (session?.accessToken) headers.set('Authorization', 'Bearer ' + session.accessToken);
      }
    }
  },

  async sendPrompt({ window, request, sleep }) {
    await navigate({ window, request });
    await setFiles({ window, filePaths: request.filePaths, sleep });
    const projectScope = request.newChat && request.workspaceId
      ? await interceptProjectConversationMode({ window, projectId: request.workspaceId })
      : null;
    const prompt = JSON.stringify(String(request.prompt || ""));
    let result;
    try {
      result = await window.webContents.executeJavaScript(`
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
    `, true);
    } finally {
      if (projectScope)
        await projectScope.dispose();
    }
    if (result.ok && projectScope?.error) {
      return {
        ok: false,
        error: 'Could not apply ChatGPT project context to the conversation request: ' +
          String(projectScope.error?.stack || projectScope.error)
      };
    }
    if (result.ok && projectScope && projectScope.appliedCount === 0) {
      return {
        ok: false,
        error: 'ChatGPT did not send a conversation request that could be assigned to the configured project.'
      };
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

async function interceptProjectConversationMode({ window, projectId }) {
  const debuggerApi = window.webContents.debugger;
  if (!debuggerApi.isAttached()) debuggerApi.attach("1.3");

  let appliedCount = 0;
  let error = null;
  const handler = async (_event, method, params) => {
    if (method !== 'Fetch.requestPaused') return;

    const requestId = params.requestId;
    try {
      const request = params.request || {};
      const isConversationRequest = request.method === 'POST' &&
        /\/backend-api\/(?:f\/)?conversation(?:\/prepare)?(?:[?#]|$)/.test(request.url || '');
      if (isConversationRequest && request.postData) {
        let body = null;
        try { body = JSON.parse(request.postData); }
        catch { }
        if (body && typeof body === 'object' && !Array.isArray(body)) {
          body.conversation_mode = {
            kind: 'gizmo_interaction',
            gizmo_id: projectId
          };
          await debuggerApi.sendCommand('Fetch.continueRequest', {
            requestId,
            postData: Buffer.from(JSON.stringify(body), 'utf8').toString('base64')
          });
          appliedCount++;
          return;
        }
      }
      await debuggerApi.sendCommand('Fetch.continueRequest', { requestId });
    } catch (caught) {
      error ||= caught;
      try { await debuggerApi.sendCommand('Fetch.continueRequest', { requestId }); }
      catch { }
    }
  };

  debuggerApi.on('message', handler);
  try {
    await debuggerApi.sendCommand('Fetch.enable', {
      patterns: [{
        urlPattern: '*://chatgpt.com/backend-api/*conversation*',
        requestStage: 'Request'
      }]
    });
  } catch (caught) {
    debuggerApi.removeListener('message', handler);
    throw caught;
  }

  return {
    get appliedCount() { return appliedCount; },
    get error() { return error; },
    async dispose() {
      debuggerApi.removeListener('message', handler);
      try { await debuggerApi.sendCommand('Fetch.disable'); }
      catch { }
    }
  };
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
  if (!request.newChat) return;
  await window.loadURL(module.exports.homeUrl);
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
