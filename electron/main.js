const { app, BrowserWindow, ipcMain, session } = require("electron");
const { randomUUID } = require("node:crypto");
const http = require("node:http");
const path = require("node:path");
const {
  cleanChromeUserAgent,
  configureSessionBrowserIdentity
} = require("./browser-identity");

let window = null;
let browserModule = null;
let activeSession = null;
let keepVisible = false;
let shuttingDown = false;
const parentProcessId = Number(process.env.MEZHS_PARENT_PROCESS_ID || 0);
const browserPreload = path.join(__dirname, "browser-preload.js");

app.commandLine.appendSwitch("no-sandbox");
app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("disable-gpu-compositing");
app.commandLine.appendSwitch("disable-software-rasterizer");
app.commandLine.appendSwitch("disable-background-timer-throttling");
app.commandLine.appendSwitch("disable-backgrounding-occluded-windows");
app.commandLine.appendSwitch("disable-renderer-backgrounding");
app.userAgentFallback = cleanChromeUserAgent(app.userAgentFallback);
app.disableHardwareAcceleration();

if (parentProcessId > 0) {
  setInterval(() => {
    try {
      process.kill(parentProcessId, 0);
    } catch {
      shuttingDown = true;
      app.quit();
    }
  }, 2000).unref();
}

function sleep(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

function loadBrowserModule(modulePath) {
  if (!modulePath)
    throw new Error("Browser module path is required.");
  const resolved = path.resolve(modulePath);
  const implementation = require(resolved);
  if (!implementation?.homeUrl || !implementation.operations)
    throw new Error(`Browser module '${resolved}' is incomplete.`);
  return implementation;
}

function invokePageOperation(targetWindow, operation, args) {
  const responseChannel = `mezhs:page-operation-result:${randomUUID()}`;
  return new Promise((resolve, reject) => {
    const handler = (_event, response) => {
      if (response?.ok)
        resolve(response.result);
      else
        reject(new Error(response?.error || `Browser page operation '${operation}' failed.`));
    };
    ipcMain.once(responseChannel, handler);
    try {
      targetWindow.webContents.send("mezhs:page-operation", {
        responseChannel,
        operation,
        args: args ?? {}
      });
    } catch (error) {
      ipcMain.removeListener(responseChannel, handler);
      reject(error);
    }
  });
}

async function initialize({ profileDirectory, showBrowser, modulePath, requireAuthorization }) {
  await app.whenReady();
  browserModule = loadBrowserModule(modulePath);
  keepVisible = Boolean(showBrowser);
  const persistentSession = session.fromPath(path.resolve(profileDirectory));
  configureSessionBrowserIdentity(persistentSession);
  activeSession = persistentSession;

  console.error(
    `Initializing ${browserModule.name} window ` +
    `(visible=${keepVisible}, authorization=${Boolean(requireAuthorization)}, profile=${profileDirectory})`
  );
  window = new BrowserWindow({
    width: 1200,
    height: 850,
    show: keepVisible,
    title: `MEŽS - ${browserModule.name}`,
    webPreferences: {
      session: persistentSession,
      preload: browserPreload,
      contextIsolation: true,
      sandbox: false,
      backgroundThrottling: false
    }
  });
  window.webContents.setWindowOpenHandler(() => ({
    action: "allow",
    overrideBrowserWindowOptions: {
      webPreferences: {
        session: persistentSession,
        preload: browserPreload,
        contextIsolation: true,
        sandbox: false
      }
    }
  }));

  window.on("close", event => {
    if (shuttingDown) return;
    event.preventDefault();
    window.hide();
  });
  window.webContents.on("did-fail-load", (_event, code, description, url) => {
    console.error(`Navigation failed (${code} ${description}): ${url}`);
  });
  window.webContents.on("console-message", (_event, level, message, line, sourceId) => {
    if (level >= 2)
      console.error(`${browserModule.name} renderer: ${message} (${sourceId}:${line})`);
  });
  window.once("ready-to-show", () => {
    if (keepVisible) {
      window.show();
      window.focus();
    }
  });

  await window.loadURL(browserModule.homeUrl);
  console.error(`${browserModule.name} navigation completed at ${window.webContents.getURL()}`);

  if (requireAuthorization) {
    if (typeof browserModule.isAuthorized !== "function")
      throw new Error(`Browser module '${modulePath}' does not support authorization.`);
    if (!await browserModule.isAuthorized(window) && !keepVisible) {
      const error = new Error(`${browserModule.name} authorization is required.`);
      error.code = "authorization_required";
      throw error;
    }
    while (!await browserModule.isAuthorized(window))
      await sleep(1000);
    await persistentSession.flushStorageData();
    await persistentSession.cookies.flushStore();
    console.error(`${browserModule.name} authorization confirmed and persisted.`);
  }

  if (typeof browserModule.afterInitialize === "function")
    await browserModule.afterInitialize({ window, session: persistentSession, sleep });
  if (!keepVisible) window.hide();
  return { ready: true };
}

function invokeProvider({ operation, arguments: args }, reportProgress) {
  if (!window || !browserModule || !activeSession)
    throw new Error("Electron browser is not initialized.");
  if (String(operation || "").startsWith("$diagnostics/"))
    return invokeDiagnostic(String(operation).slice("$diagnostics/".length), args ?? {});
  const method = browserModule.operations[operation];
  if (typeof method !== "function")
    throw new Error(`${browserModule.name} does not support provider operation '${operation}'.`);
  return method({
    window,
    session: activeSession,
    page: {
      invoke: (pageOperation, pageArgs) =>
        invokePageOperation(window, pageOperation, pageArgs)
    },
    args: args ?? {},
    sleep,
    reportProgress
  });
}


function diagnosticElementSnapshot() {
  return `(() => {
    const clean = value => String(value ?? '').replace(/\s+/g, ' ').trim().slice(0, 240);
    const visible = element => {
      const rect = element.getBoundingClientRect();
      const style = getComputedStyle(element);
      return rect.width > 0 && rect.height > 0 &&
        style.visibility !== 'hidden' && style.display !== 'none' &&
        Number(style.opacity || 1) !== 0;
    };
    const describe = element => {
      if (!element) return null;
      const rect = element.getBoundingClientRect();
      return {
        tag: element.tagName?.toLowerCase() || null,
        role: element.getAttribute?.('role') || null,
        ariaLabel: clean(element.getAttribute?.('aria-label')),
        testId: clean(element.getAttribute?.('data-testid')),
        name: clean(element.getAttribute?.('name')),
        type: clean(element.getAttribute?.('type')),
        text: element.type === 'password' ? '' : clean(element.innerText || element.textContent || element.value),
        disabled: Boolean(element.disabled || element.getAttribute?.('aria-disabled') === 'true'),
        expanded: element.getAttribute?.('aria-expanded') || null,
        checked: element.getAttribute?.('aria-checked') || null,
        selected: element.getAttribute?.('aria-selected') || null,
        x: Math.round(rect.x),
        y: Math.round(rect.y),
        width: Math.round(rect.width),
        height: Math.round(rect.height)
      };
    };
    const selector = [
      'button', 'a[href]', 'input', 'textarea', 'select',
      '[role]', '[contenteditable="true"]', '[data-testid]', '[aria-label]'
    ].join(',');
    return {
      url: location.origin + location.pathname,
      title: document.title,
      viewport: { width: innerWidth, height: innerHeight },
      activeElement: describe(document.activeElement),
      elements: [...document.querySelectorAll(selector)]
        .filter(visible)
        .slice(0, 300)
        .map(describe)
    };
  })()`;
}

async function captureDiagnosticNetwork(action, waitMs) {
  const debuggerClient = window.webContents.debugger;
  const alreadyAttached = debuggerClient.isAttached();
  const requests = [];
  const responses = new Map();
  const listener = (_event, method, params) => {
    if (method === 'Network.requestWillBeSent') {
      try {
        const parsed = new URL(params.request.url);
        requests.push({
          id: params.requestId,
          method: params.request.method,
          url: parsed.origin + parsed.pathname,
          resourceType: params.type || null
        });
      } catch {
        requests.push({
          id: params.requestId,
          method: params.request.method,
          url: null,
          resourceType: params.type || null
        });
      }
    } else if (method === 'Network.responseReceived') {
      responses.set(params.requestId, params.response.status);
    }
  };

  if (!alreadyAttached) debuggerClient.attach('1.3');
  debuggerClient.on('message', listener);
  try {
    await debuggerClient.sendCommand('Network.enable');
    await action();
    if (waitMs > 0) await sleep(waitMs);
    return requests.map(request => ({
      ...request,
      status: responses.get(request.id) ?? null
    }));
  } finally {
    debuggerClient.removeListener('message', listener);
    if (!alreadyAttached && debuggerClient.isAttached())
      debuggerClient.detach();
  }
}

async function invokeDiagnostic(operation, args) {
  const numericPoint = () => {
    const x = Number(args?.x);
    const y = Number(args?.y);
    if (!Number.isFinite(x) || !Number.isFinite(y))
      throw new Error('Diagnostic operation requires finite x and y coordinates.');
    return { x, y };
  };

  switch (operation) {
    case 'snapshot':
      return window.webContents.executeJavaScript(diagnosticElementSnapshot());

    case 'inspectPoint': {
      const { x, y } = numericPoint();
      return window.webContents.executeJavaScript(`(() => {
        const element = document.elementFromPoint(${JSON.stringify(x)}, ${JSON.stringify(y)});
        if (!element) return null;
        const rect = element.getBoundingClientRect();
        const clean = value => String(value ?? '').replace(/\\s+/g, ' ').trim().slice(0, 500);
        return {
          tag: element.tagName?.toLowerCase() || null,
          role: element.getAttribute?.('role') || null,
          ariaLabel: clean(element.getAttribute?.('aria-label')),
          testId: clean(element.getAttribute?.('data-testid')),
          text: element.type === 'password' ? '' : clean(element.innerText || element.textContent || element.value),
          x: Math.round(rect.x),
          y: Math.round(rect.y),
          width: Math.round(rect.width),
          height: Math.round(rect.height)
        };
      })()`);
    }

    case 'click': {
      const { x, y } = numericPoint();
      const waitMs = Math.max(0, Math.min(Number(args?.waitMs ?? 750), 10000));
      const before = await window.webContents.executeJavaScript(diagnosticElementSnapshot());
      const network = await captureDiagnosticNetwork(async () => {
        window.webContents.sendInputEvent({ type: 'mouseMove', x, y });
        window.webContents.sendInputEvent({ type: 'mouseDown', x, y, button: 'left', clickCount: 1 });
        window.webContents.sendInputEvent({ type: 'mouseUp', x, y, button: 'left', clickCount: 1 });
      }, waitMs);
      const after = await window.webContents.executeJavaScript(diagnosticElementSnapshot());
      return { before, after, network };
    }

    case 'type': {
      const text = String(args?.text ?? '');
      await window.webContents.insertText(text);
      return { typed: text.length };
    }

    case 'key': {
      const keyCode = String(args?.keyCode || '').trim();
      if (!keyCode) throw new Error('Diagnostic key operation requires keyCode.');
      const modifiers = Array.isArray(args?.modifiers)
        ? args.modifiers.map(value => String(value))
        : [];
      window.webContents.sendInputEvent({ type: 'keyDown', keyCode, modifiers });
      window.webContents.sendInputEvent({ type: 'keyUp', keyCode, modifiers });
      return { keyCode, modifiers };
    }

    case 'screenshot': {
      const image = await window.webContents.capturePage();
      const size = image.getSize();
      return {
        width: size.width,
        height: size.height,
        mimeType: 'image/png',
        base64: image.toPNG().toString('base64')
      };
    }

    default:
      throw new Error(`Unsupported browser diagnostic operation '${operation}'.`);
  }
}
function readJson(request) {
  return new Promise((resolve, reject) => {
    let body = "";
    request.setEncoding("utf8");
    request.on("data", chunk => body += chunk);
    request.on("end", () => {
      try { resolve(body ? JSON.parse(body) : {}); }
      catch (error) { reject(error); }
    });
    request.on("error", reject);
  });
}

function writeJson(response, status, value) {
  const body = JSON.stringify(value);
  response.writeHead(status, {
    "Content-Type": "application/json",
    "Content-Length": Buffer.byteLength(body)
  });
  response.end(body);
}

const providerOperations = new Map();
let operationQueue = Promise.resolve();

function queueProviderOperation(request) {
  const operationId = randomUUID();
  const state = {
    status: "queued",
    result: undefined,
    error: null,
    progress: null
  };
  providerOperations.set(operationId, state);

  operationQueue = operationQueue.then(async () => {
    state.status = "running";
    try {
      state.result = await invokeProvider(request, progress => {
        if (!progress || typeof progress !== "object") return;
        const progressState = String(progress.state || "").trim();
        if (!progressState) return;
        state.progress = {
          state: progressState,
          detail: progress.detail == null ? null : String(progress.detail),
          analysis: progress.analysis == null ? null : String(progress.analysis)
        };
      });
      state.status = "completed";
    } catch (error) {
      state.error = String(error?.stack ?? error);
      state.status = "failed";
    }
  });

  return operationId;
}

async function start() {
  const server = http.createServer(async (request, response) => {
    try {
      const requestUrl = new URL(request.url || "/", "http://127.0.0.1");
      if (request.method === "POST" && requestUrl.pathname === "/invoke") {
        const body = await readJson(request);
        const operationId = queueProviderOperation(body);
        writeJson(response, 202, { operationId });
      } else if (request.method === "GET" && requestUrl.pathname.startsWith("/invoke/")) {
        const operationId = decodeURIComponent(requestUrl.pathname.slice("/invoke/".length));
        const state = providerOperations.get(operationId);
        if (!state) {
          writeJson(response, 404, { error: "Provider operation was not found." });
          return;
        }
        if (state.status === "completed" || state.status === "failed")
          response.once("finish", () => providerOperations.delete(operationId));
        writeJson(response, 200, state);
      } else if (request.method === "POST" && requestUrl.pathname === "/show") {
        window?.show();
        window?.focus();
        writeJson(response, 200, { shown: true });
      } else if (request.method === "POST" && requestUrl.pathname === "/shutdown") {
        shuttingDown = true;
        activeSession?.flushStorageData();
        await activeSession?.cookies.flushStore();
        writeJson(response, 200, { stopped: true });
        server.close(() => app.quit());
      } else {
        writeJson(response, 404, { error: "Not found" });
      }
    } catch (error) {
      writeJson(response, 500, { ok: false, error: String(error?.stack ?? error) });
    }
  });

  server.listen(0, "127.0.0.1", async () => {
    try {
      await initialize({
        profileDirectory: process.env.MEZHS_PROFILE_DIRECTORY,
        showBrowser: process.env.MEZHS_SHOW_BROWSER === "1",
        modulePath: process.env.MEZHS_BROWSER_MODULE,
        requireAuthorization: process.env.MEZHS_REQUIRE_AUTHORIZATION === "1"
      });
      const address = server.address();
      process.stdout.write(`${JSON.stringify({ event: "ready", port: address.port })}\n`);
    } catch (error) {
      process.stdout.write(`${JSON.stringify({
        event: "error",
        code: error?.code || null,
        error: String(error?.stack ?? error)
      })}\n`);
      server.close(() => app.quit());
    }
  });
}

start();

app.on("window-all-closed", event => {
  if (!shuttingDown) event.preventDefault();
});
