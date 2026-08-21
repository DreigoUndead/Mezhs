const { app, BrowserWindow, ipcMain, session } = require("electron");
const { randomUUID } = require("node:crypto");
const fs = require("node:fs/promises");
const http = require("node:http");
const path = require("node:path");
const {
  cleanChromeUserAgent,
  configureSessionBrowserIdentity
} = require("./browser-identity");

let window = null;
let browserModule = null;
let activeSession = null;
let activeProfileDirectory = null;
let keepVisible = false;
let shuttingDown = false;
let networkCapture = null;
let networkLogWrite = Promise.resolve();
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

function isCapturedRequest(url) {
  try {
    const target = new URL(String(url || ""));
    const provider = new URL(browserModule.homeUrl);
    return target.origin === provider.origin &&
      (target.pathname.startsWith("/backend-api/") || target.pathname.startsWith("/api/"));
  } catch {
    return false;
  }
}

function sanitizeUrl(value) {
  try {
    const url = new URL(String(value));
    for (const key of [...url.searchParams.keys()]) {
      if (/token|proof|authorization|cookie|account[_-]?id|device[_-]?id|session[_-]?id/i.test(key))
        url.searchParams.set(key, "<redacted>");
    }
    return url.toString();
  } catch {
    return String(value || "");
  }
}

function sanitizeHeaders(headers) {
  const result = {};
  for (const [name, value] of Object.entries(headers || {})) {
    const sensitive = /authorization|cookie|account-id|device-id|session-id|sentinel.*token|conduit-token/i.test(name);
    result[name] = sensitive ? "<redacted>" : String(value);
  }
  return result;
}

function sanitizeJson(value, key = "") {
  if (/token|proof|authorization|cookie|account[_-]?id|device[_-]?id|session[_-]?id/i.test(key))
    return "<redacted>";
  if (Array.isArray(value))
    return value.map(item => sanitizeJson(item));
  if (value && typeof value === "object")
    return Object.fromEntries(Object.entries(value).map(([name, item]) => [name, sanitizeJson(item, name)]));
  return value;
}

function sanitizePostData(value) {
  if (!value) return null;
  const text = String(value);
  try {
    return sanitizeJson(JSON.parse(text));
  } catch {
    return text.length <= 20000 ? text : `${text.slice(0, 20000)}<truncated>`;
  }
}

function appendNetworkEntry(entry) {
  const capture = networkCapture;
  if (!capture) return;
  networkLogWrite = networkLogWrite
    .then(() => fs.appendFile(capture.path, `${JSON.stringify(entry)}\n`, "utf8"))
    .catch(error => console.error(`Network capture write failed: ${error}`));
}

function appendExtraHeaders(requestId, headers) {
  appendNetworkEntry({
    at: new Date().toISOString(),
    event: "request-extra-headers",
    requestId,
    headers: sanitizeHeaders(headers)
  });
}

