const { app, BrowserWindow, ipcMain, session } = require("electron");
const { randomUUID } = require("node:crypto");
const http = require("node:http");
const path = require("node:path");
const {
  cleanChromeUserAgent,
  configureSessionBrowserIdentity
} = require("./browser-identity");
const { invokeDiagnostic } = require("./diagnostics");

let browserModule = null;
let authenticatedSession = null;
let management = null;
let keepVisible = false;
let shuttingDown = false;
const guest = process.env.MEZHS_GUEST === "1";
const contexts = new Map(); // local ChatId -> Promise<context>
const providerOperations = new Map();
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
    try { process.kill(parentProcessId, 0); }
    catch {
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

function invokePageOperation(targetWindow, operation, args, timeoutMs = 0) {
  const responseChannel = `mezhs:page-operation-result:${randomUUID()}`;
  return new Promise((resolve, reject) => {
    const contents = targetWindow.webContents;
    let timer;
    const cleanup = () => {
      ipcMain.removeListener(responseChannel, onResponse);
      contents.removeListener("did-start-navigation", onNavigation);
      contents.removeListener("render-process-gone", onGone);
      contents.removeListener("destroyed", onGone);
      clearTimeout(timer);
    };
    const fail = reason => {
      cleanup();
      reject(new Error(reason));
    };
    const onResponse = (_event, response) => {
      cleanup();
      if (response?.ok) resolve(response.result);
      else reject(new Error(response?.error || `Browser page operation '${operation}' failed.`));
    };
    const onNavigation = (_event, details, isInPlace, isMainFrame) => {
      const inPlace = typeof details === "object" ? details?.isInPlace : isInPlace;
      const mainFrame = typeof details === "object" ? details?.isMainFrame : isMainFrame;
      if (mainFrame !== false && !inPlace)
        fail(`Browser navigated during '${operation}'; outcome is uncertain.`);
    };
    const onGone = () => fail(`Browser renderer disappeared during '${operation}'; outcome is uncertain.`);
    ipcMain.on(responseChannel, onResponse);
    contents.on("did-start-navigation", onNavigation);
    contents.on("render-process-gone", onGone);
    contents.on("destroyed", onGone);
    if (timeoutMs > 0)
      timer = setTimeout(() => fail(`Browser page operation '${operation}' timed out; outcome is uncertain.`), timeoutMs);
    try {
      if (contents.isDestroyed?.()) return onGone();
      contents.send("mezhs:page-operation", { responseChannel, operation, args: args ?? {} });
    } catch (error) {
      cleanup();
      reject(error);
    }
  });
}

async function createWindow(browserSession, visible, isManagement = false) {
  const target = new BrowserWindow({
    width: 1200,
    height: 850,
    show: visible,
    title: `MEŽS - ${browserModule.name}`,
    webPreferences: {
      session: browserSession,
      preload: browserPreload,
      contextIsolation: true,
      sandbox: false,
      backgroundThrottling: false
    }
  });
  target.webContents.setWindowOpenHandler(() => ({
    action: "allow",
    overrideBrowserWindowOptions: {
      webPreferences: {
        session: browserSession,
        preload: browserPreload,
        contextIsolation: true,
        sandbox: false
      }
    }
  }));
  if (isManagement) {
    target.on("close", event => {
      if (shuttingDown) return;
      event.preventDefault();
      target.hide();
    });
  }
  target.webContents.on("did-fail-load", (_event, code, description, url) => {
    console.error(`Navigation failed (${code} ${description}): ${url}`);
  });
  target.webContents.on("console-message", (_event, level, message, line, sourceId) => {
    if (level >= 2)
      console.error(`${browserModule.name} renderer: ${message} (${sourceId}:${line})`);
  });
  target.once("ready-to-show", () => {
    if (visible && !target.isDestroyed()) {
      target.show();
      target.focus();
    }
  });
  try {
    await target.loadURL(browserModule.homeUrl);
    if (typeof browserModule.afterInitialize === "function")
      await browserModule.afterInitialize({ window: target, session: browserSession, sleep });
    if (!visible && !target.isDestroyed()) target.hide();
    return target;
  } catch (error) {
    if (!target.isDestroyed()) target.destroy();
    throw error;
  }
}

function closeContext(context) {
  if (!context || context.closed) return;
  context.closed = true;
  if (!context.window.isDestroyed()) context.window.destroy();
}

async function createContext(ephemeral) {
  const browserSession = ephemeral
    ? session.fromPartition(`mezhs-guest-${randomUUID()}`) // no "persist:" prefix
    : authenticatedSession;
  if (!browserSession)
    throw new Error("Authenticated browser session is unavailable.");
  if (ephemeral)
    configureSessionBrowserIdentity(browserSession);
  const target = await createWindow(browserSession, false);
  return { window: target, session: browserSession, ephemeral, busy: 0, retire: false, closed: false };
}

function contextPromise(contextId, ephemeral) {
  if (!contextId) {
    if (!management) throw new Error("Browser management context is unavailable.");
    return Promise.resolve(management);
  }
  if (ephemeral) return createContext(true);
  if (guest) throw new Error("Guest requests must use an ephemeral browser context.");
  let pending = contexts.get(contextId);
  if (!pending) {
    pending = createContext(false);
    contexts.set(contextId, pending);
    const current = pending;
    void pending.catch(() => {
      if (contexts.get(contextId) === current) contexts.delete(contextId);
    });
  }
  return pending;
}

function discardContext(contextId, pending) {
  if (contextId && contexts.get(contextId) === pending)
    contexts.delete(contextId);
  // This also handles cancellation while a window is still navigating.
  void pending.then(closeContext).catch(() => {});
}

function releaseContext(contextId) {
  const pending = contexts.get(contextId);
  if (!pending) return;
  contexts.delete(contextId); // A later explicit send gets a new context.
  void pending.then(context => {
    context.retire = true;
    if (context.busy === 0) closeContext(context);
  }).catch(() => {});
}

async function initialize({ profileDirectory, showBrowser, modulePath, requireAuthorization }) {
  await app.whenReady();
  browserModule = loadBrowserModule(modulePath);
  keepVisible = Boolean(showBrowser);
  if (!guest) {
    authenticatedSession = session.fromPath(path.resolve(profileDirectory));
    configureSessionBrowserIdentity(authenticatedSession);
    const window = await createWindow(authenticatedSession, keepVisible, true);
    management = { window, session: authenticatedSession, busy: 0, closed: false };
    if (requireAuthorization) {
      while (!(await checkAuthorization(keepVisible))) {
        if (!keepVisible) {
          const error = new Error(`${browserModule.name} authorization is required.`);
          error.code = "authorization_required";
          throw error;
        }
        await sleep(1000);
      }
    }
  }
  return { ready: true };
}

async function checkAuthorization(interactive) {
  if (!management || typeof browserModule.isAuthorized !== "function")
    throw new Error(`${browserModule.name} does not support authorization.`);
  const authorized = await browserModule.isAuthorized(management.window);
  if (authorized) {
    await authenticatedSession.flushStorageData();
    await authenticatedSession.cookies.flushStore();
  } else if (interactive) {
    management.window.show();
    management.window.focus();
  }
  return authorized;
}

function invokeProvider(context, { operation, arguments: args }, reportProgress) {
  if (!browserModule || !context || context.closed)
    throw new Error("Electron browser is not initialized.");
  const { window, session: browserSession } = context;
  if (String(operation || "").startsWith("$diagnostics/"))
    return invokeDiagnostic(String(operation).slice("$diagnostics/".length), args ?? {},
      { window, browserModule, sleep });
  const method = browserModule.operations[operation];
  if (typeof method !== "function")
    throw new Error(`${browserModule.name} does not support provider operation '${operation}'.`);
  return method({
    window,
    session: browserSession,
    page: {
      invoke: (pageOperation, pageArgs, timeoutMs = 0) =>
        invokePageOperation(window, pageOperation, pageArgs, timeoutMs)
    },
    args: args ?? {},
    sleep,
    reportProgress
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

function queueProviderOperation(request) {
  const operationId = randomUUID();
  const contextId = typeof request.contextId === "string" && request.contextId.trim()
    ? request.contextId : null;
  const ephemeral = request.ephemeral === true;
  const state = {
    status: "queued", result: undefined, error: null, code: null, progress: null,
    cancelled: false, contextId, ephemeral, pending: null
  };
  providerOperations.set(operationId, state);
  void (async () => {
    let context;
    try {
      state.status = "running";
      state.pending = contextPromise(contextId, ephemeral);
      context = await state.pending;
      if (state.cancelled || context.closed) return;
      context.busy++;
      try {
        state.result = await invokeProvider(context, request, progress => {
          if (state.cancelled || !progress || typeof progress !== "object") return;
          const progressState = String(progress.state || "").trim();
          if (!progressState) return;
          state.progress = {
            state: progressState,
            detail: progress.detail == null ? null : String(progress.detail),
            analysis: progress.analysis == null ? null : String(progress.analysis)
          };
        });
        if (!state.cancelled) state.status = "completed";
      } finally {
        context.busy--;
        if (ephemeral) closeContext(context);
        else if (context.retire && context.busy === 0)
          discardContext(contextId, state.pending);
      }
    } catch (error) {
      if (!state.cancelled) {
        state.error = String(error?.stack ?? error);
        state.code = typeof error?.code === "string" ? error.code : null;
        state.status = "failed";
      }
    } finally {
      if (state.cancelled) providerOperations.delete(operationId);
      else {
        // Retire unpolled terminal results without imposing a timeout on model work.
        setTimeout(() => providerOperations.delete(operationId), 10 * 60 * 1000).unref();
      }
    }
  })();
  return operationId;
}

function cancelProviderOperation(operationId) {
  const state = providerOperations.get(operationId);
  if (!state) return false;
  state.cancelled = true;
  providerOperations.delete(operationId);
  if (state.pending) {
    // Only the cancelled operation's browser context is destroyed. Never stop the shared host.
    if (state.ephemeral) void state.pending.then(closeContext).catch(() => {});
    else if (state.contextId) discardContext(state.contextId, state.pending);
  }
  return true;
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
        writeJson(response, 200, {
          status: state.status, result: state.result, error: state.error,
          code: state.code, progress: state.progress
        });
      } else if (request.method === "POST" && /^\/invoke\/[^/]+\/cancel$/.test(requestUrl.pathname)) {
        const operationId = decodeURIComponent(requestUrl.pathname.slice(8, -7));
        writeJson(response, 200, { cancelled: cancelProviderOperation(operationId) });
      } else if (request.method === "POST" && requestUrl.pathname === "/contexts/release") {
        const body = await readJson(request);
        if (typeof body.contextId !== "string" || !body.contextId.trim())
          throw new Error("A contextId is required.");
        releaseContext(body.contextId);
        writeJson(response, 200, { released: true });
      } else if (request.method === "POST" && requestUrl.pathname === "/authorize") {
        const body = await readJson(request);
        writeJson(response, 200, { authorized: await checkAuthorization(body.interactive === true) });
      } else if (request.method === "POST" && requestUrl.pathname === "/show") {
        management?.window.show();
        management?.window.focus();
        writeJson(response, 200, { shown: Boolean(management) });
      } else if (request.method === "POST" && requestUrl.pathname === "/shutdown") {
        shuttingDown = true;
        await authenticatedSession?.flushStorageData();
        await authenticatedSession?.cookies.flushStore();
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