async function startNetworkCapture() {
  if (networkCapture || !window || !browserModule || !activeProfileDirectory)
    return networkCapture?.path || null;

  const debuggerClient = window.webContents.debugger;
  let attachedHere = false;
  if (!debuggerClient.isAttached()) {
    debuggerClient.attach("1.3");
    attachedHere = true;
  }

  const logPath = path.join(path.dirname(activeProfileDirectory), "network-capture.jsonl");
  await fs.writeFile(logPath, "", "utf8");
  networkLogWrite = Promise.resolve();
  const capturedRequests = new Set();
  const pendingExtraHeaders = new Map();

  const onMessage = async (_event, method, params) => {
    try {
      if (method === "Network.requestWillBeSent") {
        const request = params?.request;
        const extraHeaders = pendingExtraHeaders.get(params.requestId);
        pendingExtraHeaders.delete(params.requestId);
        if (!isCapturedRequest(request?.url)) return;
        capturedRequests.add(params.requestId);
        let postData = request?.postData || null;
        if (!postData && request?.hasPostData) {
          try {
            postData = (await debuggerClient.sendCommand("Network.getRequestPostData", {
              requestId: params.requestId
            }))?.postData || null;
          } catch { }
        }
        appendNetworkEntry({
          at: new Date().toISOString(),
          event: "request",
          requestId: params.requestId,
          resourceType: params.type || null,
          method: request?.method || null,
          url: sanitizeUrl(request?.url),
          headers: sanitizeHeaders(request?.headers),
          postData: sanitizePostData(postData)
        });
        if (extraHeaders)
          appendExtraHeaders(params.requestId, extraHeaders);
      } else if (method === "Network.requestWillBeSentExtraInfo") {
        if (capturedRequests.has(params.requestId))
          appendExtraHeaders(params.requestId, params.headers);
        else
          pendingExtraHeaders.set(params.requestId, params.headers);
      } else if (method === "Network.responseReceived" && capturedRequests.has(params.requestId)) {
        appendNetworkEntry({
          at: new Date().toISOString(),
          event: "response",
          requestId: params.requestId,
          status: params.response?.status || null,
          url: sanitizeUrl(params.response?.url),
          headers: sanitizeHeaders(params.response?.headers)
        });
      } else if (method === "Network.loadingFailed" && capturedRequests.has(params.requestId)) {
        appendNetworkEntry({
          at: new Date().toISOString(),
          event: "failed",
          requestId: params.requestId,
          error: params.errorText || null,
          canceled: Boolean(params.canceled)
        });
      }
    } catch (error) {
      console.error(`Network capture event failed: ${error}`);
    }
  };

  debuggerClient.on("message", onMessage);
  try {
    await debuggerClient.sendCommand("Network.enable", { maxPostDataSize: 1024 * 1024 });
  } catch (error) {
    debuggerClient.removeListener("message", onMessage);
    if (attachedHere && debuggerClient.isAttached()) debuggerClient.detach();
    throw error;
  }

  networkCapture = { debuggerClient, attachedHere, onMessage, path: logPath };
  appendNetworkEntry({
    at: new Date().toISOString(),
    event: "capture-start",
    provider: browserModule.name,
    homeUrl: browserModule.homeUrl
  });
  console.error(`Network capture started: ${logPath}`);
  return logPath;
}

async function stopNetworkCapture() {
  const capture = networkCapture;
  networkCapture = null;
  if (!capture) return;

  capture.debuggerClient.removeListener("message", capture.onMessage);
  await capture.debuggerClient.sendCommand("Network.disable").catch(() => {});
  if (capture.attachedHere && capture.debuggerClient.isAttached())
    capture.debuggerClient.detach();
  await networkLogWrite.catch(() => {});
}

async function initialize({ profileDirectory, showBrowser, modulePath, requireAuthorization }) {
  await app.whenReady();
  browserModule = loadBrowserModule(modulePath);
  keepVisible = Boolean(showBrowser);
  activeProfileDirectory = path.resolve(profileDirectory);
  const persistentSession = session.fromPath(activeProfileDirectory);
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
  if (keepVisible && !requireAuthorization)
    await startNetworkCapture();
  if (!keepVisible) window.hide();
  return { ready: true };
}

function invokeProvider({ operation, arguments: args }) {
  if (!window || !browserModule || !activeSession)
    throw new Error("Electron browser is not initialized.");
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
    sleep
  });
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

let operationQueue = Promise.resolve();

async function start() {
  const server = http.createServer(async (request, response) => {
    try {
      if (request.method === "POST" && request.url === "/invoke") {
        const body = await readJson(request);
        const result = await (operationQueue = operationQueue
          .catch(() => {})
          .then(() => invokeProvider(body)));
        writeJson(response, 200, result);
      } else if (request.method === "POST" && request.url === "/show") {
        const networkLog = await startNetworkCapture();
        window?.show();
        window?.focus();
        writeJson(response, 200, { shown: true, networkLog });
      } else if (request.method === "POST" && request.url === "/shutdown") {
        shuttingDown = true;
        await stopNetworkCapture();
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
